// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/MultiPinFootprintTests.cs
// PURPOSE: Locks the multi-lead footprint behaviour added for DIP and 3-pin
//          parts: hole generation across the trench, draft round-tripping,
//          image-import expansion, and the DRC rule that fires when two pins of
//          one package share a metal clip.
// ============================================================================

using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class MultiPinFootprintTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;

    [Fact]
    public void Dip_8_from_E10_walks_up_one_row_and_back_along_the_other()
    {
        bool placed = FootprintPlacer.TryPlaceDualRow(Lookup, "E10", 8, out string[] pins, out string? error);

        placed.Should().BeTrue(error);
        pins.Should().Equal("E10", "E11", "E12", "E13", "F13", "F12", "F11", "F10");
    }

    [Fact]
    public void Dip_started_on_the_lower_half_still_straddles_the_trench()
    {
        bool placed = FootprintPlacer.TryPlaceDualRow(Lookup, "H20", 14, out string[] pins, out string? error);

        placed.Should().BeTrue(error);
        pins.Should().HaveCount(14);
        pins[0].Should().Be("F20", "pin 1 lands on the inner row of the clicked half");
        pins[6].Should().Be("F26");
        pins[7].Should().Be("E26");
        pins[13].Should().Be("E20");

        // Every pin must be on a distinct strip or the package shorts itself.
        int[] strips = pins.Select(static hole => StripOf(hole)).ToArray();
        strips.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Dip_that_runs_past_column_63_is_refused_with_a_clear_error()
    {
        FootprintPlacer.TryPlaceDualRow(Lookup, "E61", 8, out _, out string? error).Should().BeFalse();
        error.Should().Contain("63");
    }

    [Fact]
    public void Dip_cannot_start_on_a_power_rail()
    {
        FootprintPlacer.TryPlaceDualRow(Lookup, "TOP_VCC_10", 8, out _, out string? error).Should().BeFalse();
        error.Should().Contain("terminal tie point");
    }

    [Fact]
    public void Three_pin_row_uses_adjacent_columns()
    {
        FootprintPlacer.TryPlaceSingleRow(Lookup, "B12", 3, out string[] pins, out string? error)
            .Should().BeTrue(error);

        pins.Should().Equal("B12", "B13", "B14");
        pins.Select(static hole => StripOf(hole)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Draft_keeps_every_pin_of_a_dip_in_order()
    {
        var draft = new CircuitDraft(Lookup);
        string[] pins = BuildDip(10, 8);

        draft.TryAddFootprint(ComponentKind.IntegratedCircuit, pins, "NE555P", "Timer/NE555P", out DraftPart part, out string? error)
            .Should().BeTrue(error);

        part.PinCount.Should().Be(8);
        part.Pin1HoleId.Should().Be("E10");
        part.Pin2HoleId.Should().Be("E11");
        part.Value.Should().Be("NE555P");
        part.SymbolId.Should().Be("Timer/NE555P");

        DetectedComponent detection = draft.BuildDetections().Should().ContainSingle().Subject;
        detection.PinCount.Should().Be(8);
        detection.IsMultiPin.Should().BeTrue();
        detection.SymbolId.Should().Be("Timer/NE555P");
        detection.ExtraLeads.Length.Should().Be(6);

        // The first extra lead is pin 3 at E12.
        Lookup.TrySnapToHole(detection.ExtraLeads.Span[0].Px, detection.ExtraLeads.Span[0].Py, out HoleCoord pin3)
            .Should().BeTrue();
        pin3.HoleId.Should().Be("E12");
    }

    [Fact]
    public void Draft_rejects_a_footprint_that_shorts_two_pins_into_one_hole()
    {
        var draft = new CircuitDraft(Lookup);

        draft.TryAddFootprint(
                ComponentKind.IntegratedCircuit,
                new[] { "E10", "E10", "E11", "E12" },
                "DIP-4",
                null,
                out _,
                out string? error)
            .Should().BeFalse();

        error.Should().Contain("short onto itself");
    }

    [Fact]
    public void Drc_accepts_a_dip_whose_pins_share_a_net_through_a_jumper()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAddFootprint(ComponentKind.IntegratedCircuit, BuildDip(10, 8), "NE555P", null, out _, out _).Should().BeTrue();

        // Tie pins 1 and 8 to the same top rail with two jumpers: legitimate.
        draft.TryAdd(ComponentKind.JumperWire, "TOP_VCC_10", "E10", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.JumperWire, "TOP_VCC_11", "E17", out _, out _).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        DrcStatePacket packet = checker.EvaluateBoard(1, 0, draft.BuildDetections());

        packet.Violations.Should().NotContain(
            v => v.Code == ViolationCode.ErcSameStripShort,
            "a DIP with two power pins is normal, only same-clip pins are a short");
    }

    [Fact]
    public void Drc_flags_two_pins_of_one_dip_in_the_same_clip()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAddFootprint(ComponentKind.IntegratedCircuit, BuildDip(10, 8), "DIP-8", null, out _, out _).Should().BeTrue();

        // Move pin 3 onto the same clip as pin 1 by hand: E10 and A10 share strip 110.
        DetectedComponent[] detections = draft.BuildDetections();
        DetectedComponent dip = detections[0];
        LeadPoint[] extras = dip.ExtraLeads.ToArray();

        Lookup.TryGetHole("A10", out HoleCoord a10).Should().BeTrue();
        extras[0] = new LeadPoint(a10.Px, a10.Py);
        detections[0] = dip with { ExtraLeads = extras };

        var checker = new ElectricalRulesChecker(Lookup);
        DrcStatePacket packet = checker.EvaluateBoard(2, 0, detections);

        DrcViolation violation = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcSameStripShort).Subject;

        violation.TechnicalMessage.Should()
            .Contain("Move pin 3")
            .And.Contain("pin 1 at E10");
        violation.OffendingStripId.Should().Be(StripOf("A10"));
    }

    [Fact]
    public void Import_expands_a_detected_ic_into_the_requested_dip()
    {
        var draft = new CircuitDraft(Lookup);
        DetectedComponent ic = new(
            TrackId: 1,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.9f,
            BoxX: 0f, BoxY: 0f, BoxW: 80f, BoxH: 40f,
            Lead1Px: HoleX("E20"), Lead1Py: HoleY("E20"),
            Lead2Px: HoleX("E23"), Lead2Py: HoleY("E23"),
            DecodedValueLabel: "DIP");

        int added = draft.TryImportDetections(new[] { ic }, replaceExisting: true, out int skipped, icPinCount: 8, icSymbolId: "Timer/NE555P");

        added.Should().Be(1);
        skipped.Should().Be(0);

        DraftPart part = draft.Parts.Should().ContainSingle().Subject;
        part.PinCount.Should().Be(8);
        part.Pins[0].Should().Be("E20");
        part.Value.Should().Be("DIP-8", "the placeholder label is replaced by the real package");
        part.SymbolId.Should().Be("Timer/NE555P");
    }

    [Fact]
    public void Import_without_a_pin_count_keeps_the_two_lead_detection()
    {
        var draft = new CircuitDraft(Lookup);
        DetectedComponent ic = new(
            TrackId: 1,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.9f,
            BoxX: 0f, BoxY: 0f, BoxW: 80f, BoxH: 40f,
            Lead1Px: HoleX("E20"), Lead1Py: HoleY("E20"),
            Lead2Px: HoleX("E23"), Lead2Py: HoleY("E23"));

        draft.TryImportDetections(new[] { ic }, replaceExisting: true, out _).Should().Be(1);
        draft.Parts[0].PinCount.Should().Be(2);
    }

    [Fact]
    public void Value_and_symbol_editing_round_trips_into_the_detection()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Resistor, "A5", "B9", out DraftPart part, out _).Should().BeTrue();

        draft.TrySetValue(part.TrackId, "4K7").Should().BeTrue();
        draft.TrySetSymbol(part.TrackId, "Device/R_Small").Should().BeTrue();

        DetectedComponent detection = draft.BuildDetections().Should().ContainSingle().Subject;
        detection.DecodedValueLabel.Should().Be("4K7");
        detection.SymbolId.Should().Be("Device/R_Small");

        draft.TrySetValue(999, "1K").Should().BeFalse();
        draft.TrySetSymbol(999, "Device/R").Should().BeFalse();
    }

    private static string[] BuildDip(int column, int pinCount)
    {
        FootprintPlacer.TryPlaceDualRow(Lookup, $"E{column}", pinCount, out string[] pins, out string? error)
            .Should().BeTrue(error);

        return pins;
    }

    private static int StripOf(string holeId)
    {
        Lookup.TryGetHole(holeId, out HoleCoord hole).Should().BeTrue(holeId);
        return hole.StripId;
    }

    private static float HoleX(string holeId)
    {
        Lookup.TryGetHole(holeId, out HoleCoord hole).Should().BeTrue(holeId);
        return hole.Px;
    }

    private static float HoleY(string holeId)
    {
        Lookup.TryGetHole(holeId, out HoleCoord hole).Should().BeTrue(holeId);
        return hole.Py;
    }
}
