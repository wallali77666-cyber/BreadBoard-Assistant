using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class ElectricalRulesCheckerTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;

    [Fact]
    public void Jumper_from_VCC_rail_to_A12_resolves_strip_112_to_NET_VCC()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 12, out var vcc).Should().BeTrue();
        Lookup.TryGetHole("A12", out var a12).Should().BeTrue();

        var jumper = Create(ComponentKind.JumperWire, vcc, a12);
        var checker = new ElectricalRulesChecker(Lookup);

        var packet = checker.EvaluateBoard(7, 1.2, new[] { jumper });

        checker.GetResolvedNetName(112).Should().Be("NET_VCC");
        checker.GetRootNet(112).Should().Be(BreadboardSpec.StripTopVcc);
        checker.AreSameNet(BreadboardSpec.StripTopVcc, 112).Should().BeTrue();
        packet.Components.Should().ContainSingle().Which.Net1Name.Should().Be("NET_VCC");
        packet.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Chained_jumpers_propagate_the_canonical_VCC_root()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 10, out var vcc).Should().BeTrue();
        Lookup.TryGetHole("A12", out var a12).Should().BeTrue();
        Lookup.TryGetHole("B40", out var b40).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        checker.EvaluateBoard(1, 0.8, new[]
        {
            Create(ComponentKind.JumperWire, vcc, a12, trackId: 1),
            Create(ComponentKind.JumperWire, a12, b40, trackId: 2)
        });

        checker.GetResolvedNetName(140).Should().Be("NET_VCC");
        checker.GetRootNet(112).Should().Be(checker.GetRootNet(140));
    }

    [Fact]
    public void Led_with_both_legs_in_A15_and_D15_emits_same_strip_short_with_D16_target_under_100_microseconds()
    {
        Lookup.TryGetHole("A15", out var a15).Should().BeTrue();
        Lookup.TryGetHole("D15", out var d15).Should().BeTrue();

        var led = Create(ComponentKind.Led, a15, d15, trackId: 3);
        var checker = new ElectricalRulesChecker(Lookup);

        // Warm up JIT so the measured frame reflects steady-state DRC cost.
        checker.EvaluateBoard(0, 0, Array.Empty<DetectedComponent>());

        var packet = checker.EvaluateBoard(42, 5.8, new[] { led });

        var violation = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcSameStripShort).Subject;

        violation.Severity.Should().Be(DrcSeverity.Critical);
        violation.OffendingStripId.Should().Be(115);
        violation.BadHole.HoleId.Should().Be("D15");
        violation.SuggestedTargetHole.HoleId.Should().Be("D16");
        violation.SuggestedTargetHole.StripId.Should().Be(116);

        packet.DrcComputeMs.Should().BeLessThan(0.1,
            "the reflex loop gives the KiCad DRC engine a 100 microsecond budget");
    }

    [Fact]
    public void Vcc_gnd_bridge_emits_critical_short()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 10, out var vcc).Should().BeTrue();
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 10, out var gnd).Should().BeTrue();

        var jumper = Create(ComponentKind.JumperWire, vcc, gnd);
        var checker = new ElectricalRulesChecker(Lookup);

        var packet = checker.EvaluateBoard(9, 0.5, new[] { jumper });

        packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcVccGndDirectShort)
            .Which.Severity.Should().Be(DrcSeverity.Critical);

        packet.HasCriticalShort.Should().BeTrue();
    }

    [Fact]
    public void Lone_terminal_on_an_isolated_strip_emits_floating_lead_warnings()
    {
        Lookup.TryGetHole("A30", out var a30).Should().BeTrue();
        Lookup.TryGetHole("B45", out var b45).Should().BeTrue();

        var resistor = Create(ComponentKind.Resistor, a30, b45, trackId: 11);
        var checker = new ElectricalRulesChecker(Lookup);

        var packet = checker.EvaluateBoard(12, 1.0, new[] { resistor });
        var floating = packet.Violations.Where(v => v.Code == ViolationCode.ErcFloatingLead).ToArray();

        floating.Should().HaveCount(2);
        floating.Should().OnlyContain(v => v.Severity == DrcSeverity.Warning);
        floating.Select(v => v.BadHole.HoleId).Should().BeEquivalentTo("A30", "B45");
    }

    [Fact]
    public void Power_rail_with_a_single_terminal_is_not_reported_as_floating()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 25, out var vcc).Should().BeTrue();
        Lookup.TryGetHole("A30", out var a30).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(13, 1.0, new[]
        {
            Create(ComponentKind.Resistor, vcc, a30, trackId: 12)
        });

        packet.Violations.Should().NotContain(v =>
            v.Code == ViolationCode.ErcFloatingLead && BreadboardSpec.IsRailStrip(v.OffendingStripId));
    }

    [Fact]
    public void Empty_board_reports_no_violations()
    {
        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(1, 0.4, Array.Empty<DetectedComponent>());

        packet.Violations.Should().BeEmpty();
        packet.Components.Should().BeEmpty();
        packet.StateHash.Should().NotBe(0);
    }

    [Fact]
    public void Target_parity_passes_when_target_holes_are_jumpered()
    {
        var target = TargetSchematicLoader.Parse("""
            { "name": "mini", "nets": [ { "name": "NET_OUT", "holes": [ "A10", "F10" ] } ] }
            """);

        Lookup.TryGetHole("A10", out var a10).Should().BeTrue();
        Lookup.TryGetHole("F10", out var f10).Should().BeTrue();

        var jumper = Create(ComponentKind.JumperWire, a10, f10);
        var checker = new ElectricalRulesChecker(Lookup);

        var packet = checker.EvaluateBoard(20, 1.0, new[] { jumper }, target);

        packet.Violations.Should().NotContain(v => v.Code == ViolationCode.DrcSchematicNetMismatch);
    }

    [Fact]
    public void Target_parity_reports_mismatch_with_ratsnest_target()
    {
        var target = TargetSchematicLoader.Parse("""
            { "name": "mini", "nets": [ { "name": "NET_OUT", "holes": [ "A10", "F10" ] } ] }
            """);

        Lookup.TryGetHole("A10", out var a10).Should().BeTrue();
        Lookup.TryGetHole("F10", out var f10).Should().BeTrue();
        Lookup.TryGetHole("C20", out var c20).Should().BeTrue();
        Lookup.TryGetHole("J40", out var j40).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(21, 1.0, new[]
        {
            Create(ComponentKind.Resistor, a10, c20, trackId: 21),
            Create(ComponentKind.Resistor, f10, j40, trackId: 22)
        }, target);

        var mismatch = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.DrcSchematicNetMismatch).Subject;

        mismatch.Severity.Should().Be(DrcSeverity.Warning);
        mismatch.ComponentId.Should().Be("NET_OUT");
        mismatch.BadHole.HoleId.Should().Be("F10");
        mismatch.SuggestedTargetHole.HoleId.Should().Be("A10");
    }

    [Fact]
    public void Shipped_target_netlist_produces_ratsnest_air_wires()
    {
        TargetSchematic? target = TargetSchematicLoader.TryLoadDefault(out string? error);
        target.Should().NotBeNull(error);

        var checker = new ElectricalRulesChecker(Lookup);
        checker.EvaluateBoard(30, 1.0, Array.Empty<DetectedComponent>(), target);

        var ratsnest = TargetSchematicLoader.ComputeRatsnest(target!, Lookup, checker);
        ratsnest.Should().NotBeEmpty();
        ratsnest.Should().OnlyContain(v => v.From.HoleId != v.To.HoleId);
    }

    private static DetectedComponent Create(ComponentKind kind, HoleCoord lead1, HoleCoord lead2, int trackId = 1)
    {
        float minX = MathF.Min(lead1.Px, lead2.Px) - 6f;
        float minY = MathF.Min(lead1.Py, lead2.Py) - 6f;
        float width = MathF.Abs(lead2.Px - lead1.Px) + 12f;
        float height = MathF.Abs(lead2.Py - lead1.Py) + 12f;

        return new DetectedComponent(
            TrackId: trackId,
            Kind: kind,
            Confidence: 0.97f,
            BoxX: minX,
            BoxY: minY,
            BoxW: width,
            BoxH: height,
            Lead1Px: lead1.Px,
            Lead1Py: lead1.Py,
            Lead2Px: lead2.Px,
            Lead2Py: lead2.Py,
            DecodedValueLabel: null);
    }
}
