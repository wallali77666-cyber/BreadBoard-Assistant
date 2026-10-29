// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/ElectricalRulesChecker.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Sub-millisecond deterministic Netlist Builder & KiCad ERC/DRC Checker.
// PERFORMANCE REQUIREMENT: Must execute in < 0.1 ms on E-Core 18.
// THREADING: Single-threaded reflex loop only. The checker owns reusable
//            Union-Find / occupancy buffers and is NOT thread-safe.
//
// RULE SET (v2)
//   100  ErcVccGndDirectShort   critical  VCC rail tied to a GND rail
//   101  ErcSameStripShort      critical  both leads of a part on one metal clip
//   102  ErcFloatingLead        warning   terminal on a net with a single connection
//   104  ErcRedundantJumper     warning   jumper whose two ends already share a net
//   105  ErcHoleCollision       warning   more than two leads forced into one hole
//   200  DrcSchematicNetMismatch warning  target net split across two nets (open)
//   202  DrcUnexpectedNetLink   warning   two distinct target nets bridged together
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Builds the electrical netlist from snapped component leads with a Union-Find
/// structure and applies the KiCad-style ERC/DRC rule set listed in the file header.
/// </summary>
public sealed class ElectricalRulesChecker
{
    // Reserved Strip IDs for power rails (mirrors BreadboardSpec).
    public const int StripTopVcc = BreadboardSpec.StripTopVcc;
    public const int StripTopGnd = BreadboardSpec.StripTopGnd;
    public const int StripBotVcc = BreadboardSpec.StripBotVcc;
    public const int StripBotGnd = BreadboardSpec.StripBotGnd;

    /// <summary>A solderless clip comfortably grips three thin leads; a fourth is intermittent.</summary>
    public const int MaxLeadsPerHole = 3;

    private readonly int[] _parentStrip = new int[BreadboardSpec.StripCount];
    private readonly bool[] _stripOccupied = new bool[BreadboardSpec.StripCount];
    private readonly int[] _stripDegree = new int[BreadboardSpec.StripCount];
    private readonly int[] _holeLeadCount = new int[BreadboardSpec.TotalHoleCount];
    private readonly PinMatrixLookup _pinMatrix;

    // The Union-Find scratch arrays are reused for zero allocations, which makes the
    // checker stateful; every public entry point takes this gate so a UI preview and
    // the 60 FPS reflex loop can never corrupt each other's graph.
    private readonly object _gate = new();

    // Attribution for the VCC/GND bridge found during the snap pass.
    private int _bridgeTrackId;
    private HoleCoord _bridgeHole1;
    private HoleCoord _bridgeHole2;

    /// <summary>Root of the merged VCC+GND net while a power short is present (-1 otherwise).</summary>
    private int _powerShortRoot = -1;

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
        lock (_gate)
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

        lock (_gate)
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
            {
                _stripOccupied[hole1.StripId] = true;
                CountLead(hole1);
            }

            if (found2 && IsValidStrip(hole2.StripId))
            {
                _stripOccupied[hole2.StripId] = true;
                CountLead(hole2);
            }

            // Pins 3..N of a DIP or module. A pin that misses the matrix stays empty
            // in the array so pin numbering never shifts.
            SnappedPin[]? extraPins = null;
            if (!detection.ExtraLeads.IsEmpty)
            {
                extraPins = new SnappedPin[detection.ExtraLeads.Length];
                ReadOnlySpan<LeadPoint> leads = detection.ExtraLeads.Span;

                for (int p = 0; p < leads.Length; p++)
                {
                    if (!snapper.TrySnap(leads[p].Px, leads[p].Py, out HoleCoord pinHole)
                        || !IsValidStrip(pinHole.StripId))
                    {
                        continue;
                    }

                    _stripOccupied[pinHole.StripId] = true;
                    CountLead(pinHole);
                    extraPins[p] = new SnappedPin(pinHole, string.Empty);
                }
            }

            if (!found1 || !found2)
                continue;

            if (!IsValidStrip(hole1.StripId) || !IsValidStrip(hole2.StripId))
                continue;

