using System;
using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.Design;

public sealed record ThermalHotspot(
    int TrackId,
    string ComponentLabel,
    float CenterPx,
    float CenterPy,
    double EstimatedCurrentMa,
    double PowerMilliwatts,
    float NormalizedHeat01,
    bool IsOverloaded,
    string StatusNote);

/// <summary>Core algorithms for DRC Auto-Fix, AI Prompt-to-Circuit Synthesis, and Thermal/Current Estimation.</summary>
public static class ExperimentalLabEngine
{
    private static readonly PinMatrixLookup DefaultMatrix = new();

    public static bool TryAutoFixViolations(
        CircuitDraft draft,
        DrcStatePacket? packet,
        out int fixedCount,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(draft);

        fixedCount = 0;
        var fixNotes = new List<string>();
        var checker = new ElectricalRulesChecker();
        DrcStatePacket currentPacket = packet ?? checker.EvaluateBoard(1, 0, draft.BuildDetections());

        // 1. Remove any direct VCC-to-GND short jumpers
        var directShortIds = ExtractTrackIdsForCode(currentPacket, ViolationCode.ErcVccGndDirectShort);
        for (int i = draft.Parts.Count - 1; i >= 0; i--)
        {
            DraftPart part = draft.Parts[i];
            if (part.Kind != ComponentKind.JumperWire || part.Pins.Length < 2)
                continue;

            bool isRailShort = IsVccRailHole(draft.Matrix, part.Pins[0]) && IsGndRailHole(draft.Matrix, part.Pins[1])
                || IsGndRailHole(draft.Matrix, part.Pins[0]) && IsVccRailHole(draft.Matrix, part.Pins[1]);

            if (isRailShort || directShortIds.Contains(part.TrackId))
            {
                string label = $"Removed VCC-GND short jumper #{part.TrackId} ({part.Pins[0]}->{part.Pins[1]})";
                draft.RemoveAt(i);
                fixedCount++;
                fixNotes.Add(label);
            }
        }

        // Re-evaluate after removing direct shorts
        currentPacket = checker.EvaluateBoard(2, 0, draft.BuildDetections());

        // 2. Fix low-ohms resistors or unprotected LEDs (ErcResistorCurrentLimitLow)
        var ledCurrentIds = ExtractTrackIdsForCode(currentPacket, ViolationCode.ErcResistorCurrentLimitLow);

        // 2a. Upgrade <100 ohm resistors feeding LEDs to 220R
        for (int i = 0; i < draft.Parts.Count; i++)
        {
            DraftPart part = draft.Parts[i];
            if (part.Kind != ComponentKind.Resistor)
                continue;

            if (ResistorColorCode.TryParseOhms(part.Value, out double ohms) && ohms < 100.0 &&
                (ledCurrentIds.Contains(part.TrackId) || IsPartInSeriesWithLed(draft.Parts, draft.Matrix, part)))
            {
                if (draft.TrySetValue(part.TrackId, "220R"))
                {
                    fixedCount++;
                    fixNotes.Add($"Upgraded under-rated resistor #{part.TrackId} ({part.Value}) to 220R");
                }
            }
        }

        // 2b. Fix LEDs wired directly across VCC & GND rails without a series resistor
        var ledSnapshot = draft.Parts.Where(p => p.Kind == ComponentKind.Led && p.Pins.Length >= 2).ToList();
        foreach (DraftPart led in ledSnapshot)
        {
            if (!TryGetRailPair(draft.Matrix, led.Pins[0], led.Pins[1], out string vccHole, out string gndHole))
                continue;

            int freeCol = FindFreeColumn(draft.Parts, draft.Matrix, preferredColumn: ExtractRailIndexOrDefault(vccHole, 5));
            if (freeCol < 1)
                continue;

            string resTerminalHole = $"A{freeCol}";
            string ledAnodeHole = $"B{freeCol}";

            // Move LED first to free vccHole if hole capacity is tight
            if (draft.TryReplacePins(led.TrackId, new[] { ledAnodeHole, gndHole }, out _) &&
                draft.TryAdd(ComponentKind.Resistor, vccHole, resTerminalHole, "220R", out _, out _))
            {
                fixedCount++;
                fixNotes.Add($"Inserted 220R series resistor ({vccHole}->{resTerminalHole}) and re-wired LED #{led.TrackId} ({ledAnodeHole}->{gndHole})");
            }
        }

        // Re-evaluate before polarity / strip repair
        currentPacket = checker.EvaluateBoard(3, 0, draft.BuildDetections());
        var reversedIds = ExtractTrackIdsForCode(currentPacket, ViolationCode.ErcReversedPolarity);
        var sameStripIds = ExtractTrackIdsForCode(currentPacket, ViolationCode.ErcSameStripShort);

        // 3. Fix reversed-polarity LEDs, Diodes, or reversed Sensor/IC power jumpers
        foreach (DraftPart part in draft.Parts.Where(p => p.Kind is ComponentKind.Led or ComponentKind.Diode && p.Pins.Length >= 2).ToList())
        {
            bool looksReversed = reversedIds.Contains(part.TrackId) ||
                IsGndRailHole(draft.Matrix, part.Pins[0]) ||
                IsVccRailHole(draft.Matrix, part.Pins[1]);

            if (looksReversed && draft.TryReplacePins(part.TrackId, new[] { part.Pins[1], part.Pins[0] }, out _))
            {
                fixedCount++;
                fixNotes.Add($"Flipped reversed polarity on {part.Kind} #{part.TrackId}");
            }
        }

        if (reversedIds.Count > 0)
        {
            // Flip any jumper wires attached to power-reversed multi-pin IC/sensor modules
            foreach (DraftPart jumper in draft.Parts.Where(p => p.Kind == ComponentKind.JumperWire && p.Pins.Length >= 2).ToList())
            {
                bool vccOnStripWithGndPart = IsVccRailHole(draft.Matrix, jumper.Pins[0]) || IsVccRailHole(draft.Matrix, jumper.Pins[1]);
                bool gndOnStripWithVccPart = IsGndRailHole(draft.Matrix, jumper.Pins[0]) || IsGndRailHole(draft.Matrix, jumper.Pins[1]);
                if (!vccOnStripWithGndPart && !gndOnStripWithVccPart)
                    continue;

                // Only adjust jumpers attached to reversed multi-pin components' strips
                bool touchesReversedModule = draft.Parts.Any(m =>
                    reversedIds.Contains(m.TrackId) &&
                    m.Pins.Length > 2 &&
                    m.Pins.Any(mp => SharesStrip(draft.Matrix, mp, jumper.Pins[0]) || SharesStrip(draft.Matrix, mp, jumper.Pins[1])));

                if (!touchesReversedModule)
                    continue;

                string p0 = jumper.Pins[0];
                string p1 = jumper.Pins[1];
                if (IsVccRailHole(draft.Matrix, p0)) p0 = "TOP_GND_" + ExtractRailIndexOrDefault(p0, 1);
                else if (IsGndRailHole(draft.Matrix, p0)) p0 = "TOP_VCC_" + ExtractRailIndexOrDefault(p0, 1);
                else if (IsVccRailHole(draft.Matrix, p1)) p1 = "TOP_GND_" + ExtractRailIndexOrDefault(p1, 1);
                else if (IsGndRailHole(draft.Matrix, p1)) p1 = "TOP_VCC_" + ExtractRailIndexOrDefault(p1, 1);

                if ((p0 != jumper.Pins[0] || p1 != jumper.Pins[1]) &&
                    draft.TryReplacePins(jumper.TrackId, new[] { p0, p1 }, out _))
                {
                    fixedCount++;
                    fixNotes.Add($"Corrected reversed power rail on jumper #{jumper.TrackId}");
                }
            }
        }

        // 4. Fix same-strip 2-pin component shorts
        foreach (DraftPart part in draft.Parts.Where(p => p.Pins.Length == 2).ToList())
        {
            if (!draft.Matrix.TryGetHole(part.Pins[0], out HoleCoord h0) ||
                !draft.Matrix.TryGetHole(part.Pins[1], out HoleCoord h1))
                continue;

            if (h0.StripId == h1.StripId || sameStripIds.Contains(part.TrackId))
            {
                if (h0.StripId == h1.StripId &&
                    TryFindFreeAdjacentColumnHole(draft.Parts, draft.Matrix, h1, out HoleCoord newHole) &&
                    draft.TryReplacePins(part.TrackId, new[] { part.Pins[0], newHole.HoleId }, out _))
                {
                    fixedCount++;
                    fixNotes.Add($"Moved shorted pin of {part.Kind} #{part.TrackId} from {part.Pins[1]} to {newHole.HoleId}");
                }
            }
        }

        if (fixedCount == 0)
        {
            summary = "No fixable DRC/ERC violations detected; circuit wiring is clean.";
            return false;
        }

        summary = $"Auto-fixed {fixedCount} issue(s): {string.Join("; ", fixNotes)}.";
        return true;
    }

