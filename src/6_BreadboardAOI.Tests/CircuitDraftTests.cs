using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class CircuitDraftTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;

    [Fact]
    public void Draft_builds_detections_at_the_exact_hole_centres()
    {
        var draft = new CircuitDraft(Lookup);

        draft.TryAdd(ComponentKind.Led, "A15", "D15", out DraftPart part, out string? error)
            .Should().BeTrue(error);

        part.TrackId.Should().Be(1);
        part.Pin1HoleId.Should().Be("A15");
        part.Pin2HoleId.Should().Be("D15");
        draft.Revision.Should().Be(1);

        DetectedComponent detection = draft.BuildDetections().Should().ContainSingle().Subject;
        detection.Kind.Should().Be(ComponentKind.Led);
        detection.Confidence.Should().Be(1f);

        Lookup.TrySnapToHole(detection.Lead1Px, detection.Lead1Py, out HoleCoord snapped)
            .Should().BeTrue();
        snapped.HoleId.Should().Be("A15");
    }

    [Fact]
    public void Draft_runs_through_the_real_drc_engine_and_reports_the_same_strip_short()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Led, "A15", "D15", out _, out _).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(1, 0, draft.BuildDetections());

        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcSameStripShort);
        packet.Components.Should().ContainSingle().Which.Net1Name.Should().Be("NET_115");
    }

    [Fact]
    public void Draft_rejects_the_same_hole_twice_and_unknown_labels()
    {
        var draft = new CircuitDraft(Lookup);

        draft.TryAdd(ComponentKind.Resistor, "A15", "A15", out _, out string? sameHole)
            .Should().BeFalse();
        sameHole.Should().Contain("A15");

        draft.TryAdd(ComponentKind.Resistor, "A15", "Z99", out _, out string? badHole)
            .Should().BeFalse();
        badHole.Should().Contain("Z99");

        draft.TryAdd(ComponentKind.Unknown, "A15", "B15", out _, out string? unknown)
            .Should().BeFalse();
        unknown.Should().NotBeNullOrWhiteSpace();

        draft.Count.Should().Be(0);
        draft.Revision.Should().Be(0);
    }

    [Fact]
    public void Draft_undo_removes_the_last_touching_part_and_clear_empties_it()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.JumperWire, "TOP_VCC_10", "A12", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Resistor, "A12", "B30", out _, out _).Should().BeTrue();

        draft.RemoveLastTouching("A12").Should().BeTrue();
        draft.Count.Should().Be(1);

        draft.RemoveLast().Should().BeTrue();
        draft.Count.Should().Be(0);
        draft.BuildDetections().Should().BeEmpty();

        draft.TryAdd(ComponentKind.JumperWire, "TOP_VCC_10", "A12", out _, out _).Should().BeTrue();
        draft.Clear();
        draft.Count.Should().Be(0);
    }

    [Fact]
    public void Demo_scenarios_copy_into_the_draft_without_losing_the_fault()
    {
        var draft = new CircuitDraft(Lookup);
        DetectedComponent[] detections = DemoScenarioCatalog.GetDetections(DemoScenario.SameStripLedShort);

        foreach (DetectedComponent detection in detections)
            draft.TryAddDetection(detection, out _).Should().BeTrue();

        draft.Count.Should().Be(detections.Length);

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(2, 0, draft.BuildDetections());

        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcSameStripShort);
    }

    [Fact]
    public void Builder_copy_of_the_555_demo_still_passes_the_target_netlist()
    {
        TargetSchematic? target = TargetSchematicLoader.TryLoadDefault(out string? error);
        target.Should().NotBeNull(error);

        var draft = new CircuitDraft(Lookup);
        DetectedComponent[] detections = DemoScenarioCatalog.GetDetections(DemoScenario.Valid555Blinker);

        foreach (DetectedComponent detection in detections)
            draft.TryAddDetection(detection, out _).Should().BeTrue();

        draft.Count.Should().Be(detections.Length);

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(3, 0, draft.BuildDetections(), target);

        packet.Violations.Should().BeEmpty(
            "a part copied through the builder must snap back to the same holes: {0}",
            string.Join(" | ", System.Linq.Enumerable.Select(packet.Violations, v => v.Code + " " + v.TechnicalMessage)));
    }

    [Fact]
    public void Draft_undo_and_redo_stack_restores_circuit_states()
    {
        var draft = new CircuitDraft(Lookup);
        draft.CanUndo.Should().BeFalse();
        draft.CanRedo.Should().BeFalse();

        draft.TryAdd(ComponentKind.Resistor, "A1", "A5", out _, out _).Should().BeTrue();
        draft.Count.Should().Be(1);
        draft.CanUndo.Should().BeTrue();
        draft.CanRedo.Should().BeFalse();

        draft.TryAdd(ComponentKind.Led, "B1", "B5", out _, out _).Should().BeTrue();
        draft.Count.Should().Be(2);

        draft.TryUndo().Should().BeTrue();
        draft.Count.Should().Be(1);
        draft.CanRedo.Should().BeTrue();

        draft.TryUndo().Should().BeTrue();
        draft.Count.Should().Be(0);
        draft.CanUndo.Should().BeFalse();

        draft.TryRedo().Should().BeTrue();
        draft.Count.Should().Be(1);
        draft.CanUndo.Should().BeTrue();

        draft.TryRedo().Should().BeTrue();
        draft.Count.Should().Be(2);
        draft.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Draft_RestoreParts_RestoresPartsAndIncrementsRevision()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Resistor, "A1", "A5", out _, out _).Should().BeTrue();
        int initialRev = draft.Revision;

        var snapshot = draft.Parts.ToList();
        draft.Clear();
        draft.Count.Should().Be(0);

        draft.RestoreParts(snapshot);
        draft.Count.Should().Be(1);
        draft.Parts[0].Kind.Should().Be(ComponentKind.Resistor);
        draft.Revision.Should().BeGreaterThan(initialRev);
    }

    [Fact]
    public void Draft_supports_battery_power_source_component()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Battery, "TOP_VCC_1", "BOT_GND_1", out DraftPart battery, out string? error)
            .Should().BeTrue(error);

        battery.Kind.Should().Be(ComponentKind.Battery);
        battery.Pin1HoleId.Should().Be("TOP_VCC_1");
        battery.Pin2HoleId.Should().Be("BOT_GND_1");

        DetectedComponent[] detections = draft.BuildDetections();
        detections.Should().ContainSingle();
        detections[0].Kind.Should().Be(ComponentKind.Battery);
    }

    [Theory]
    [InlineData("220", 220.0)]
    [InlineData("220R", 220.0)]
    [InlineData("1K", 1000.0)]
    [InlineData("4.7k", 4700.0)]
    [InlineData("4K7", 4700.0)]
    [InlineData("10K", 10000.0)]
    [InlineData("100k", 100000.0)]
    [InlineData("1M", 1000000.0)]
    public void ResistorColorCode_parses_nominal_ohms_accurately(string valueText, double expectedOhms)
    {
        ResistorColorCode.TryParseOhms(valueText, out double ohms).Should().BeTrue();
        ohms.Should().BeApproximately(expectedOhms, 0.001);
    }

    [Theory]
    [InlineData("220R", new int[] { 2, 2, 1, 10 })]
    [InlineData("330", new int[] { 3, 3, 1, 10 })]
    [InlineData("1k", new int[] { 1, 0, 2, 10 })]
    [InlineData("4k7", new int[] { 4, 7, 2, 10 })]
    [InlineData("10k", new int[] { 1, 0, 3, 10 })]
    [InlineData("100K", new int[] { 1, 0, 4, 10 })]
    [InlineData("1M", new int[] { 1, 0, 5, 10 })]
    public void ResistorColorCode_decodes_color_bands_accurately(string valueText, int[] expectedBands)
    {
        Span<int> bands = stackalloc int[4];
        ResistorColorCode.TryDecodeBands(valueText, bands).Should().BeTrue();
        bands[0].Should().Be(expectedBands[0]);
        bands[1].Should().Be(expectedBands[1]);
        bands[2].Should().Be(expectedBands[2]);
        bands[3].Should().Be(expectedBands[3]);
    }

    [Fact]
    public void Draft_updating_resistor_value_updates_detection_and_color_bands()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Resistor, "F48", "F54", out DraftPart part, out string? error)
            .Should().BeTrue(error);

        part.Value.Should().Be("220R");
        DetectedComponent[] detections = draft.BuildDetections();
        detections.Should().ContainSingle();
        detections[0].DecodedValueLabel.Should().Be("220R");

        Span<int> bands = stackalloc int[4];
        ResistorColorCode.TryDecodeBands(detections[0].DecodedValueLabel, bands).Should().BeTrue();
        bands.ToArray().Should().Equal([2, 2, 1, 10]); // Red, Red, Brown, Gold

        // Live update value to 100K (matching user scenario)
        draft.TrySetValue(part.TrackId, "100K").Should().BeTrue();
        DetectedComponent[] updatedDetections = draft.BuildDetections();
        updatedDetections.Should().ContainSingle();
        updatedDetections[0].DecodedValueLabel.Should().Be("100K");

        ResistorColorCode.TryDecodeBands(updatedDetections[0].DecodedValueLabel, bands).Should().BeTrue();
        bands.ToArray().Should().Equal([1, 0, 4, 10]); // Brown, Black, Yellow, Gold
    }

    [Fact]
    public void Draft_updating_led_color_updates_detection_and_value()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Led, "C28", "B35", out DraftPart part, out string? error)
            .Should().BeTrue(error);

        part.Value.Should().Be("LED_RED_5MM");
        DetectedComponent[] detections = draft.BuildDetections();
        detections.Should().ContainSingle();
        detections[0].DecodedValueLabel.Should().Be("LED_RED_5MM");

        // Live update value to GREEN 2.1V (matching user scenario)
        draft.TrySetValue(part.TrackId, "GREEN 2.1V").Should().BeTrue();
        DetectedComponent[] updatedDetections = draft.BuildDetections();
        updatedDetections.Should().ContainSingle();
        updatedDetections[0].DecodedValueLabel.Should().Be("GREEN 2.1V");

        // Live update value to BLUE 3.2V
        draft.TrySetValue(part.TrackId, "BLUE 3.2V").Should().BeTrue();
        draft.BuildDetections()[0].DecodedValueLabel.Should().Be("BLUE 3.2V");
    }
}
