// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/ElectricalRulesChecker.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Sub-millisecond deterministic Netlist Builder & KiCad ERC/DRC Checker.
// PERFORMANCE REQUIREMENT: Must execute in < 0.1 ms on E-Core 18.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.DrcEngine;

public sealed class ElectricalRulesChecker
{
    // Reserved Strip IDs for power rails
    public const int StripTopVcc = 1;
    public const int StripTopGnd = 2;
    public const int StripBotVcc = 3;
    public const int StripBotGnd = 4;

    private readonly int[] _parentStrip = new int[256];

    public DrcStatePacket EvaluateBoard(
        long frameId,
        double yoloMs,
        ReadOnlySpan<DetectedComponent> rawDetections,
        Func<float, float, (bool Found, HoleCoord Hole)> snapLookup)
    {
        var sw = Stopwatch.StartNew();

        // 1. Reset Union-Find structure (each strip starts as its own electrical net)
        for (int i = 0; i < _parentStrip.Length; i++)
            _parentStrip[i] = i;

        var snappedList = new List<SnappedComponent>(rawDetections.Length);
        var violations = new List<DrcViolation>();

        // 2. First Pass: Snap all leads to PinMatrix & merge Jumper Wire nodes
        foreach (ref readonly var det in rawDetections)
        {
            var (found1, hole1) = snapLookup(det.Lead1Px, det.Lead1Py);
            var (found2, hole2) = snapLookup(det.Lead2Px, det.Lead2Py);

            if (!found1 || !found2) continue;

            if (det.Kind == ComponentKind.JumperWire)
            {
                UnionStrips(hole1.StripId, hole2.StripId);
            }

            snappedList.Add(new SnappedComponent(det, hole1, hole2, string.Empty, string.Empty));
        }

        // 3. Rule Check A: Universal Power-to-Ground Direct Short (CRITICAL)
        bool vccToGndShort =
            FindRootNet(StripTopVcc) == FindRootNet(StripTopGnd) ||
            FindRootNet(StripBotVcc) == FindRootNet(StripBotGnd) ||
            FindRootNet(StripTopVcc) == FindRootNet(StripBotGnd);

        if (vccToGndShort)
        {
            violations.Add(new DrcViolation(
                Code: ViolationCode.ErcVccGndDirectShort,
                Severity: DrcSeverity.Critical,
                ComponentId: "JUMPER_NET",
                OffendingStripId: StripTopVcc,
                BadHole: new HoleCoord("VCC_RAIL", StripTopVcc, 50, 20),
                SuggestedTargetHole: default,
                TechnicalMessage: "CRITICAL SHORT: Positive VCC rail is bridged directly to Ground (GND)."
            ));
        }

        // 4. Second Pass: Assign Resolved Net Names & Check Component-Level Rules
        var stripDegreeCount = new Dictionary<int, int>();
        for (int i = 0; i < snappedList.Count; i++)
        {
            var item = snappedList[i];
            int net1 = FindRootNet(item.Pin1Hole.StripId);
            int net2 = FindRootNet(item.Pin2Hole.StripId);

            string net1Name = FormatNetName(net1);
            string net2Name = FormatNetName(net2);

            snappedList[i] = item with { Net1Name = net1Name, Net2Name = net2Name };

            stripDegreeCount[net1] = stripDegreeCount.GetValueOrDefault(net1) + 1;
            stripDegreeCount[net2] = stripDegreeCount.GetValueOrDefault(net2) + 1;

            // Skip jumper wires for component body checks
            if (item.RawDetection.Kind == ComponentKind.JumperWire) continue;

            // Rule Check B: Same-Strip / Same-Net Short across a 2-terminal component
            if (net1 == net2)
            {
                // [JUNIOR TASK - ISSUE #2A]: Replace the placeholder SuggestedTargetHole below 
                // by querying PinMatrixLookup for the nearest adjacent empty strip (e.g., StripId + 1)
                // in the same column so SkiaSharp draws the green Ratsnest arrow to the exact right hole.
                var fallbackTarget = new HoleCoord(
                    HoleId: "ADJACENT_ROW",
                    StripId: item.Pin2Hole.StripId + 1,
                    Px: item.Pin2Hole.Px + 15.8f, // 2.54mm pitch step in pixels
                    Py: item.Pin2Hole.Py
                );

                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcSameStripShort,
                    Severity: DrcSeverity.Critical,
                    ComponentId: $"{item.RawDetection.Kind}_{item.RawDetection.TrackId}",
                    OffendingStripId: item.Pin2Hole.StripId,
                    BadHole: item.Pin2Hole,
                    SuggestedTargetHole: fallbackTarget,
                    TechnicalMessage: $"Both leads of {item.RawDetection.Kind} #{item.RawDetection.TrackId} " +
                                      $"({item.Pin1Hole.HoleId} & {item.Pin2Hole.HoleId}) are shorted on {net1Name}."
                ));
            }
        }

        // ====================================================================
        // [JUNIOR TASK - ISSUE #2B]: Implement Floating Lead Check (ERC_FLOATING_LEAD)
        // Loop through `snappedList`. If a non-jumper component has a lead on a `net` 
        // where `stripDegreeCount[net] == 1`, emit a `DrcSeverity.Warning` violation 
        // with `ViolationCode.ErcFloatingLead` so the UI highlights the open node in Amber.
        // ====================================================================

        // ====================================================================
        // [JUNIOR TASK - ISSUE #2C]: Implement Target Schematic Parity Check
        // Compare `snappedList` adjacency against `target_netlist.json`. If a pin is in the
        // wrong net, calculate the `SuggestedTargetHole` and emit `DrcSchematicNetMismatch`.
        // ====================================================================

        sw.Stop();
        int stateHash = ComputeTopologyHash(snappedList, violations);

        return new DrcStatePacket
        {
            FrameNumber = frameId,
            YoloInferenceMs = yoloMs,
            DrcComputeMs = sw.Elapsed.TotalMilliseconds,
            StateHash = stateHash,
            Components = snappedList,
            Violations = violations
        };
    }

    private int FindRootNet(int stripId)
    {
        while (_parentStrip[stripId] != stripId)
        {
            _parentStrip[stripId] = _parentStrip[_parentStrip[stripId]]; // Path compression
            stripId = _parentStrip[stripId];
        }
        return stripId;
    }

    private void UnionStrips(int stripA, int stripB)
    {
        int rootA = FindRootNet(stripA);
        int rootB = FindRootNet(stripB);
        if (rootA == rootB) return;

        // Always keep power rail IDs (1..4) as the canonical root when merging
        if (rootA < rootB) _parentStrip[rootB] = rootA;
        else _parentStrip[rootA] = rootB;
    }

    private static string FormatNetName(int rootId) => rootId switch
    {
        StripTopVcc or StripBotVcc => "NET_VCC",
        StripTopGnd or StripBotGnd => "NET_GND",
        _ => $"NET_{rootId:D3}"
    };

    private static int ComputeTopologyHash(List<SnappedComponent> comps, List<DrcViolation> errs)
    {
        var hash = new HashCode();
        foreach (var c in comps)
        {
            hash.Add(c.RawDetection.Kind);
            hash.Add(c.Pin1Hole.StripId);
            hash.Add(c.Pin2Hole.StripId);
        }
        hash.Add(errs.Count);
        return hash.ToHashCode();
    }
}