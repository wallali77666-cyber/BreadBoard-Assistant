// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Design/DemoCircuitCatalog.cs
// PURPOSE: Deterministic circuit snapshots used by Demo Mode and by the X-ray
//          builder's "load example" action. They live in Core so the DRC test
//          suite can assert the engine's verdict on every scenario, and so the
//          builder and the vision loop consume the exact same data.
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.Design;

/// <summary>Faults injected by the Demo Mode selector or loaded into the builder.</summary>
public enum DemoScenario
{
    None = 0,
    SameStripLedShort = 1,
    VccGndShort = 2,
    FloatingLead = 3,
    Valid555Blinker = 4
}

/// <summary>Pre-built detection sets; the arrays are shared and must never be mutated.</summary>
public static class DemoScenarioCatalog
{
    private static readonly DetectedComponent[] SameStripLedShortDetections = BuildSameStripLedShort();
    private static readonly DetectedComponent[] VccGndShortDetections = BuildVccGndShort();
    private static readonly DetectedComponent[] FloatingLeadDetections = BuildFloatingLead();
    private static readonly DetectedComponent[] Valid555Detections = BuildValid555();

    public static IReadOnlyList<DemoScenario> All { get; } = new[]
    {
        DemoScenario.SameStripLedShort,
        DemoScenario.VccGndShort,
        DemoScenario.FloatingLead,
        DemoScenario.Valid555Blinker
    };

    public static string GetTitle(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => "Same-Strip LED Short (Row 15)",
        DemoScenario.VccGndShort => "Direct VCC-GND Jumper Short",
        DemoScenario.FloatingLead => "Floating Resistor Lead",
        DemoScenario.Valid555Blinker => "Valid 555 Astable Blinker",
        _ => "No Demo Scenario"
    };

    public static string GetDescription(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => "LED legs in A15 and D15 share metal clip 115: ERC 101 with a corrective arrow to D16.",
        DemoScenario.VccGndShort => "A jumper bridges TOP_VCC to TOP_GND: ERC 100 critical short plus the emergency reflex alarm.",
        DemoScenario.FloatingLead => "A 220R resistor sits on A30 while its return lead is grounded: ERC 102 floating lead.",
        DemoScenario.Valid555Blinker => "NE555 astable wired to the reference netlist: DRC pass, target ratsnest and tutor praise.",
        _ => "Live YOLO detections from the selected camera source."
    };

    public static DetectedComponent[] GetDetections(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => SameStripLedShortDetections,
        DemoScenario.VccGndShort => VccGndShortDetections,
        DemoScenario.FloatingLead => FloatingLeadDetections,
        DemoScenario.Valid555Blinker => Valid555Detections,
        _ => Array.Empty<DetectedComponent>()
    };

    public static bool UsesTargetSchematic(DemoScenario scenario) => scenario == DemoScenario.Valid555Blinker;

    private static DetectedComponent[] BuildSameStripLedShort() =>
    [
        Part(1, ComponentKind.Led, Hole("A15"), Hole("D15"), 0.96f, "LED_RED_5MM")
    ];

    private static DetectedComponent[] BuildVccGndShort() =>
    [
        Part(1, ComponentKind.JumperWire, Hole("TOP_VCC_25"), Hole("TOP_GND_25"), 0.99f, null)
    ];

    private static DetectedComponent[] BuildFloatingLead() =>
    [
        Part(1, ComponentKind.Resistor, Hole("A30"), Hole("D33"), 0.94f, "220R"),
        Part(2, ComponentKind.JumperWire, Hole("D33"), Hole("TOP_GND_10"), 0.98f, null)
    ];

    private static DetectedComponent[] BuildValid555() =>
    [
        // VCC distribution: rail -> U1 pin 8 -> R1 top -> target net holes.
        Part(1, ComponentKind.JumperWire, Hole("TOP_VCC_25"), Hole("E20"), 0.99f, null),
        Part(2, ComponentKind.JumperWire, Hole("E20"), Hole("F23"), 0.99f, null),
        Part(3, ComponentKind.JumperWire, Hole("F23"), Hole("A18"), 0.99f, null),

        // GND distribution: rail -> U1 pin 1 -> C1 return -> spare ground columns.
        Part(4, ComponentKind.JumperWire, Hole("TOP_GND_25"), Hole("F20"), 0.99f, null),
        Part(5, ComponentKind.JumperWire, Hole("F20"), Hole("J25"), 0.99f, null),
        Part(6, ComponentKind.JumperWire, Hole("J25"), Hole("J24"), 0.99f, null),
        Part(7, ComponentKind.JumperWire, Hole("J24"), Hole("J16"), 0.99f, null),

        // Discharge and trigger/threshold nets.
        Part(8, ComponentKind.JumperWire, Hole("E21"), Hole("B24"), 0.99f, null),
        Part(9, ComponentKind.JumperWire, Hole("F21"), Hole("E22"), 0.99f, null),
        Part(10, ComponentKind.JumperWire, Hole("E22"), Hole("D25"), 0.99f, null),

        // Placed parts: U1 + timing network (C2 omitted: E23 has no second terminal).
        Part(11, ComponentKind.IntegratedCircuit, Hole("E20"), Hole("F20"), 0.98f, "NE555P"),
        Part(12, ComponentKind.Resistor, Hole("A18"), Hole("B24"), 0.97f, "10K"),
        Part(13, ComponentKind.Resistor, Hole("B24"), Hole("D25"), 0.96f, "100K"),
        Part(14, ComponentKind.Capacitor, Hole("D25"), Hole("J25"), 0.95f, "10uF")
    ];

    private static DetectedComponent Part(
        int trackId,
        ComponentKind kind,
        HoleCoord pin1,
        HoleCoord pin2,
        float confidence,
        string? decodedValue)
    {
        const float padding = 12f;

        float width = MathF.Max(18f, MathF.Abs(pin2.Px - pin1.Px) + padding * 2f);
        float height = MathF.Max(18f, MathF.Abs(pin2.Py - pin1.Py) + padding * 2f);

        float left = Math.Clamp(
            MathF.Min(pin1.Px, pin2.Px) - padding,
            0f,
            BreadboardSpec.CanvasWidth - width);

        float top = Math.Clamp(
            MathF.Min(pin1.Py, pin2.Py) - padding,
            0f,
            BreadboardSpec.CanvasHeight - height);

        return new DetectedComponent(
            TrackId: trackId,
            Kind: kind,
            Confidence: confidence,
            BoxX: left,
            BoxY: top,
            BoxW: width,
            BoxH: height,
            Lead1Px: pin1.Px,
            Lead1Py: pin1.Py,
            Lead2Px: pin2.Px,
            Lead2Py: pin2.Py,
            DecodedValueLabel: decodedValue);
    }

    private static HoleCoord Hole(string holeId) =>
        PinMatrixLookup.Shared.TryGetHole(holeId, out HoleCoord hole)
            ? hole
            : new HoleCoord(holeId, 0, 0f, 0f);
}
