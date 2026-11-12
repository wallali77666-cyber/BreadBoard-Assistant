// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/ElectricalRulesChecker.cs
// AUTHOR: Senior Systems Architect & Argon-1 Lead
// PURPOSE: Sub-millisecond deterministic Netlist Builder & KiCad ERC/DRC Checker.
// PERFORMANCE REQUIREMENT: Executes in < 0.1 ms on steady-state reflex loop.
// THREADING: Guaranteed thread-safe public API with re-entrant state gate.
//
// RULE SET (v3 - Argon-1 Enterprise Grade)
//   100  ErcVccGndDirectShort   critical  VCC rail tied to a GND rail
//   101  ErcSameStripShort      critical  both leads of a part on one metal clip
//   102  ErcFloatingLead        warning   terminal on a net with a single connection
//   103  ErcReversedPolarity    critical  IC or polarized sensor connected in reverse
//   104  ErcRedundantJumper     warning   jumper whose two ends already share a net
//   105  ErcHoleCollision       warning   more than three leads forced into one hole
//   106  ErcVoltageOverload     critical  5V rail connected to 3.3V-only GPIO (ESP32/RP2040)
//   107  ErcInputOnlyViolation  critical  output attempted on input-only pin (ESP32 GPIO 34-39)
//   108  ErcProtocolCrossover   critical  UART TX-TX/RX-RX or I2C SDA-SCL swapped crossover
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
/// Builds the electrical netlist from snapped component leads with an optimized Union-Find
/// structure and applies the full KiCad-grade ERC/DRC rule set.
/// </summary>
public sealed class ElectricalRulesChecker
{
    public const int StripTopVcc = BreadboardSpec.StripTopVcc;
    public const int StripTopGnd = BreadboardSpec.StripTopGnd;
    public const int StripBotVcc = BreadboardSpec.StripBotVcc;
    public const int StripBotGnd = BreadboardSpec.StripBotGnd;

    public const int MaxLeadsPerHole = 3;

    private readonly int[] _parentStrip = new int[BreadboardSpec.StripCount];
    private readonly bool[] _stripOccupied = new bool[BreadboardSpec.StripCount];
    private readonly int[] _stripDegree = new int[BreadboardSpec.StripCount];
    private readonly int[] _holeLeadCount = new int[BreadboardSpec.TotalHoleCount];
    private readonly PinMatrixLookup _pinMatrix;

    private readonly object _gate = new();

    private int _bridgeTrackId;
    private HoleCoord _bridgeHole1;
    private HoleCoord _bridgeHole2;
    private int _powerShortRoot = -1;

    public ElectricalRulesChecker()
        : this(PinMatrixLookup.Shared)
    {
    }

    public ElectricalRulesChecker(PinMatrixLookup pinMatrix)
    {
        _pinMatrix = pinMatrix ?? throw new ArgumentNullException(nameof(pinMatrix));
    }

    public PinMatrixLookup PinMatrix => _pinMatrix;

    public DrcStatePacket EvaluateBoard(
        long frameId,
        double yoloMs,
        ReadOnlySpan<DetectedComponent> rawDetections,
        TargetSchematic? target = null)
    {
        lock (_gate)
            return EvaluateBoardCore(frameId, yoloMs, rawDetections, new PinMatrixSnapper(_pinMatrix), target);
    }

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
        var violations = new List<DrcViolation>(8);

        // ------------------------------------------------------------------
        // Pass 1: Snap every lead to the 830-point matrix and union jumper nets.
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
        // Rule 100: VCC-to-GND Direct Power Short
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

        _powerShortRoot = !vccShortedToGnd
            ? -1
            : rootTopVcc == rootTopGnd || rootTopVcc == rootBotGnd
                ? rootTopVcc
                : rootBotVcc;

        if (vccShortedToGnd)
            violations.Add(BuildPowerShortViolation());

        // ------------------------------------------------------------------
        // Pass 2: Resolve Net Names and Terminal Degrees
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

            if (component.RawDetection.Kind == ComponentKind.JumperWire)
                continue;

