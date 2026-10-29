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
}