    public static bool BuildCircuitFromPrompt(
        string prompt,
        CircuitDraft draft,
        out MicrocontrollerBoardType recommendedBoard,
        out List<BoxBlockItem> generatedBlocks,
        out string summary)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            recommendedBoard = MicrocontrollerBoardType.None;
            generatedBlocks = new List<BoxBlockItem>();
            summary = "Prompt cannot be empty.";
            return false;
        }

        string lower = prompt.Trim().ToLowerInvariant();
        draft.Clear();

        if (ContainsAny(lower, "555", "ne555", "timer", "oscillator", "astable", "pulse"))
        {
            recommendedBoard = MicrocontrollerBoardType.ArduinoUnoR3R4;
            Build555OscillatorCircuit(draft);
            generatedBlocks = Build555MonitorBlocks(recommendedBoard);
            summary = "Built NE555P Astable Timer Oscillator with timing capacitors & output indicator LED on Arduino Uno.";
            return true;
        }

        if (ContainsAny(lower, "ultrasonic", "hc-sr04", "hcsr04", "radar", "distance", "sonar"))
        {
            recommendedBoard = MicrocontrollerBoardType.Esp32DevKitV1;
            BuildUltrasonicRadarCircuit(draft);
            generatedBlocks = BuildUltrasonicBlocks(recommendedBoard);
            summary = "Built HC-SR04 Ultrasonic Distance Radar with status LED & musical tone alert on ESP32 DevKit v1.";
            return true;
        }

        if (ContainsAny(lower, "dht", "temp", "temperature", "humidity", "weather", "climate"))
        {
            recommendedBoard = MicrocontrollerBoardType.Esp32DevKitV1;
            BuildDhtWeatherCircuit(draft);
            generatedBlocks = BuildDhtLcdBlocks(recommendedBoard);
            summary = "Built DHT11 Temperature/Humidity Weather Station with I2C pull-ups & LCD1602 display on ESP32 DevKit v1.";
            return true;
        }

        if (ContainsAny(lower, "traffic", "stoplight", "multi-led", "rgb") ||
            (lower.Contains("red", StringComparison.Ordinal) && lower.Contains("green", StringComparison.Ordinal)))
        {
            recommendedBoard = MicrocontrollerBoardType.ArduinoUnoR3R4;
            BuildTrafficLightCircuit(draft);
            generatedBlocks = BuildTrafficLightBlocks(recommendedBoard);
            summary = "Built 3-Channel Traffic Light (Red/Yellow/Green LEDs with 220R current-limiting resistors) on Arduino Uno.";
            return true;
        }

        if (ContainsAny(lower, "doorbell", "buzzer", "alarm", "button", "switch", "siren"))
        {
            recommendedBoard = MicrocontrollerBoardType.ArduinoUnoR3R4;
            BuildDoorbellCircuit(draft);
            generatedBlocks = BuildDoorbellBlocks(recommendedBoard);
            summary = "Built Tactile Push-Button Doorbell & Buzzer Alarm with indicator LED on Arduino Uno.";
            return true;
        }

        // Default fallback starter: protected LED + potentiometer + I2C LCD
        recommendedBoard = MicrocontrollerBoardType.Esp32DevKitV1;
        BuildStarterSmartGadgetCircuit(draft);
        generatedBlocks = BuildStarterSmartBlocks(recommendedBoard);
        summary = "Built Smart LED, Analog Dial & LCD1602 Starter Circuit on ESP32 DevKit v1.";
        return true;
    }

    public static IReadOnlyList<ThermalHotspot> ComputeThermalHotspots(
        IReadOnlyList<DraftPart> parts,
        DrcStatePacket? packet = null,
        PinMatrixLookup? matrix = null)
    {
        if (parts == null || parts.Count == 0)
            return Array.Empty<ThermalHotspot>();

        PinMatrixLookup lookup = matrix ?? DefaultMatrix;
        var criticalTrackIds = new HashSet<int>();
        var criticalStripIds = new HashSet<int>();
        var shortTrackIds = new HashSet<int>();

        if (packet?.Violations != null)
        {
            foreach (DrcViolation v in packet.Violations)
            {
                if (v.Severity != DrcSeverity.Critical)
                    continue;

                if (v.OffendingStripId > 0)
                    criticalStripIds.Add(v.OffendingStripId);

                if (TryParseTrackId(v.ComponentId, out int tid))
                {
                    criticalTrackIds.Add(tid);
                    if (v.Code is ViolationCode.ErcVccGndDirectShort or ViolationCode.ErcSameStripShort or ViolationCode.ErcResistorCurrentLimitLow)
                        shortTrackIds.Add(tid);
                }
            }
        }

        var results = new List<ThermalHotspot>(parts.Count);

        foreach (DraftPart part in parts)
        {
            (float cx, float cy, bool sameStrip) = ComputePartGeometry(part, lookup);
            bool hasCritical = criticalTrackIds.Contains(part.TrackId) || sameStrip;
            bool inShortedZone = shortTrackIds.Contains(part.TrackId) || AnyPinOnStripSet(part, lookup, criticalStripIds);

            (double currentMa, double powerMw, float heat01, bool overloaded, string note) =
                EstimatePartThermal(part, parts, lookup, hasCritical, inShortedZone);

            string label = string.IsNullOrWhiteSpace(part.Value)
                ? $"{part.Kind} #{part.TrackId}"
                : $"{part.Kind} ({part.Value}) #{part.TrackId}";

            results.Add(new ThermalHotspot(
                TrackId: part.TrackId,
                ComponentLabel: label,
                CenterPx: cx,
                CenterPy: cy,
                EstimatedCurrentMa: Math.Round(currentMa, 1),
                PowerMilliwatts: Math.Round(powerMw, 1),
                NormalizedHeat01: Math.Clamp(heat01, 0.0f, 1.0f),
                IsOverloaded: overloaded,
                StatusNote: note));
        }

        return results;
    }

    private static (double CurrentMa, double PowerMw, float Heat01, bool Overloaded, string Note) EstimatePartThermal(
        DraftPart part,
        IReadOnlyList<DraftPart> allParts,
        PinMatrixLookup lookup,
        bool hasCriticalViolation,
        bool inShortedZone)
    {
        switch (part.Kind)
        {
            case ComponentKind.Resistor:
            {
                if (ResistorColorCode.TryParseOhms(part.Value, out double ohms) && ohms > 0.0 && ohms < 47.0)
                {
                    double iMa = (5.0 / ohms) * 1000.0;
                    double pMw = 5.0 * iMa;
                    float heat = Math.Clamp(0.86f + (float)Math.Min(0.14, (47.0 - ohms) / 300.0), 0.85f, 1.0f);
                    return (iMa, pMw, heat, true, $"Overloaded low-resistance part ({ohms:0.#}Ω): high thermal dissipation");
                }

                if (hasCriticalViolation)
                    return (180.0, 900.0, 0.88f, true, "Shorted path through resistor");

                double nominalOhms = ResistorColorCode.TryParseOhms(part.Value, out double parsed) && parsed > 0 ? parsed : 220.0;
                double normalMa = Math.Max(0.8, (3.2 / nominalOhms) * 1000.0);
                double normalMw = Math.Min(350.0, 3.2 * normalMa);
                float normalHeat = Math.Clamp(0.25f + (float)(normalMw / 400.0) * 0.25f, 0.25f, 0.55f);
                return (normalMa, normalMw, normalHeat, false, $"Normal resistive load ({nominalOhms:0.#}Ω)");
            }

            case ComponentKind.Led:
            {
                bool directRail = TryGetRailPair(lookup, part.Pins.FirstOrDefault() ?? "", part.Pins.Skip(1).FirstOrDefault() ?? "", out _, out _);
                bool unguarded = hasCriticalViolation || directRail;
                if (unguarded)
                    return (210.0, 690.0, 0.95f, true, "Unprotected LED junction overcurrent risk");

                return (14.0, 28.0, 0.36f, false, "Nominal current-limited LED illumination");
            }

            case ComponentKind.JumperWire:
            {
                bool directShort = part.Pins.Length >= 2 &&
                    ((IsVccRailHole(lookup, part.Pins[0]) && IsGndRailHole(lookup, part.Pins[1])) ||
                     (IsGndRailHole(lookup, part.Pins[0]) && IsVccRailHole(lookup, part.Pins[1])));

                if (directShort || hasCriticalViolation)
                    return (1200.0, 6000.0, 0.98f, true, "Direct power-rail dead short across jumper");

                return (2.0, 1.0, 0.10f, false, "Passive conductor link");
            }

            case ComponentKind.IntegratedCircuit:
            case ComponentKind.VoltageRegulator:
            case ComponentKind.Microcontroller:
            {
                if (hasCriticalViolation || inShortedZone)
                    return (420.0, 2100.0, 0.92f, true, "Critical power fault on silicon package");

                return (38.0, 190.0, 0.44f, false, "Normal active logic/regulator quiescent load");
            }

            case ComponentKind.Sensor:
            case ComponentKind.Buzzer:
                if (hasCriticalViolation)
                    return (260.0, 1300.0, 0.90f, true, "Electrical fault on module pins");
                return (22.0, 95.0, 0.34f, false, "Nominal transducer / module draw");

            default:
                if (hasCriticalViolation)
                    return (180.0, 720.0, 0.87f, true, "Fault detected on component leads");
                return (5.0, 12.0, 0.25f, false, "Normal passive/control component");
        }
    }

    #region Auto-Fix Geometry & Lookup Helpers

    private static HashSet<int> ExtractTrackIdsForCode(DrcStatePacket packet, ViolationCode code)
    {
        var ids = new HashSet<int>();
        foreach (DrcViolation v in packet.Violations)
        {
            if (v.Code == code && TryParseTrackId(v.ComponentId, out int tid))
                ids.Add(tid);
        }
        return ids;
    }

    private static bool TryParseTrackId(string? componentId, out int trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(componentId))
            return false;

        int us = componentId.LastIndexOf('_');
        return us >= 0 && us + 1 < componentId.Length &&
               int.TryParse(componentId[(us + 1)..], out trackId);
    }

    private static bool SharesStrip(PinMatrixLookup matrix, string holeA, string holeB) =>
        matrix.TryGetHole(holeA, out HoleCoord a) &&
        matrix.TryGetHole(holeB, out HoleCoord b) &&
        a.StripId == b.StripId;

    private static bool IsVccRailHole(PinMatrixLookup matrix, string? holeId) =>
        !string.IsNullOrWhiteSpace(holeId) &&
        matrix.TryGetHole(holeId, out HoleCoord h) &&
        h.StripId is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc;

    private static bool IsGndRailHole(PinMatrixLookup matrix, string? holeId) =>
        !string.IsNullOrWhiteSpace(holeId) &&
        matrix.TryGetHole(holeId, out HoleCoord h) &&
        h.StripId is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd;

    private static bool TryGetRailPair(PinMatrixLookup matrix, string pinA, string pinB, out string vccHole, out string gndHole)
    {
        vccHole = string.Empty;
        gndHole = string.Empty;
        if (IsVccRailHole(matrix, pinA) && IsGndRailHole(matrix, pinB))
        {
            vccHole = pinA;
            gndHole = pinB;
            return true;
        }
        if (IsVccRailHole(matrix, pinB) && IsGndRailHole(matrix, pinA))
        {
            vccHole = pinB;
            gndHole = pinA;
            return true;
        }
        return false;
    }

    private static bool IsPartInSeriesWithLed(IReadOnlyList<DraftPart> parts, PinMatrixLookup matrix, DraftPart resistor)
    {
        if (resistor.Pins.Length < 2) return false;
        if (!matrix.TryGetHole(resistor.Pins[0], out HoleCoord r0) ||
            !matrix.TryGetHole(resistor.Pins[1], out HoleCoord r1))
            return false;

        foreach (DraftPart p in parts)
        {
            if (p.Kind != ComponentKind.Led || p.Pins.Length < 2) continue;
            if (!matrix.TryGetHole(p.Pins[0], out HoleCoord l0) ||
                !matrix.TryGetHole(p.Pins[1], out HoleCoord l1))
                continue;

            if (l0.StripId == r0.StripId || l0.StripId == r1.StripId ||
                l1.StripId == r0.StripId || l1.StripId == r1.StripId)
                return true;
        }

        return false;
    }

    private static int FindFreeColumn(IReadOnlyList<DraftPart> parts, PinMatrixLookup matrix, int preferredColumn)
    {
        var usedCols = new HashSet<int>();
        foreach (DraftPart p in parts)
        {
            foreach (string pin in p.Pins)
            {
                if (matrix.TryGetHole(pin, out HoleCoord h) && h.StripId >= 101 && h.StripId <= 163)
                    usedCols.Add(h.StripId - 100);
            }
        }

        int start = Math.Clamp(preferredColumn, 1, BreadboardSpec.ColumnCount);
        for (int delta = 0; delta < BreadboardSpec.ColumnCount; delta++)
        {
            int right = start + delta;
            if (right <= BreadboardSpec.ColumnCount && !usedCols.Contains(right))
                return right;
            int left = start - delta;
            if (left >= 1 && !usedCols.Contains(left))
                return left;
        }
        return 1;
    }

    private static int ExtractRailIndexOrDefault(string railHoleId, int fallback)
    {
        int us = railHoleId.LastIndexOf('_');
        return us >= 0 && int.TryParse(railHoleId[(us + 1)..], out int idx) ? idx : fallback;
    }

    private static bool TryFindFreeAdjacentColumnHole(
        IReadOnlyList<DraftPart> parts,
        PinMatrixLookup matrix,
        HoleCoord currentHole,
        out HoleCoord freeHole)
    {
        var occupiedStrips = new bool[264];
        foreach (DraftPart p in parts)
        {
            foreach (string pin in p.Pins)
            {
                if (matrix.TryGetHole(pin, out HoleCoord hc) && hc.StripId >= 0 && hc.StripId < occupiedStrips.Length)
                    occupiedStrips[hc.StripId] = true;
            }
        }
        return matrix.TryFindNearestFreeHoleInRow(currentHole, occupiedStrips, out freeHole);
    }

    private static (float Cx, float Cy, bool SameStrip) ComputePartGeometry(DraftPart part, PinMatrixLookup matrix)
    {
        float sumX = 0f, sumY = 0f;
        int count = 0;
        var strips = new HashSet<int>();

        foreach (string pin in part.Pins)
        {
            if (matrix.TryGetHole(pin, out HoleCoord hc))
            {
                sumX += hc.Px;
                sumY += hc.Py;
                count++;
                strips.Add(hc.StripId);
            }
        }

        bool sameStrip = part.Pins.Length == 2 && count == 2 && strips.Count == 1;
        return count > 0
            ? (sumX / count, sumY / count, sameStrip)
            : (BreadboardSpec.CanvasWidth / 2f, BreadboardSpec.CanvasHeight / 2f, false);
    }

    private static bool AnyPinOnStripSet(DraftPart part, PinMatrixLookup matrix, HashSet<int> strips)
    {
        foreach (string pin in part.Pins)
        {
            if (matrix.TryGetHole(pin, out HoleCoord hc) && strips.Contains(hc.StripId))
                return true;
        }
        return false;
    }

    #endregion

    #region Prompt-to-Circuit Synthesis Templates

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.Ordinal));

    private static void BuildUltrasonicRadarCircuit(CircuitDraft draft)
    {
        // HC-SR04 pins: 1:VCC(E10), 2:TRIG(E11), 3:ECHO(E12), 4:GND(E13)
        if (FootprintPlacer.TryPlaceSingleRow(draft.Matrix, "E10", 4, out var ultraPins, out _))
            draft.TryAddFootprint(ComponentKind.Sensor, ultraPins, "HC-SR04", "HC-SR04", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "A10", "TOP_VCC_10", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A13", "TOP_GND_13", "GND", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "D11", "D25", "TRIG Signal", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "D12", "D26", "ECHO Signal", out _, out _);

        // Indicator LED with 220R current-limiting resistor
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_28", "A28", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B28", "TOP_GND_28", "RED 2.0V", out _, out _);

        // Active Buzzer alert (driven via resistor/signal bus)
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_32", "A32", "100R", out _, out _);
        draft.TryAdd(ComponentKind.Buzzer, "B32", "TOP_GND_32", "Piezo 5V", out _, out _);
    }

    private static List<BoxBlockItem> BuildUltrasonicBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var read = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadUltrasonicCm, board);
        read.VariableName = "distCm";

        var cond = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.IfCondition, board);
        cond.VariableName = "distCm";
        cond.ConditionOperator = "<";
        cond.ConditionThreshold = 20.0;

        var ledOn = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, board);
        ledOn.Parameter1 = "HIGH";
        ledOn.IndentationLevel = 1;

        var note = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.PlayToneNote, board);
        note.Parameter1 = "C5";
        note.Parameter2 = "150";
        note.IndentationLevel = 1;

        var wait = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        wait.Parameter1 = "150";

        return new List<BoxBlockItem> { when, forever, read, cond, ledOn, note, wait };
    }

    private static void BuildDhtWeatherCircuit(CircuitDraft draft)
    {
        // DHT11: 1:VCC(E8), 2:DATA(E9), 3:NC(E10 — intentionally unconnected), 4:GND(E11)
        if (FootprintPlacer.TryPlaceSingleRow(draft.Matrix, "E8", 4, out var dhtPins, out _))
            draft.TryAddFootprint(ComponentKind.Sensor, dhtPins, "DHT11", "DHT11", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "A8", "TOP_VCC_8", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_9", "A9", "4.7k", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A11", "TOP_GND_11", "GND", out _, out _);

        // LCD1602-I2C: 1:GND(E16), 2:VCC(E17), 3:SDA(E18), 4:SCL(E19)
        if (FootprintPlacer.TryPlaceSingleRow(draft.Matrix, "E16", 4, out var lcdPins, out _))
            draft.TryAddFootprint(ComponentKind.Sensor, lcdPins, "LCD1602-I2C", "LCD1602-I2C", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "A16", "TOP_GND_16", "GND", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A17", "TOP_VCC_17", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_18", "A18", "4.7k", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_19", "A19", "4.7k", out _, out _);
    }

    private static List<BoxBlockItem> BuildDhtLcdBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var lcdInit = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.LcdInitI2c, board);
        lcdInit.IndentationLevel = 1;
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var readDht = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadDhtSensor, board);
        var lcdPrint = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.LcdPrintSensor, board);
        var log = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SerialPrint, board);
        log.CustomMessage = "Weather sample logged";
        var wait = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        wait.Parameter1 = "1000";

        return new List<BoxBlockItem> { when, lcdInit, forever, readDht, lcdPrint, log, wait };
    }

    private static void BuildTrafficLightCircuit(CircuitDraft draft)
    {
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_10", "A10", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B10", "TOP_GND_10", "RED 2.0V", out _, out _);

        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_14", "A14", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B14", "TOP_GND_14", "YELLOW 2.1V", out _, out _);

        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_18", "A18", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B18", "TOP_GND_18", "GREEN 3.0V", out _, out _);
    }

    private static List<BoxBlockItem> BuildTrafficLightBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var redOn = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.RgbLedColor, board);
        redOn.Parameter1 = "255,0,0";
        var waitRed = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        waitRed.Parameter1 = "1000";
        var yellowOn = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.RgbLedColor, board);
        yellowOn.Parameter1 = "255,180,0";
        var waitYellow = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        waitYellow.Parameter1 = "400";
        var greenOn = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.RgbLedColor, board);
        greenOn.Parameter1 = "0,255,0";
        var ledToggle = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, board);
        ledToggle.Parameter1 = "HIGH";

        return new List<BoxBlockItem> { when, forever, redOn, waitRed, yellowOn, waitYellow, greenOn, ledToggle };
    }

    private static void Build555OscillatorCircuit(CircuitDraft draft)
    {
        // NE555P DIP-8 across trench: 1=E10(GND), 2=E11(TRIG), 3=E12(OUT), 4=E13(RST), 5=F13(CTRL), 6=F12(THR), 7=F11(DIS), 8=F10(VCC)
        if (FootprintPlacer.TryPlaceDualRow(draft.Matrix, "E10", 8, out var dipPins, out _))
            draft.TryAddFootprint(ComponentKind.IntegratedCircuit, dipPins, "NE555P", "NE555P", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "A10", "TOP_GND_10", "GND", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A13", "TOP_VCC_13", "RESET", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "G10", "BOT_VCC_10", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "G11", "BOT_VCC_11", "1k", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "H11", "H12", "10k", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "B11", "G12", "TRIG-THR", out _, out _);
        draft.TryAdd(ComponentKind.Capacitor, "J12", "BOT_GND_12", "10uF", out _, out _);
        draft.TryAdd(ComponentKind.Capacitor, "G13", "BOT_GND_13", "10nF", out _, out _);

        // Pin 3 (E12) Output -> 220R -> LED -> GND
        draft.TryAdd(ComponentKind.Resistor, "A12", "A22", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B22", "TOP_GND_22", "RED 2.0V", out _, out _);
    }

    private static List<BoxBlockItem> Build555MonitorBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var readPot = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadPotentiometer, board);
        var map = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.MapRange, board);
        var led = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, board);
        var wait = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        wait.Parameter1 = "250";

        return new List<BoxBlockItem> { when, forever, readPot, map, led, wait };
    }

    private static void BuildDoorbellCircuit(CircuitDraft draft)
    {
        draft.TryAdd(ComponentKind.JumperWire, "A10", "TOP_VCC_10", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.PushButton, "E10", "F10", "Doorbell BTN", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "G10", "BOT_GND_10", "10k", out _, out _);

        draft.TryAdd(ComponentKind.Resistor, "H10", "H18", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "G18", "BOT_GND_18", "GREEN 3.0V", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "J10", "J24", "Trigger", out _, out _);
        draft.TryAdd(ComponentKind.Buzzer, "G24", "BOT_GND_24", "Doorbell Tone", out _, out _);
    }

    private static List<BoxBlockItem> BuildDoorbellBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var btn = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadButton, board);

        var cond = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.IfCondition, board);
        cond.VariableName = "buttonPressed";
        cond.ConditionOperator = "==";
        cond.ConditionThreshold = 1.0;

        var chime = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.PlayToneNote, board);
        chime.Parameter1 = "E5";
        chime.Parameter2 = "250";
        chime.IndentationLevel = 1;

        var led = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, board);
        led.Parameter1 = "HIGH";
        led.IndentationLevel = 1;

        var wait = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        wait.Parameter1 = "150";

        return new List<BoxBlockItem> { when, forever, btn, cond, chime, led, wait };
    }

    private static void BuildStarterSmartGadgetCircuit(CircuitDraft draft)
    {
        // Protected LED
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_5", "A5", "220R", out _, out _);
        draft.TryAdd(ComponentKind.Led, "B5", "TOP_GND_5", "RED 2.0V", out _, out _);

        // 10k Potentiometer 3-pin
        draft.TryAddFootprint(ComponentKind.Potentiometer, new[] { "E10", "E11", "E12" }, "10k POT", null, out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A10", "TOP_VCC_10", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A12", "TOP_GND_12", "GND", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "B11", "B24", "ADC Tap", out _, out _);

        // LCD1602-I2C
        if (FootprintPlacer.TryPlaceSingleRow(draft.Matrix, "E16", 4, out var lcdPins, out _))
            draft.TryAddFootprint(ComponentKind.Sensor, lcdPins, "LCD1602-I2C", "LCD1602-I2C", out _, out _);

        draft.TryAdd(ComponentKind.JumperWire, "A16", "TOP_GND_16", "GND", out _, out _);
        draft.TryAdd(ComponentKind.JumperWire, "A17", "TOP_VCC_17", "VCC", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_18", "A18", "4.7k", out _, out _);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_19", "A19", "4.7k", out _, out _);
    }

    private static List<BoxBlockItem> BuildStarterSmartBlocks(MicrocontrollerBoardType board)
    {
        var when = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board);
        var lcdInit = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.LcdInitI2c, board);
        lcdInit.IndentationLevel = 1;
        var forever = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board);
        var readPot = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadPotentiometer, board);
        var map = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.MapRange, board);
        var lcdSensor = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.LcdPrintSensor, board);
        var pwm = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetPwm, board);
        var wait = BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board);
        wait.Parameter1 = "300";

        return new List<BoxBlockItem> { when, lcdInit, forever, readPot, map, lcdSensor, pwm, wait };
    }

    #endregion
}