            _stripDegree[net1]++;
            _stripDegree[net2]++;
        }

        // ------------------------------------------------------------------
        // Pass 3: Component-Specific ERC/DRC Verification
        // ------------------------------------------------------------------
        for (int i = 0; i < snapped.Count; i++)
        {
            SnappedComponent component = snapped[i];
            int net1 = FindRootNet(component.Pin1Hole.StripId);
            int net2 = FindRootNet(component.Pin2Hole.StripId);

            if (component.RawDetection.Kind == ComponentKind.JumperWire)
            {
                // Rule 104: Redundant Jumper within the same physical strip
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

            // Microcontroller Dev Board Rule Engine (ESP32, NodeMCU, Arduino, Pico)
            if (DevBoardAndModuleRuleEngine.IsDevBoard(component))
            {
                DevBoardAndModuleRuleEngine.CheckDevBoardDrc(
                    component,
                    snapped,
                    violations,
                    _pinMatrix,
                    FindRootNet,
                    FormatNetName,
                    _stripOccupied,
                    _stripDegree);
                continue;
            }

            // Sensors and Regulators
            if (component.RawDetection.Kind is ComponentKind.Sensor or ComponentKind.VoltageRegulator
                || (component.RawDetection.Kind != ComponentKind.IntegratedCircuit
                    && SensorPinoutCatalog.TryGetDefinition(component.RawDetection.DecodedValueLabel ?? component.RawDetection.SymbolId, component.PinCount, out _)))
            {
                CheckSensorDrc(component, violations, snapped);
                continue;
            }

            // Integrated Circuits (DIP-8..DIP-40)
            if (component.RawDetection.Kind == ComponentKind.IntegratedCircuit || component.PinCount > 2)
            {
                CheckIntegratedCircuitDrc(component, violations);
                continue;
            }

            // Standard 2-lead components (Resistors, Capacitors, LEDs, Diodes, Buttons)
            if (net1 == net2)
            {
                bool sameStrip = component.Pin1Hole.StripId == component.Pin2Hole.StripId;

                HoleCoord suggestedTarget = sameStrip
                    && _pinMatrix.TryFindNearestFreeHoleInRow(component.Pin2Hole, _stripOccupied, out HoleCoord freeHole)
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

                // Rule 109 & 110: LED Current-Limiting Resistor Checks
        CheckLedCurrentLimitingResistors(snapped, violations);

        // Rule 105: Mechanical Congestion (excessive leads in one hole)
        AppendHoleCongestion(violations);

        // Rules 200 + 202: Target Schematic Parity
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

    public int GetRootNet(int stripId)
    {
        lock (_gate)
            return IsValidStrip(stripId) ? FindRootNet(stripId) : -1;
    }

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

    private void CheckIntegratedCircuitDrc(SnappedComponent component, List<DrcViolation> violations)
    {
        string partName = component.RawDetection.DecodedValueLabel ?? component.RawDetection.SymbolId ?? "IC";
        int pinCount = component.PinCount;

        for (int a = 0; a < pinCount; a++)
        {
            HoleCoord holeA = component.GetHole(a);
            if (string.IsNullOrEmpty(holeA.HoleId))
                continue;

            for (int b = a + 1; b < pinCount; b++)
            {
                HoleCoord holeB = component.GetHole(b);
                if (string.IsNullOrEmpty(holeB.HoleId) || holeA.StripId != holeB.StripId)
                    continue;

                bool isOpposingTrenchPair = component.RawDetection.Kind == ComponentKind.IntegratedCircuit
                    && pinCount >= 4
                    && (a + b == pinCount - 1);

                HoleCoord suggested = _pinMatrix.TryFindNearestFreeHoleInRow(holeB, _stripOccupied, out HoleCoord free)
                    ? free
                    : default;

                string msg = isOpposingTrenchPair
                    ? $"DIP IC '{partName}' does not straddle the center divider trench! Move pin {b + 1} of {component.RawDetection.Kind} #{component.RawDetection.TrackId} from {holeB.HoleId} to {(string.IsNullOrEmpty(suggested.HoleId) ? "an empty column" : suggested.HoleId)}, as it shares metal clip {holeB.StripId} with pin {a + 1} at {holeA.HoleId} ({component.GetNetName(a)}). Place the IC across the center trench between rows E and F."
                    : $"Move pin {b + 1} of {component.RawDetection.Kind} #{component.RawDetection.TrackId} from {holeB.HoleId} to {(string.IsNullOrEmpty(suggested.HoleId) ? "an empty column" : suggested.HoleId)}, as it shares metal clip {holeB.StripId} with pin {a + 1} at {holeA.HoleId} ({component.GetNetName(a)}).";

                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcSameStripShort,
                    Severity: DrcSeverity.Critical,
                    ComponentId: $"{component.RawDetection.Kind}_{component.RawDetection.TrackId}",
                    OffendingStripId: holeB.StripId,
                    BadHole: holeB,
                    SuggestedTargetHole: suggested,
                    TechnicalMessage: msg));
            }
        }

        if (IcPinoutCatalog.TryGetDefinition(partName, pinCount, out IcDefinition def))
        {
            int vccIdx = def.PrimaryVccPin - 1;
            int gndIdx = def.PrimaryGndPin - 1;

            if (vccIdx >= 0 && vccIdx < pinCount && gndIdx >= 0 && gndIdx < pinCount)
            {
                HoleCoord vccHole = component.GetHole(vccIdx);
                HoleCoord gndHole = component.GetHole(gndIdx);

                if (!string.IsNullOrEmpty(vccHole.HoleId) && !string.IsNullOrEmpty(gndHole.HoleId))
                {
                    int vccRoot = FindRootNet(vccHole.StripId);
                    int gndRoot = FindRootNet(gndHole.StripId);

                    string vccNet = FormatNetName(vccRoot);
                    string gndNet = FormatNetName(gndRoot);

                    if (vccRoot == gndRoot && vccRoot >= 0)
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcVccGndDirectShort,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: vccHole.StripId,
                            BadHole: vccHole,
                            SuggestedTargetHole: gndHole,
                            TechnicalMessage: $"Direct power short across IC '{partName}': VCC (pin {def.PrimaryVccPin}) at {vccHole.HoleId} and GND (pin {def.PrimaryGndPin}) at {gndHole.HoleId} both resolve to {vccNet}. Power is shorted through the circuit; disconnect immediately to prevent silicon burnout."));
                    }
                    else if (IsGndStrip(vccHole.StripId) || vccNet == "NET_GND" || IsVccStrip(gndHole.StripId) || gndNet == "NET_VCC")
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcReversedPolarity,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: vccHole.StripId,
                            BadHole: vccHole,
                            SuggestedTargetHole: gndHole,
                            TechnicalMessage: $"Reverse power polarity on IC '{partName}': VCC pin {def.PrimaryVccPin} is tied to {vccNet} and GND pin {def.PrimaryGndPin} is tied to {gndNet}. Reverse voltage will destroy the IC! Swap VCC and GND connections before powering on."));
                    }
                    else
                    {
                        if (IsFloatingNet(vccRoot) || (!IsVccStrip(vccRoot) && _stripDegree[vccRoot] <= 1))
                        {
                            _pinMatrix.TryGetRailHole(StripTopVcc, 1, out HoleCoord vccRailHole);
                            violations.Add(new DrcViolation(
                                Code: ViolationCode.ErcFloatingLead,
                                Severity: DrcSeverity.Warning,
                                ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                                OffendingStripId: vccHole.StripId,
                                BadHole: vccHole,
                                SuggestedTargetHole: vccRailHole,
                                TechnicalMessage: $"IC '{partName}' power pin {def.PrimaryVccPin} (VCC) at {vccHole.HoleId} is floating ({vccNet}). Connect a jumper from {vccHole.HoleId} to the positive power rail (+)."));
                        }

                        if (IsFloatingNet(gndRoot) || (!IsGndStrip(gndRoot) && _stripDegree[gndRoot] <= 1))
                        {
                            _pinMatrix.TryGetRailHole(StripTopGnd, 1, out HoleCoord gndRailHole);
                            violations.Add(new DrcViolation(
                                Code: ViolationCode.ErcFloatingLead,
                                Severity: DrcSeverity.Warning,
                                ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                                OffendingStripId: gndHole.StripId,
                                BadHole: gndHole,
                                SuggestedTargetHole: gndRailHole,
                                TechnicalMessage: $"IC '{partName}' ground pin {def.PrimaryGndPin} (GND) at {gndHole.HoleId} is floating ({gndNet}). Connect a jumper from {gndHole.HoleId} to the ground rail (-)."));
                        }
                    }
                }
            }
        }
    }

    private void CheckSensorDrc(
        SnappedComponent component,
        List<DrcViolation> violations,
        IReadOnlyList<SnappedComponent> allComponents)
    {
        string partName = component.RawDetection.DecodedValueLabel ?? component.RawDetection.SymbolId ?? "SENSOR";
        int pinCount = component.PinCount;

        SensorPinoutCatalog.TryGetDefinition(partName, pinCount, out SensorDefinition sensorDef);

        for (int a = 0; a < pinCount; a++)
        {
            HoleCoord holeA = component.GetHole(a);
            if (string.IsNullOrEmpty(holeA.HoleId))
                continue;

            for (int b = a + 1; b < pinCount; b++)
            {
                HoleCoord holeB = component.GetHole(b);
                if (string.IsNullOrEmpty(holeB.HoleId) || holeA.StripId != holeB.StripId)
                    continue;

                string pinAName = sensorDef?.GetPin(a + 1)?.Name ?? $"pin {a + 1}";
                string pinBName = sensorDef?.GetPin(b + 1)?.Name ?? $"pin {b + 1}";

                HoleCoord suggested = _pinMatrix.TryFindNearestFreeHoleInRow(holeB, _stripOccupied, out HoleCoord free)
                    ? free
                    : default;

                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcSameStripShort,
                    Severity: DrcSeverity.Critical,
                    ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                    OffendingStripId: holeB.StripId,
                    BadHole: holeB,
                    SuggestedTargetHole: suggested,
                    TechnicalMessage: $"Sensor '{sensorDef?.DisplayName ?? partName}' {pinBName} at {holeB.HoleId} shares conductive strip {holeB.StripId} with {pinAName} at {holeA.HoleId}. Move {pinBName} to an adjacent empty column."));
            }
        }

        if (sensorDef is null)
            return;

        int vccPin = sensorDef.PrimaryVccPin;
        int gndPin = sensorDef.PrimaryGndPin;

        if (vccPin > 0 && gndPin > 0 && vccPin <= pinCount && gndPin <= pinCount)
        {
            HoleCoord vccHole = component.GetHole(vccPin - 1);
            HoleCoord gndHole = component.GetHole(gndPin - 1);

            if (!string.IsNullOrEmpty(vccHole.HoleId) && !string.IsNullOrEmpty(gndHole.HoleId))
            {
                int vccRoot = FindRootNet(vccHole.StripId);
                int gndRoot = FindRootNet(gndHole.StripId);

                string vccNet = FormatNetName(vccRoot);
                string gndNet = FormatNetName(gndRoot);

                if (vccRoot == gndRoot && vccRoot >= 0)
                {
                    violations.Add(new DrcViolation(
                        Code: ViolationCode.ErcVccGndDirectShort,
                        Severity: DrcSeverity.Critical,
                        ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                        OffendingStripId: vccHole.StripId,
                        BadHole: vccHole,
                        SuggestedTargetHole: gndHole,
                        TechnicalMessage: $"Direct power short across '{sensorDef.DisplayName}': VCC (pin {vccPin}) at {vccHole.HoleId} and GND (pin {gndPin}) at {gndHole.HoleId} both resolve to {vccNet}. Disconnect immediately to prevent thermal damage."));
                }
                else if (IsGndStrip(vccHole.StripId) || vccNet == "NET_GND" || IsVccStrip(gndHole.StripId) || gndNet == "NET_VCC")
                {
                    violations.Add(new DrcViolation(
                        Code: ViolationCode.ErcReversedPolarity,
                        Severity: DrcSeverity.Critical,
                        ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                        OffendingStripId: vccHole.StripId,
                        BadHole: vccHole,
                        SuggestedTargetHole: gndHole,
                        TechnicalMessage: $"Reverse power polarity on '{sensorDef.DisplayName}': VCC pin {vccPin} is tied to {vccNet} and GND pin {gndPin} is tied to {gndNet}. Reverse voltage will destroy the sensor! Swap VCC and GND connections before powering on."));
                }
                else
                {
                                        if (sensorDef.MaxVoltage > 0 && sensorDef.MaxVoltage <= 3.6 && (IsVccStrip(vccRoot) || vccNet == "NET_VCC"))
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcSensorVoltageMismatch,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: vccHole.StripId,
                            BadHole: vccHole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Voltage mismatch on '{sensorDef.DisplayName}': Sensor maximum supply is {sensorDef.MaxVoltage:F1}V, but VCC pin {vccPin} is tied to 5V rail '{vccNet}'. Connect to 3.3V power to prevent damaging sensor silicon."));
                    }
                    else if (sensorDef.MinVoltage >= 4.5 && (vccNet == "NET_3V3" || vccNet.Contains("3V3") || vccNet.Contains("3.3V")))
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcSensorVoltageMismatch,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: vccHole.StripId,
                            BadHole: vccHole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Undersupply voltage warning on '{sensorDef.DisplayName}': Module minimum supply is {sensorDef.MinVoltage:F1}V (standard nematic fluid contrast requires ~4.5V+), but VCC pin {vccPin} is tied to 3.3V rail '{vccNet}'. At 3.3V, backlight illuminates but characters remain invisible even with contrast potentiometer turned to ground. Connect VCC to 5.0V rail."));
                    }

                    if (IsFloatingNet(vccRoot) || (!IsVccStrip(vccRoot) && _stripDegree[vccRoot] <= 1))
                    {
                        _pinMatrix.TryGetRailHole(StripTopVcc, 1, out HoleCoord vccRailHole);
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcFloatingLead,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: vccHole.StripId,
                            BadHole: vccHole,
                            SuggestedTargetHole: vccRailHole,
                            TechnicalMessage: $"'{sensorDef.DisplayName}' power pin {vccPin} ({sensorDef.GetPin(vccPin)?.Name}) at {vccHole.HoleId} is floating or unpowered ({vccNet}). Connect a jumper from {vccHole.HoleId} to the positive power rail (+)."));
                    }

                    if (IsFloatingNet(gndRoot) || (!IsGndStrip(gndRoot) && _stripDegree[gndRoot] <= 1))
                    {
                        _pinMatrix.TryGetRailHole(StripTopGnd, 1, out HoleCoord gndRailHole);
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcFloatingLead,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: gndHole.StripId,
                            BadHole: gndHole,
                            SuggestedTargetHole: gndRailHole,
                            TechnicalMessage: $"'{sensorDef.DisplayName}' ground pin {gndPin} ({sensorDef.GetPin(gndPin)?.Name}) at {gndHole.HoleId} is floating ({gndNet}). Connect a jumper from {gndHole.HoleId} to the ground rail (-)."));
                    }
                }
            }
        }

        for (int p = 0; p < sensorDef.Pins.Count; p++)
        {
            SensorPinSpec spec = sensorDef.Pins[p];
            int pinIdx = spec.PinNumber - 1;
            if (pinIdx >= pinCount)
                continue;

            HoleCoord hole = component.GetHole(pinIdx);
            if (string.IsNullOrEmpty(hole.HoleId))
                continue;

            int root = FindRootNet(hole.StripId);
            string netName = FormatNetName(root);

            switch (spec.PinType)
            {
                case SensorPinType.NotConnected:
                    int stripConnections = CountLeadsOnStrip(hole.StripId, allComponents);
                    if (stripConnections > 1 || IsVccStrip(root) || IsGndStrip(root) || root != hole.StripId)
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcSameStripShort,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Pin {spec.PinNumber} ({spec.Name}) of '{sensorDef.DisplayName}' is NC (Not Connected / factory null pin). It is wired to net {netName}. Remove all jumpers and leads from {hole.HoleId} so pin {spec.PinNumber} remains isolated."));
                    }
                    break;

                case SensorPinType.OpenCollector:
                case SensorPinType.OneWireBus:
                    if (IsVccStrip(root) || netName == "NET_VCC")
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcVccGndDirectShort,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Dangerous connection: Pin {spec.PinNumber} ({spec.Name}) of '{sensorDef.DisplayName}' is tied directly to VCC without a pull-up resistor. When the internal open-collector transistor turns ON, it will short VCC directly to GND through the IC!"));
                    }
                    else if (IsGndStrip(root) || netName == "NET_GND")
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcSameStripShort,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Pin {spec.PinNumber} ({spec.Name}) of '{sensorDef.DisplayName}' is tied directly to GND rail. The open-collector signal line cannot toggle high."));
                    }
                    else
                    {
                        bool hasPullUp = HasPullUpResistor(root, allComponents);
                        if (!hasPullUp)
                        {
                            _pinMatrix.TryGetRailHole(StripTopVcc, 1, out HoleCoord vccRail);
                            string rec = spec.PinType == SensorPinType.OpenCollector ? "1kΩ to 10kΩ" : "4.7kΩ to 10kΩ";
                            // Pin is open-drain / I2C and requires pull-up
                            var pullupViolationCode = (spec.Name.Contains("SDA", StringComparison.OrdinalIgnoreCase) || spec.Name.Contains("SCL", StringComparison.OrdinalIgnoreCase)) ? ViolationCode.ErcI2cMissingPullup : ViolationCode.ErcFloatingLead;
                            violations.Add(new DrcViolation(
                                Code: pullupViolationCode,
                                Severity: DrcSeverity.Warning,
                                ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                                OffendingStripId: hole.StripId,
                                BadHole: hole,
                                SuggestedTargetHole: vccRail,
                                TechnicalMessage: $"Pin {spec.PinNumber} ({spec.Name}) of '{sensorDef.DisplayName}' is an open-drain/open-collector line on {netName} and requires an external pull-up resistor ({rec}) to VCC. Connect a pull-up resistor between {hole.HoleId} and the positive rail (+)."));
                        }
                    }
                    break;

                case SensorPinType.DigitalOutput:
                case SensorPinType.AnalogOutput:
                    if (IsVccStrip(root) || IsGndStrip(root))
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcVccGndDirectShort,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Output pin {spec.PinNumber} ({spec.Name}) of '{sensorDef.DisplayName}' at {hole.HoleId} is shorted directly to {netName} rail. Sensor output driver will overheat or be held invalid."));
                    }
                    break;

                case SensorPinType.RegulatorOut:
                    if (IsGndStrip(root) || netName == "NET_GND")
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcVccGndDirectShort,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Regulator output pin {spec.PinNumber} (OUT) at {hole.HoleId} is shorted directly to GND! Disconnect to prevent regulator shutdown or burnout."));
                    }
                    break;

                case SensorPinType.PassiveLead:
                    if (pinCount == 2 && p == 1)
                    {
                        HoleCoord lead1Hole = component.GetHole(0);
                        int netA = FindRootNet(lead1Hole.StripId);
                        int netB = root;
                        if ((IsVccStrip(netA) && IsGndStrip(netB)) || (IsVccStrip(netB) && IsGndStrip(netA)))
                        {
                            violations.Add(new DrcViolation(
                                Code: ViolationCode.ErcVccGndDirectShort,
                                Severity: DrcSeverity.Warning,
                                ComponentId: $"{partName}_{component.RawDetection.TrackId}",
                                OffendingStripId: hole.StripId,
                                BadHole: hole,
                                SuggestedTargetHole: default,
                                TechnicalMessage: $"'{sensorDef.DisplayName}' is connected directly across power rails without a series divider resistor. To measure sensor changes, connect a fixed resistor (e.g. 10kΩ) in series with the sensor to form a voltage divider."));
                        }
                    }
                    break;
            }
        }
    }

    private bool HasPullUpResistor(int signalRootNet, IReadOnlyList<SnappedComponent> components)
    {
        for (int i = 0; i < components.Count; i++)
        {
            SnappedComponent c = components[i];
            if (c.RawDetection.Kind != ComponentKind.Resistor)
                continue;

            int rNet1 = FindRootNet(c.Pin1Hole.StripId);
            int rNet2 = FindRootNet(c.Pin2Hole.StripId);

            if ((rNet1 == signalRootNet && IsVccStrip(rNet2)) ||
                (rNet2 == signalRootNet && IsVccStrip(rNet1)))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountLeadsOnStrip(int stripId, IReadOnlyList<SnappedComponent> components)
    {
        int count = 0;
        for (int i = 0; i < components.Count; i++)
        {
            SnappedComponent c = components[i];
            for (int p = 0; p < c.PinCount; p++)
            {
                if (c.GetHole(p).StripId == stripId)
                    count++;
            }
        }
        return count;
    }

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
            _parentStrip[stripId] = _parentStrip[_parentStrip[stripId]];
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

        if (rootA < rootB)
            _parentStrip[rootB] = rootA;
        else
            _parentStrip[rootA] = rootB;
    }

    private string FormatNetName(int rootId)
    {
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
        kind is ComponentKind.Led
            or ComponentKind.Diode
            or ComponentKind.Battery
            or ComponentKind.Transistor
            or ComponentKind.VoltageRegulator
            or ComponentKind.Sensor
            or ComponentKind.IntegratedCircuit
            or ComponentKind.Microcontroller;

    private void CheckLedCurrentLimitingResistors(IReadOnlyList<SnappedComponent> components, List<DrcViolation> violations)
    {
        for (int i = 0; i < components.Count; i++)
        {
            SnappedComponent c = components[i];
            if (c.RawDetection.Kind != ComponentKind.Led)
                continue;

            int net1 = FindRootNet(c.Pin1Hole.StripId);
            int net2 = FindRootNet(c.Pin2Hole.StripId);
            if (net1 == net2)
                continue;

            bool pin1Vcc = IsVccStrip(c.Pin1Hole.StripId) || FormatNetName(net1) == "NET_VCC";
            bool pin2Vcc = IsVccStrip(c.Pin2Hole.StripId) || FormatNetName(net2) == "NET_VCC";
            bool pin1Gnd = IsGndStrip(c.Pin1Hole.StripId) || FormatNetName(net1) == "NET_GND";
            bool pin2Gnd = IsGndStrip(c.Pin2Hole.StripId) || FormatNetName(net2) == "NET_GND";

            // Direct power rail connection without resistor:
            if ((pin1Vcc && pin2Gnd) || (pin2Vcc && pin1Gnd))
            {
                violations.Add(new DrcViolation(
                    Code: ViolationCode.ErcResistorCurrentLimitLow,
                    Severity: DrcSeverity.Critical,
                    ComponentId: $"{c.RawDetection.Kind}_{c.RawDetection.TrackId}",
                    OffendingStripId: c.Pin1Hole.StripId,
                    BadHole: c.Pin1Hole,
                    SuggestedTargetHole: default,
                    TechnicalMessage: $"LED #{c.RawDetection.TrackId} is connected directly across power rails without a current-limiting resistor! Immediate burnout risk. Insert a 220Ω–1kΩ series resistor."));
                continue;
            }

            bool pin1PowerRail = pin1Vcc || pin1Gnd || IsVccStrip(net1) || IsGndStrip(net1);
            bool pin2PowerRail = pin2Vcc || pin2Gnd || IsVccStrip(net2) || IsGndStrip(net2);

            // Series resistor check (matches both direct strip sharing and jumper-bridged root nets):
            for (int r = 0; r < components.Count; r++)
            {
                SnappedComponent res = components[r];
                if (res.RawDetection.Kind != ComponentKind.Resistor)
                    continue;

                int resNet1 = FindRootNet(res.Pin1Hole.StripId);
                int resNet2 = FindRootNet(res.Pin2Hole.StripId);
                if (resNet1 == resNet2)
                    continue;

                int ledOtherStrip = -1;
                int resOtherStrip = -1;

                if (!pin1PowerRail && resNet1 == net1)
                {
                    ledOtherStrip = c.Pin2Hole.StripId;
                    resOtherStrip = res.Pin2Hole.StripId;
                }
                else if (!pin1PowerRail && resNet2 == net1)
                {
                    ledOtherStrip = c.Pin2Hole.StripId;
                    resOtherStrip = res.Pin1Hole.StripId;
                }
                else if (!pin2PowerRail && resNet1 == net2)
                {
                    ledOtherStrip = c.Pin1Hole.StripId;
                    resOtherStrip = res.Pin2Hole.StripId;
                }
                else if (!pin2PowerRail && resNet2 == net2)
                {
                    ledOtherStrip = c.Pin1Hole.StripId;
                    resOtherStrip = res.Pin1Hole.StripId;
                }

                if (ledOtherStrip == -1)
                    continue;

                int ledOtherNet = FindRootNet(ledOtherStrip);
                int resOtherNet = FindRootNet(resOtherStrip);

                bool end1Vcc = IsVccStrip(ledOtherStrip) || FormatNetName(ledOtherNet) == "NET_VCC";
                bool end1Gnd = IsGndStrip(ledOtherStrip) || FormatNetName(ledOtherNet) == "NET_GND";
                bool end2Vcc = IsVccStrip(resOtherStrip) || FormatNetName(resOtherNet) == "NET_VCC";
                bool end2Gnd = IsGndStrip(resOtherStrip) || FormatNetName(resOtherNet) == "NET_GND";

                if ((end1Vcc && end2Gnd) || (end1Gnd && end2Vcc))
                {
                    string resistorId = res.RawDetection.SymbolId ?? "R";
                    double resistorOhms = -1;
                    if (BreadboardAOI.Core.Geometry.ResistorColorCode.TryParseOhms(res.RawDetection.DecodedValueLabel ?? res.RawDetection.SymbolId, out double val))
                        resistorOhms = val;

                    if (resistorOhms >= 0 && resistorOhms < 100)
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcResistorCurrentLimitLow,
                            Severity: DrcSeverity.Critical,
                            ComponentId: $"{c.RawDetection.Kind}_{c.RawDetection.TrackId}",
                            OffendingStripId: res.Pin1Hole.StripId,
                            BadHole: res.Pin1Hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Current-limiting resistor ({resistorId}) for LED #{c.RawDetection.TrackId} is too low ({resistorOhms:0}Ω < 100Ω). High risk of burning out the LED. Replace with a 220Ω–1kΩ resistor."));
                    }
                    else if (resistorOhms > 100000)
                    {
                        violations.Add(new DrcViolation(
                            Code: ViolationCode.ErcResistorCurrentLimitHigh,
                            Severity: DrcSeverity.Warning,
                            ComponentId: $"{c.RawDetection.Kind}_{c.RawDetection.TrackId}",
                            OffendingStripId: res.Pin1Hole.StripId,
                            BadHole: res.Pin1Hole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Current-limiting resistor ({resistorId}) for LED #{c.RawDetection.TrackId} is excessively high ({resistorOhms:0}Ω > 100kΩ). LED will appear unlit or dim."));
                    }
                    break;
                }
            }
        }
    }

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
                hash = (hash ^ (uint)component.PinCount) * prime;
                for (int pin = 0; pin < component.PinCount; pin++)
                    hash = (hash ^ (uint)component.GetHole(pin).StripId) * prime;
            }
            else
            {
                int first = component.Pin1Hole.StripId;
                int second = component.Pin2Hole.StripId;

                if (!IsPolarized(component.RawDetection.Kind) && first > second)
                    (first, second) = (second, first);

                hash = (hash ^ (uint)first) * prime;
                hash = (hash ^ (uint)second) * prime;
            }

            // Hash the decoded value label (e.g. "100K", "RED 2.0V", "GREEN 2.1V")
            // so value and colour updates alter the topology hash and trigger a render refresh.
            if (!string.IsNullOrEmpty(component.RawDetection.DecodedValueLabel))
            {
                foreach (char c in component.RawDetection.DecodedValueLabel)
                    hash = (hash ^ (uint)c) * prime;
            }
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
