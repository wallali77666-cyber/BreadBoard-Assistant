// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/ElectricalRulesChecker.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Sub-millisecond deterministic Netlist Builder & KiCad ERC/DRC Checker.
// PERFORMANCE REQUIREMENT: Must execute in < 0.1 ms on E-Core 18.
// THREADING: Single-threaded reflex loop only. The checker owns reusable
//            Union-Find / occupancy buffers and is NOT thread-safe.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Builds the electrical netlist from snapped component leads with a Union-Find
/// structure and applies the four mandatory KiCad-style ERC/DRC rules:
/// <list type="number">
///   <item><description><c>ErcVccGndDirectShort (100)</c> — critical VCC↔GND bridge.</description></item>
///   <item><description><c>ErcSameStripShort (101)</c> — both leads on one metal clip, with a corrective target hole.</description></item>
///   <item><description><c>ErcFloatingLead (102)</c> — a terminal on a net with exactly one connected terminal.</description></item>
///   <item><description><c>DrcSchematicNetMismatch (200)</c> — live connectivity diverges from the target netlist.</description></item>
/// </list>
/// </summary>
public sealed class ElectricalRulesChecker
{
    // Reserved Strip IDs for power rails (mirrors BreadboardSpec).
    public const int StripTopVcc = BreadboardSpec.StripTopVcc;
    public const int StripTopGnd = BreadboardSpec.StripTopGnd;
    public const int StripBotVcc = BreadboardSpec.StripBotVcc;
    public const int StripBotGnd = BreadboardSpec.StripBotGnd;

    private readonly int[] _parentStrip = new int[BreadboardSpec.StripCount];
    private readonly bool[] _stripOccupied = new bool[BreadboardSpec.StripCount];
    private readonly int[] _stripDegree = new int[BreadboardSpec.StripCount];
    private readonly PinMatrixLookup _pinMatrix;

    public ElectricalRulesChecker()
        : this(PinMatrixLookup.Shared)
    {
    }

    public ElectricalRulesChecker(PinMatrixLookup pinMatrix)
    {
        _pinMatrix = pinMatrix ?? throw new ArgumentNullException(nameof(pinMatrix));
    }

    /// <summary>Geometry used for corrective-hole suggestions and target-net resolution.</summary>
    public PinMatrixLookup PinMatrix => _pinMatrix;

    /// <summary>
    /// Evaluates one camera frame using the production <see cref="PinMatrixLookup"/>.
    /// </summary>
    public DrcStatePacket EvaluateBoard(
        long frameId,
        double yoloMs,
        ReadOnlySpan<DetectedComponent> rawDetections,
        TargetSchematic? target = null)
    {
        return EvaluateBoardCore(frameId, yoloMs, rawDetections, new PinMatrixSnapper(_pinMatrix), target);
    }

    /// <summary>
    /// Compatibility overload for camera back-ends that expose their own snapping delegate.
    /// Corrective-hole search still uses <see cref="PinMatrix"/>.
    /// </summary>
    public DrcStatePacket EvaluateBoard(
        long frameId,
        double yoloMs,
        ReadOnlySpan<DetectedComponent> rawDetections,
        Func<float, float, (bool Found, HoleCoord Hole)> snapLookup)
    {
        ArgumentNullException.ThrowIfNull(snapLookup);
        return EvaluateBoardCore(frameId, yoloMs, rawDetections, new DelegateSnapper(snapLookup), target: null);
    }

    private DrcStatePacket EvaluateBoardCore<TSnapper>(
        long frameId,
        double yoloMs,
        ReadOnlySpan<DetectedComponent> rawDetections,
        TSnapper snapper,
        TargetSchematic? target)
        where TSnapper : struct, IHoleSnapper
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        ResetState();

        var snapped = new List<SnappedComponent>(rawDetections.Length);
        var violations = new List<DrcViolation>(4);