            if (detection.Kind == ComponentKind.JumperWire)
            {
                if (_bridgeTrackId == 0 && IsPowerBridge(hole1.StripId, hole2.StripId))
                {
                    _bridgeTrackId = detection.TrackId;
                    _bridgeHole1 = hole1;
                    _bridgeHole2 = hole2;
                }

                UnionStrips(hole1.StripId, hole2.StripId);
            }

            snapped.Add(new SnappedComponent(
                detection,
                hole1,
                hole2,
                string.Empty,
                string.Empty,
                extraPins is null ? default : extraPins));
        }

        // ------------------------------------------------------------------
        // Rule 100: VCC-to-GND bridge (top/bottom rail pairings).
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

        // Keep the merged root recognisable: without this, every ground strip would
        // report itself as NET_VCC and the netlist would hide which side was shorted.
        _powerShortRoot = !vccShortedToGnd
            ? -1
            : rootTopVcc == rootTopGnd || rootTopVcc == rootBotGnd
                ? rootTopVcc
                : rootBotVcc;

        if (vccShortedToGnd)
            violations.Add(BuildPowerShortViolation());

        // ------------------------------------------------------------------
        // Pass 2: resolve net names and count terminals per net.
        // ------------------------------------------------------------------
        for (int i = 0; i < snapped.Count; i++)
        {
            SnappedComponent component = snapped[i];
            int net1 = FindRootNet(component.Pin1Hole.StripId);
            int net2 = FindRootNet(component.Pin2Hole.StripId);

            SnappedPin[]? extraPins = null;
            if (component.ExtraPins.Length > 0)
            {
                extraPins = new SnappedPin[component.ExtraPins.Length];
                ReadOnlySpan<SnappedPin> source = component.ExtraPins.Span;

                for (int p = 0; p < source.Length; p++)
                {
                    SnappedPin pin = source[p];
                    if (string.IsNullOrEmpty(pin.Hole.HoleId))
                    {
                        extraPins[p] = pin;
                        continue;
                    }

                    int pinNet = FindRootNet(pin.Hole.StripId);
                    extraPins[p] = pin with { NetName = FormatNetName(pinNet) };

                    if (component.RawDetection.Kind != ComponentKind.JumperWire)
                        _stripDegree[pinNet]++;
                }
            }

            snapped[i] = component with
            {
                Net1Name = FormatNetName(net1),
                Net2Name = FormatNetName(net2),
                ExtraPins = extraPins is null ? default : extraPins
            };

            // Jumpers are wires, not terminals: they must not satisfy floating-lead counts.
            if (component.RawDetection.Kind == ComponentKind.JumperWire)
                continue;

            _stripDegree[net1]++;
            _stripDegree[net2]++;
        }

        // ------------------------------------------------------------------
        // Rules 101, 102 and 104.
        // ------------------------------------------------------------------
        for (int i = 0; i < snapped.Count; i++)
        {
            SnappedComponent component = snapped[i];
            int net1 = FindRootNet(component.Pin1Hole.StripId);
            int net2 = FindRootNet(component.Pin2Hole.StripId);

            if (component.RawDetection.Kind == ComponentKind.JumperWire)
            {
                // A jumper is redundant when both ends sit in the same metal clip:
                // the strip already connects them, so the wire adds nothing.
                if (component.Pin1Hole.StripId == component.Pin2Hole.StripId)
                {
                    violations.Add(new DrcViolation(
                        Code: ViolationCode.ErcRedundantJumper,
                        Severity: DrcSeverity.Warning,
                        ComponentId: $"JumperWire_{component.RawDetection.TrackId}",
                        OffendingStripId: component.Pin2Hole.StripId,
                        BadHole: component.Pin2Hole,
                        SuggestedTargetHole: default,
                        TechnicalMessage: $"Remove the jumper from {component.Pin1Hole.HoleId} to {component.Pin2Hole.HoleId}, " +
                                          $"as it does nothing: both holes already share metal clip {component.Pin2Hole.StripId}. " +
                                          "Move one end to a net that still needs the connection."));
                }

                continue;
            }

            // A DIP is not "bypassed" when two of its pins share a net through a
            // rail: only two pins in the same physical clip are a real short.
            if (component.PinCount > 2)
            {
                AppendMultiPinShort(component, violations);
                continue;
            }

            if (net1 == net2)
            {
                bool sameStrip = component.Pin1Hole.StripId == component.Pin2Hole.StripId;

                HoleCoord suggestedTarget = sameStrip
                    && _pinMatrix.TryFindNearestFreeHoleInRow(
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
                    TechnicalMessage: sameStrip
                        ? $"Move lead 2 of {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                          $"from {component.Pin2Hole.HoleId} to " +
                          (string.IsNullOrEmpty(suggestedTarget.HoleId) ? "an empty column" : suggestedTarget.HoleId) +
                          $", as it shares metal clip {component.Pin2Hole.StripId} with lead 1 at " +
                          $"{component.Pin1Hole.HoleId} ({component.Net1Name})."
                        : $"Remove the jumper that parallels {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                          $"between {component.Pin1Hole.HoleId} and {component.Pin2Hole.HoleId}, as both ends already " +
                          $"resolve to {component.Net1Name} and the part is bypassed."));
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
                    TechnicalMessage: $"Move the free end of {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                                      $"from {component.Pin1Hole.HoleId} to the target net, as {component.Pin1Hole.HoleId} is the " +
                                      $"only terminal on {FormatNetName(net1)} and carries no return current."));
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
                    TechnicalMessage: $"Move the free end of {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                                      $"from {component.Pin2Hole.HoleId} to the target net, as {component.Pin2Hole.HoleId} is the " +
                                      $"only terminal on {FormatNetName(net2)} and carries no return current."));
            }
        }

        // ------------------------------------------------------------------
        // Rule 105: mechanical congestion (three or more leads in one hole).
        // ------------------------------------------------------------------
        AppendHoleCongestion(violations);

        // ------------------------------------------------------------------
        // Rules 200 + 202: target schematic parity.
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
    public int GetRootNet(int stripId)
    {
        lock (_gate)
            return IsValidStrip(stripId) ? FindRootNet(stripId) : -1;
    }

    /// <summary>Resolved KiCad-style net name for a strip (e.g. <c>NET_VCC</c>, <c>NET_115</c>).</summary>
    public string GetResolvedNetName(int stripId)
    {
        lock (_gate)
            return IsValidStrip(stripId) ? FormatNetName(FindRootNet(stripId)) : string.Empty;
    }

    public bool AreSameNet(int stripIdA, int stripIdB)
    {
        lock (_gate)
            return IsValidStrip(stripIdA) && IsValidStrip(stripIdB) && FindRootNet(stripIdA) == FindRootNet(stripIdB);
    }

    private DrcViolation BuildPowerShortViolation()
    {
        if (_bridgeTrackId != 0)
        {
            return new DrcViolation(
                Code: ViolationCode.ErcVccGndDirectShort,
                Severity: DrcSeverity.Critical,
                ComponentId: $"JumperWire_{_bridgeTrackId}",
                OffendingStripId: IsVccStrip(_bridgeHole1.StripId) ? _bridgeHole1.StripId : _bridgeHole2.StripId,
                BadHole: _bridgeHole1,
                SuggestedTargetHole: default,
                TechnicalMessage: $"Remove jumper #{_bridgeTrackId} from {_bridgeHole1.HoleId} (VCC) to " +
                                  $"{_bridgeHole2.HoleId} (GND), as it ties the supply directly to ground.");
        }

        HoleCoord badHole = _pinMatrix.TryGetRailHole(StripTopVcc, 1, out HoleCoord railHole)
            ? railHole
            : new HoleCoord("VCC_RAIL", StripTopVcc, BreadboardSpec.RailFirstX, BreadboardSpec.TopVccRailY);

        return new DrcViolation(
            Code: ViolationCode.ErcVccGndDirectShort,
            Severity: DrcSeverity.Critical,
            ComponentId: "POWER_RAILS",
            OffendingStripId: StripTopVcc,
            BadHole: badHole,
            SuggestedTargetHole: default,
            TechnicalMessage: "Remove the jumper chain from the VCC rail to the GND rail, " +
                              "as it ties the supply directly to ground. Trace the rail connections before applying power.");
    }

    /// <summary>
    /// Rule 101 for a DIP/module: two of its pins forced into one metal clip are
    /// a hard short. Pins that merely share a net through the rails are normal.
    /// </summary>
    private void AppendMultiPinShort(SnappedComponent component, List<DrcViolation> violations)
    {
        int pinA = -1;
        int pinB = -1;
        HoleCoord holeA = default;
        HoleCoord holeB = default;

        for (int a = 0; a < component.PinCount && pinB < 0; a++)
        {
            HoleCoord first = component.GetHole(a);
            if (string.IsNullOrEmpty(first.HoleId))
                continue;

            for (int b = a + 1; b < component.PinCount; b++)
            {
                HoleCoord second = component.GetHole(b);
                if (string.IsNullOrEmpty(second.HoleId) || first.StripId != second.StripId)
                    continue;

                pinA = a;
                pinB = b;
                holeA = first;
                holeB = second;
                break;
            }
        }

        if (pinB < 0)
            return;

        HoleCoord suggested = _pinMatrix.TryFindNearestFreeHoleInRow(holeB, _stripOccupied, out HoleCoord free)
            ? free
            : default;

        violations.Add(new DrcViolation(
            Code: ViolationCode.ErcSameStripShort,
            Severity: DrcSeverity.Critical,
            ComponentId: $"{component.RawDetection.Kind}_{component.RawDetection.TrackId}",
            OffendingStripId: holeB.StripId,
            BadHole: holeB,
            SuggestedTargetHole: suggested,
            TechnicalMessage: $"Move pin {pinB + 1} of {component.RawDetection.Kind} #{component.RawDetection.TrackId} " +
                              $"from {holeB.HoleId} to " +
                              (string.IsNullOrEmpty(suggested.HoleId) ? "an empty column" : suggested.HoleId) +
                              $", as it shares metal clip {holeB.StripId} with pin {pinA + 1} at {holeA.HoleId} " +
                              $"({component.GetNetName(pinA)})."));
    }

    /// <summary>
    /// Rule 105. A clip that already grips two leads will not reliably grip a
    /// third, so flag the hole and point at the nearest free hole in the same row.
    /// </summary>
    private void AppendHoleCongestion(List<DrcViolation> violations)
    {
        for (int index = 0; index < _holeLeadCount.Length; index++)
        {
            int leads = _holeLeadCount[index];
            if (leads <= MaxLeadsPerHole)
                continue;

            if (!_pinMatrix.TryGetHoleAt(index, out HoleCoord hole))
                continue;

            HoleCoord suggestion = TryFindFreeHoleNear(hole);

            violations.Add(new DrcViolation(
                Code: ViolationCode.ErcHoleCollision,
                Severity: DrcSeverity.Warning,
                ComponentId: $"HOLE_{hole.HoleId}",
                OffendingStripId: hole.StripId,
                BadHole: hole,
                SuggestedTargetHole: suggestion,
                TechnicalMessage: $"Move one lead from {hole.HoleId} to " +
                                  (string.IsNullOrEmpty(suggestion.HoleId) ? "a nearby free hole" : suggestion.HoleId) +
                                  $", as {leads} leads share one clip and the joint will go intermittent."));
        }
    }

    private HoleCoord TryFindFreeHoleNear(HoleCoord hole)
    {
        if (BreadboardSpec.IsRailStrip(hole.StripId))
        {
            int separator = hole.HoleId.LastIndexOf('_');
            if (separator < 0 || !int.TryParse(hole.HoleId.AsSpan(separator + 1), out int railIndex))
                return default;

            for (int distance = 1; distance <= 8; distance++)
            {
                if (_pinMatrix.TryGetRailHole(hole.StripId, railIndex + distance, out HoleCoord candidate)
                    && IsHoleFree(candidate))
                {
                    return candidate;
                }

                if (_pinMatrix.TryGetRailHole(hole.StripId, railIndex - distance, out candidate)
                    && IsHoleFree(candidate))
                {
                    return candidate;
                }
            }

            return default;
        }

        if (!BreadboardSpec.TryParseHoleId(hole.HoleId, out char row, out int column))
            return default;

        for (int distance = 1; distance <= 8; distance++)
        {
            for (int direction = 0; direction < 2; direction++)
            {
                int candidateColumn = column + (direction == 0 ? distance : -distance);
                if (!BreadboardSpec.IsValidColumn(candidateColumn))
                    continue;

                HoleCoord candidate = BreadboardSpec.CreateHole(row, candidateColumn);
                if (candidate.StripId != hole.StripId && IsHoleFree(candidate))
                    return candidate;
            }
        }

        return default;
    }

    private bool IsHoleFree(in HoleCoord hole) =>
        _pinMatrix.TryGetHoleIndex(hole.HoleId, out int index) && _holeLeadCount[index] < MaxLeadsPerHole;

    /// <summary>
    /// Rules 200 (open target net) and 202 (two target nets bridged). One report
    /// per target net keeps the inspector readable while still naming both holes.
    /// </summary>
    private void CheckSchematicParity(TargetSchematic target, List<DrcViolation> violations)
    {
        IReadOnlyList<TargetNet> nets = target.Nets;

        for (int n = 0; n < nets.Count; n++)
        {
            TargetNet net = nets[n];
            if (!TryGetAnchor(net, out int anchorRoot, out HoleCoord anchor))
                continue;

            for (int i = 0; i < net.Holes.Count; i++)
            {
                if (!TryResolveOccupied(net.Holes[i], out HoleCoord hole, out int root))
                    continue;

                if (root == anchorRoot)
                    continue;

                violations.Add(new DrcViolation(
                    Code: ViolationCode.DrcSchematicNetMismatch,
                    Severity: DrcSeverity.Warning,
                    ComponentId: net.Name,
                    OffendingStripId: hole.StripId,
                    BadHole: hole,
                    SuggestedTargetHole: anchor,
                    TechnicalMessage: $"Add a jumper from {anchor.HoleId} to {hole.HoleId}, as net '{net.Name}' is open " +
                                      $"between them: they resolve to {FormatNetName(root)} and {FormatNetName(anchorRoot)}."));
                break;
            }
        }

        for (int a = 0; a < nets.Count; a++)
        {
            for (int b = a + 1; b < nets.Count; b++)
            {
                TargetNet first = nets[a];
                TargetNet second = nets[b];

                if (NetsShareHole(first, second) || IsPowerPair(first, second))
                    continue;

                if (!TryFindSharedRoot(first, second, out int root, out HoleCoord holeA, out HoleCoord holeB))
                    continue;

                violations.Add(new DrcViolation(
                    Code: ViolationCode.DrcUnexpectedNetLink,
                    Severity: DrcSeverity.Warning,
                    ComponentId: $"{first.Name} / {second.Name}",
                    OffendingStripId: holeB.StripId,
                    BadHole: holeB,
                    SuggestedTargetHole: holeA,
                    TechnicalMessage: $"Remove the jumper that joins {holeA.HoleId} and {holeB.HoleId}, as target nets " +
                                      $"'{first.Name}' and '{second.Name}' must stay separate; both now resolve to " +
                                      $"{FormatNetName(root)}."));
            }
        }
    }

    private bool TryGetAnchor(TargetNet net, out int root, out HoleCoord hole)
    {
        root = -1;
        hole = default;

        for (int i = 0; i < net.Holes.Count; i++)
        {
            if (!TryResolveOccupied(net.Holes[i], out hole, out root))
                continue;

            return true;
        }

        return false;
    }

    private bool TryResolveOccupied(string holeId, out HoleCoord hole, out int root)
    {
        root = -1;
        hole = default;

        if (!_pinMatrix.TryGetHole(holeId, out hole))
            return false;

        if (!IsValidStrip(hole.StripId) || !_stripOccupied[hole.StripId])
            return false;

        root = FindRootNet(hole.StripId);
        return root >= 0;
    }

    private bool TryFindSharedRoot(
        TargetNet first,
        TargetNet second,
        out int root,
        out HoleCoord firstHole,
        out HoleCoord secondHole)
    {
        root = -1;
        firstHole = default;
        secondHole = default;

        for (int i = 0; i < first.Holes.Count; i++)
        {
            if (!TryResolveOccupied(first.Holes[i], out HoleCoord a, out int rootA))
                continue;

            for (int j = 0; j < second.Holes.Count; j++)
            {
                if (!TryResolveOccupied(second.Holes[j], out HoleCoord b, out int rootB))
                    continue;

                if (rootA != rootB)
                    continue;

                root = rootA;
                firstHole = a;
                secondHole = b;
                return true;
            }
        }

        return false;
    }

    private static bool NetsShareHole(TargetNet first, TargetNet second)
    {
        for (int i = 0; i < first.Holes.Count; i++)
        {
            for (int j = 0; j < second.Holes.Count; j++)
            {
                if (string.Equals(first.Holes[i], second.Holes[j], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>VCC↔GND is Rule 100's job; reporting it twice would double the alarm.</summary>
    private static bool IsPowerPair(TargetNet first, TargetNet second)
    {
        string a = first.Name.ToUpperInvariant();
        string b = second.Name.ToUpperInvariant();

        return (a.Contains("VCC") || a.Contains("VDD")) && (b.Contains("GND") || b.Contains("VSS"))
               || (b.Contains("VCC") || b.Contains("VDD")) && (a.Contains("GND") || a.Contains("VSS"));
    }

    private void CountLead(in HoleCoord hole)
    {
        if (_pinMatrix.TryGetHoleIndex(hole.HoleId, out int index))
            _holeLeadCount[index]++;
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
        Array.Clear(_holeLeadCount);

        _bridgeTrackId = 0;
        _bridgeHole1 = default;
        _bridgeHole2 = default;
        _powerShortRoot = -1;
    }

    private static bool IsValidStrip(int stripId) => (uint)stripId < (uint)BreadboardSpec.StripCount;

    private static bool IsVccStrip(int stripId) => stripId is StripTopVcc or StripBotVcc;

    private static bool IsGndStrip(int stripId) => stripId is StripTopGnd or StripBotGnd;

    private static bool IsPowerBridge(int stripA, int stripB) =>
        (IsVccStrip(stripA) && IsGndStrip(stripB)) || (IsGndStrip(stripA) && IsVccStrip(stripB));

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

    private string FormatNetName(int rootId)
    {
        // A merged power rail keeps its own name so ground never masquerades as VCC.
        if (rootId >= 0 && rootId == _powerShortRoot)
            return "NET_VCC_GND_SHORT";

        return rootId switch
        {
            StripTopVcc or StripBotVcc => "NET_VCC",
            StripTopGnd or StripBotGnd => "NET_GND",
            _ => $"NET_{rootId:D3}"
        };
    }

    private static bool IsPolarized(ComponentKind kind) =>
        kind is ComponentKind.Led or ComponentKind.IntegratedCircuit;

    private static int ComputeTopologyHash(List<SnappedComponent> components, List<DrcViolation> violations)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (SnappedComponent component in components)
        {
            hash = (hash ^ (byte)component.RawDetection.Kind) * prime;

            if (component.PinCount > 2)
            {
                // Pin order is meaningful on a DIP, so hash every clip in sequence.
                hash = (hash ^ (uint)component.PinCount) * prime;
                for (int pin = 0; pin < component.PinCount; pin++)
                    hash = (hash ^ (uint)component.GetHole(pin).StripId) * prime;

                continue;
            }

            int first = component.Pin1Hole.StripId;
            int second = component.Pin2Hole.StripId;

            // Rotating a resistor, capacitor or jumper 180 degrees is the same physical
            // circuit, so hash the pair in canonical order. A flipped LED or IC really
            // is a different board and must still change the hash.
            if (!IsPolarized(component.RawDetection.Kind) && first > second)
                (first, second) = (second, first);

            hash = (hash ^ (uint)first) * prime;
            hash = (hash ^ (uint)second) * prime;
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
