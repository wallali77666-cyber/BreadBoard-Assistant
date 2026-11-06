// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/IcDrcEngineTests.cs
// PURPOSE: Automated unit tests verifying KiCad-grade DRC rules for Integrated Circuits:
//          - Trench misplacement & opposing pin shorts across the center trench
//          - VCC-to-GND direct power shorts across the IC
//          - Reversed power polarity detection
//          - Floating power/ground pin detection with corrective rail vectors
// ============================================================================

using System;
using System.Linq;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class IcDrcEngineTests
{
    private readonly ElectricalRulesChecker _checker = new();
    private readonly PinMatrixLookup _matrix = PinMatrixLookup.Shared;

    [Fact]
    public void DIP_IC_properly_straddling_center_trench_has_no_opposing_pin_shorts()
    {
        // Place a DIP-8 (e.g. NE555) straddling the trench:
        // Pins 1..4 on E20..E23 (Strip IDs 120..123)
        // Pins 5..8 on F23..F20 (Strip IDs 223..220)
        _matrix.TryGetHole("E20", out HoleCoord h1);
        _matrix.TryGetHole("E21", out HoleCoord h2);
        _matrix.TryGetHole("E22", out HoleCoord h3);
        _matrix.TryGetHole("E23", out HoleCoord h4);
        _matrix.TryGetHole("F23", out HoleCoord h5);
        _matrix.TryGetHole("F22", out HoleCoord h6);
        _matrix.TryGetHole("F21", out HoleCoord h7);
        _matrix.TryGetHole("F20", out HoleCoord h8);

        LeadPoint[] extra =
        [
            new(h3.Px, h3.Py),
            new(h4.Px, h4.Py),
            new(h5.Px, h5.Py),
            new(h6.Px, h6.Py),
            new(h7.Px, h7.Py),
            new(h8.Px, h8.Py)
        ];

        DetectedComponent ic = new(
            TrackId: 1,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "NE555P",
            SymbolId: "Timer/NE555P",
            ExtraLeads: extra);

        // Power jumper from VCC rail to pin 8 (F20)
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 20, out HoleCoord vccRail);
        DetectedComponent vccWire = new(
            TrackId: 2,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: h8.Px, Lead2Py: h8.Py);

        // Ground jumper from GND rail to pin 1 (E20)
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 20, out HoleCoord gndRail);
        DetectedComponent gndWire = new(
            TrackId: 3,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: h1.Px, Lead2Py: h1.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [ic, vccWire, gndWire]);

        // Must not have same-strip shorts between opposite pins
        packet.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcSameStripShort);
        packet.HasCriticalShort.Should().BeFalse();
    }

    [Fact]
    public void DIP_IC_placed_without_straddling_trench_fails_DRC_with_trench_short()
    {
        // Faulty placement: DIP-8 placed entirely in upper half (Row A and Row E of Col 20..23).
        // Pin 1 is in A20 (Strip 120), Pin 8 is in E20 (Strip 120)! Dead short!
        _matrix.TryGetHole("A20", out HoleCoord h1);
        _matrix.TryGetHole("A21", out HoleCoord h2);
        _matrix.TryGetHole("A22", out HoleCoord h3);
        _matrix.TryGetHole("A23", out HoleCoord h4);
        _matrix.TryGetHole("E23", out HoleCoord h5);
        _matrix.TryGetHole("E22", out HoleCoord h6);
        _matrix.TryGetHole("E21", out HoleCoord h7);
        _matrix.TryGetHole("E20", out HoleCoord h8);

        LeadPoint[] extra =
        [
            new(h3.Px, h3.Py),
            new(h4.Px, h4.Py),
            new(h5.Px, h5.Py),
            new(h6.Px, h6.Py),
            new(h7.Px, h7.Py),
            new(h8.Px, h8.Py)
        ];

        DetectedComponent faultyIc = new(
            TrackId: 10,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "NE555P",
            SymbolId: "Timer/NE555P",
            ExtraLeads: extra);

        DrcStatePacket packet = _checker.EvaluateBoard(2, 0.5, [faultyIc]);

        // Must emit critical ErcSameStripShort explaining the trench failure
        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcSameStripShort &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("straddle the center divider trench"));
    }

    [Fact]
    public void IC_with_VCC_and_GND_shorted_together_emits_critical_power_short()
    {
        // DIP-8 straddling trench, but a jumper bridges pin 8 (VCC) directly to pin 1 (GND)
        _matrix.TryGetHole("E20", out HoleCoord h1); // Pin 1 (GND)
        _matrix.TryGetHole("E21", out HoleCoord h2);
        _matrix.TryGetHole("E22", out HoleCoord h3);
        _matrix.TryGetHole("E23", out HoleCoord h4);
        _matrix.TryGetHole("F23", out HoleCoord h5);
        _matrix.TryGetHole("F22", out HoleCoord h6);
        _matrix.TryGetHole("F21", out HoleCoord h7);
        _matrix.TryGetHole("F20", out HoleCoord h8); // Pin 8 (VCC)

        LeadPoint[] extra =
        [
            new(h3.Px, h3.Py),
            new(h4.Px, h4.Py),
            new(h5.Px, h5.Py),
            new(h6.Px, h6.Py),
            new(h7.Px, h7.Py),
            new(h8.Px, h8.Py)
        ];

        DetectedComponent ic = new(
            TrackId: 20,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "NE555P",
            SymbolId: "Timer/NE555P",
            ExtraLeads: extra);

        // Accidental shorting jumper between Pin 1 and Pin 8
        DetectedComponent shortingWire = new(
            TrackId: 21,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 10, BoxH: 10,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h8.Px, Lead2Py: h8.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(3, 0.5, [ic, shortingWire]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcVccGndDirectShort &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("Direct power short across IC"));
    }

    [Fact]
    public void IC_with_reversed_power_polarity_emits_critical_violation()
    {
        // DIP-8 straddling trench, but pin 8 (VCC) is wired to GND rail and pin 1 (GND) is wired to VCC rail!
        _matrix.TryGetHole("E20", out HoleCoord h1); // Pin 1 (GND)
        _matrix.TryGetHole("E21", out HoleCoord h2);
        _matrix.TryGetHole("E22", out HoleCoord h3);
        _matrix.TryGetHole("E23", out HoleCoord h4);
        _matrix.TryGetHole("F23", out HoleCoord h5);
        _matrix.TryGetHole("F22", out HoleCoord h6);
        _matrix.TryGetHole("F21", out HoleCoord h7);
        _matrix.TryGetHole("F20", out HoleCoord h8); // Pin 8 (VCC)

        LeadPoint[] extra =
        [
            new(h3.Px, h3.Py),
            new(h4.Px, h4.Py),
            new(h5.Px, h5.Py),
            new(h6.Px, h6.Py),
            new(h7.Px, h7.Py),
            new(h8.Px, h8.Py)
        ];

        DetectedComponent ic = new(
            TrackId: 30,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "NE555P",
            SymbolId: "Timer/NE555P",
            ExtraLeads: extra);

        // Pin 8 (VCC) incorrectly wired to GND rail!
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 20, out HoleCoord gndRail);
        DetectedComponent badVccWire = new(
            TrackId: 31,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: h8.Px, Lead2Py: h8.Py);

        // Pin 1 (GND) incorrectly wired to VCC rail!
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 20, out HoleCoord vccRail);
        DetectedComponent badGndWire = new(
            TrackId: 32,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: h1.Px, Lead2Py: h1.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(4, 0.5, [ic, badVccWire, badGndWire]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcReversedPolarity &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("Reverse power polarity on IC"));
    }

    [Fact]
    public void IC_with_unconnected_power_pins_emits_floating_power_warnings_with_rail_suggestions()
    {
        // DIP-8 straddling trench with no jumpers attached to VCC or GND
        _matrix.TryGetHole("E20", out HoleCoord h1);
        _matrix.TryGetHole("E21", out HoleCoord h2);
        _matrix.TryGetHole("E22", out HoleCoord h3);
        _matrix.TryGetHole("E23", out HoleCoord h4);
        _matrix.TryGetHole("F23", out HoleCoord h5);
        _matrix.TryGetHole("F22", out HoleCoord h6);
        _matrix.TryGetHole("F21", out HoleCoord h7);
        _matrix.TryGetHole("F20", out HoleCoord h8);

        LeadPoint[] extra =
        [
            new(h3.Px, h3.Py),
            new(h4.Px, h4.Py),
            new(h5.Px, h5.Py),
            new(h6.Px, h6.Py),
            new(h7.Px, h7.Py),
            new(h8.Px, h8.Py)
        ];

        DetectedComponent unpoweredIc = new(
            TrackId: 40,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "NE555P",
            SymbolId: "Timer/NE555P",
            ExtraLeads: extra);

        DrcStatePacket packet = _checker.EvaluateBoard(5, 0.5, [unpoweredIc]);

        // Should flag floating VCC and floating GND warnings pointing to the rails
        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcFloatingLead &&
            v.Severity == DrcSeverity.Warning &&
            v.TechnicalMessage.Contains("power pin 8 (VCC)") &&
            BreadboardSpec.IsRailStrip(v.SuggestedTargetHole.StripId));

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcFloatingLead &&
            v.Severity == DrcSeverity.Warning &&
            v.TechnicalMessage.Contains("ground pin 1 (GND)") &&
            BreadboardSpec.IsRailStrip(v.SuggestedTargetHole.StripId));
    }
}