        // ------------------------------------------------------------------
        // Pass 1: snap every lead to the 830-point matrix and union jumper nets.
        // ------------------------------------------------------------------
        foreach (ref readonly DetectedComponent detection in rawDetections)
        {
            bool found1 = snapper.TrySnap(detection.Lead1Px, detection.Lead1Py, out HoleCoord hole1);
            bool found2 = snapper.TrySnap(detection.Lead2Px, detection.Lead2Py, out HoleCoord hole2);

            if (found1 && IsValidStrip(hole1.StripId))
                _stripOccupied[hole1.StripId] = true;
            if (found2 && IsValidStrip(hole2.StripId))
                _stripOccupied[hole2.StripId] = true;

            if (!found1 || !found2)
                continue;

            if (!IsValidStrip(hole1.StripId) || !IsValidStrip(hole2.StripId))
                continue;

            if (detection.Kind == ComponentKind.JumperWire)
                UnionStrips(hole1.StripId, hole2.StripId);

            snapped.Add(new SnappedComponent(detection, hole1, hole2, string.Empty, string.Empty));
        }

        // ------------------------------------------------------------------
        // Rule 1: VCC-to-GND direct bridge (top/bottom rail pairings).
        // ------------------------------------------------------------------
        int rootTopVcc = FindRootNet(StripTopVcc);
        int rootTopGnd = FindRootNet(StripTopGnd);
        int rootBotVcc = FindRootNet(StripBotVcc);
        int rootBotGnd = FindRootNet(StripBotGnd);

        bool vccShortedToGnd =
            rootTopVcc == rootTopGnd ||
            rootTopVcc == rootBotGnd ||
            rootBotVcc == rootTopGnd ||
            rootBotVcc == rootBotGnd;

        if (vccShortedToGnd)
        {
            HoleCoord badHole = _pinMatrix.TryGetRailHole(StripTopVcc, 1, out HoleCoord railHole)
                ? railHole
                : new HoleCoord("VCC_RAIL", StripTopVcc, BreadboardSpec.RailFirstX, BreadboardSpec.TopVccRailY);

            violations.Add(new DrcViolation(
                Code: ViolationCode.ErcVccGndDirectShort,
                Severity: DrcSeverity.Critical,
                ComponentId: "POWER_RAILS",
                OffendingStripId: StripTopVcc,
                BadHole: badHole,
                SuggestedTargetHole: default,
                TechnicalMessage: "CRITICAL SHORT: a jumper bridges the VCC rail directly to the GND rail. " +
                                  "Remove the bridge before applying power."));
        }

        // ------------------------------------------------------------------
        // Pass 2: resolve net names and count terminals per net.
        // ------------------------------------------------------------------
        for (int i = 0; i < snapped.Count; i++)
        {
            SnappedComponent component = snapped[i];
            int net1 = FindRootNet(component.Pin1Hole.StripId);
            int net2 = FindRootNet(component.Pin2Hole.StripId);

            snapped[i] = component with
            {
                Net1Name = FormatNetName(net1),
                Net2Name = FormatNetName(net2)
            };

            // Jumpers are wires, not terminals: they must not satisfy floating-lead counts.
            if (component.RawDetection.Kind == ComponentKind.JumperWire)
                continue;

            _stripDegree[net1]++;
            _stripDegree[net2]++;
        }

        // ------------------------------------------------------------------
        // Rules 2 + 3: same-strip short and floating lead.
        // ------------------------------------------------------------------
        for (int i = 0; i < snapped.Count; i++)
        {
            SnappedComponent component = snapped[i];
            if (component.RawDetection.Kind == ComponentKind.JumperWire)
                continue;

            int net1 = FindRootNet(component.Pin1Hole.StripId);
            int net2 = FindRootNet(component.Pin2Hole.StripId);

            if (net1 == net2)
            {
                HoleCoord suggestedTarget = _pinMatrix.TryFindNearestFreeHoleInRow(
                    component.Pin2Hole,
                    _stripOccupied,
                    out HoleCoord freeHole)
                    ? freeHole
                    : default;

                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcSameStripShort,
                    Severity: DrcSeverity.Critical,
                    ComponentId: $"{component.RawDetection.Kind}_{component.RawDetection.TrackId}",
                    OffendingStripId: component.Pin2Hole.StripId,
                    BadHole: component.Pin2Hole,
                    SuggestedTargetHole: suggestedTarget,
                    TechnicalMessage: $"SHORT: both leads of {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                                      $"({component.Pin1Hole.HoleId} & {component.Pin2Hole.HoleId}) sit on the same metal clip " +
                                      $"({component.Net1Name}). Move one lead to " +
                                      (string.IsNullOrEmpty(suggestedTarget.HoleId) ? "an adjacent strip." : suggestedTarget.HoleId + ".")));
            }

            if (IsFloatingNet(net1))
            {
                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcFloatingLead,
                    Severity: DrcSeverity.Warning,
                    ComponentId: $"{component.RawDetection.Kind}_{component.RawDetection.TrackId}",
                    OffendingStripId: component.Pin1Hole.StripId,
                    BadHole: component.Pin1Hole,
                    SuggestedTargetHole: default,
                    TechnicalMessage: $"FLOATING LEAD: {component.Pin1Hole.HoleId} of " +
                                      $"{component.RawDetection.Kind} #{component.RawDetection.TrackId} is the only terminal on " +
                                      $"{FormatNetName(net1)}. Connect it to the target net."));
            }

            if (net2 != net1 && IsFloatingNet(net2))
            {
                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcFloatingLead,
                    Severity: DrcSeverity.Warning,
                    ComponentId: $"{component.RawDetection.Kind}_{component.RawDetection.TrackId}",
                    OffendingStripId: component.Pin2Hole.StripId,
                    BadHole: component.Pin2Hole,
                    SuggestedTargetHole: default,
                    TechnicalMessage: $"FLOATING LEAD: {component.Pin2Hole.HoleId} of " +
                                      $"{component.RawDetection.Kind} #{component.RawDetection.TrackId} is the only terminal on " +
                                      $"{FormatNetName(net2)}. Connect it to the target net."));
            }
        }

        // ------------------------------------------------------------------
        // Rule 4: target schematic parity.
        // ------------------------------------------------------------------
        if (target is not null)
            CheckSchematicParity(target, violations);

        double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        return new DrcStatePacket
        {
            FrameNumber = frameId,
            YoloInferenceMs = yoloMs,
            DrcComputeMs = elapsedMs,
            StateHash = ComputeTopologyHash(snapped, violations),
            Components = snapped,
            Violations = violations
        };
    }

    /// <summary>Repairs the live Union-Find state after the most recent <c>EvaluateBoard</c>.</summary>
    public int GetRootNet(int stripId) =>
        IsValidStrip(stripId) ? FindRootNet(stripId) : -1;

    /// <summary>Resolved KiCad-style net name for a strip (e.g. <c>NET_VCC</c>, <c>NET_115</c>).</summary>
    public string GetResolvedNetName(int stripId) =>
        IsValidStrip(stripId) ? FormatNetName(FindRootNet(stripId)) : string.Empty;

    public bool AreSameNet(int stripIdA, int stripIdB) =>
        IsValidStrip(stripIdA) && IsValidStrip(stripIdB) && FindRootNet(stripIdA) == FindRootNet(stripIdB);

    private void CheckSchematicParity(TargetSchematic target, List<DrcViolation> violations)
    {
        foreach (TargetNet net in target.Nets)
        {
            int anchorRoot = -1;
            HoleCoord anchor = default;

            foreach (string holeId in net.Holes)
            {
                if (!_pinMatrix.TryGetHole(holeId, out HoleCoord hole))
                    continue;

                if (!IsValidStrip(hole.StripId) || !_stripOccupied[hole.StripId])
                    continue; // only validate holes that actually have a lead in them

                int root = FindRootNet(hole.StripId);

                if (anchorRoot < 0)
                {
                    anchorRoot = root;
                    anchor = hole;
                    continue;
                }

                if (root == anchorRoot)
                    continue;

                violations.Add(new DrcViolation(
                    Code: ViolationCode.DrcSchematicNetMismatch,
                    Severity: DrcSeverity.Warning,
                    ComponentId: net.Name,
                    OffendingStripId: hole.StripId,
                    BadHole: hole,
                    SuggestedTargetHole: anchor,
                    TechnicalMessage: $"SCHEMATIC MISMATCH: {hole.HoleId} resolves to {FormatNetName(root)} but the target " +
                                      $"net '{net.Name}' requires it common with {anchor.HoleId} " +
                                      $"({FormatNetName(anchorRoot)})."));

                break; // one violation per target net keeps the inspector readable
            }
        }
    }

    private bool IsFloatingNet(int rootNet)
    {
        // Power rails are externally driven; a rail with one terminal is not floating.
        if (rootNet is >= StripTopVcc and <= StripBotGnd)
            return false;

        return rootNet >= 0
            && rootNet < _stripDegree.Length
            && _stripDegree[rootNet] == 1;
    }

    private void ResetState()
    {
        for (int i = 0; i < _parentStrip.Length; i++)
            _parentStrip[i] = i;

        Array.Clear(_stripOccupied);
        Array.Clear(_stripDegree);
    }

    private static bool IsValidStrip(int stripId) => (uint)stripId < (uint)BreadboardSpec.StripCount;

    private int FindRootNet(int stripId)
    {
        while (_parentStrip[stripId] != stripId)
        {
            _parentStrip[stripId] = _parentStrip[_parentStrip[stripId]]; // path compression
            stripId = _parentStrip[stripId];
        }

        return stripId;
    }

    private void UnionStrips(int stripA, int stripB)
    {
        int rootA = FindRootNet(stripA);
        int rootB = FindRootNet(stripB);
        if (rootA == rootB)
            return;

        // Always keep the lowest ID as root so power rails (1..4) stay canonical,
        // making any jumper chain that reaches a rail resolve directly to NET_VCC/NET_GND.
        if (rootA < rootB)
            _parentStrip[rootB] = rootA;
        else
            _parentStrip[rootA] = rootB;
    }

    private static string FormatNetName(int rootId) => rootId switch
    {
        StripTopVcc or StripBotVcc => "NET_VCC",
        StripTopGnd or StripBotGnd => "NET_GND",
        _ => $"NET_{rootId:D3}"
    };

    private static int ComputeTopologyHash(List<SnappedComponent> components, List<DrcViolation> violations)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (SnappedComponent component in components)
        {
            hash = (hash ^ (byte)component.RawDetection.Kind) * prime;
            hash = (hash ^ (uint)component.Pin1Hole.StripId) * prime;
            hash = (hash ^ (uint)component.Pin2Hole.StripId) * prime;
        }

        hash = (hash ^ (uint)violations.Count) * prime;
        foreach (DrcViolation violation in violations)
            hash = (hash ^ (uint)violation.Code) * prime;

        return unchecked((int)(hash ^ (hash >> 32)));
    }

    private readonly struct PinMatrixSnapper : IHoleSnapper
    {
        private readonly PinMatrixLookup _lookup;

        public PinMatrixSnapper(PinMatrixLookup lookup) => _lookup = lookup;

        public bool TrySnap(float px, float py, out HoleCoord hole) => _lookup.TrySnapToHole(px, py, out hole);
    }

    private readonly struct DelegateSnapper : IHoleSnapper
    {
        private readonly Func<float, float, (bool Found, HoleCoord Hole)> _snap;

        public DelegateSnapper(Func<float, float, (bool Found, HoleCoord Hole)> snap) => _snap = snap;

        public bool TrySnap(float px, float py, out HoleCoord hole)
        {
            (bool found, HoleCoord snapped) = _snap(px, py);
            hole = snapped;
            return found;
        }
    }
}
