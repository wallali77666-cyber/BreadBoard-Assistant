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

        // The 100 us gate is a steady-state figure. Take the fastest of several
        // runs so scheduler jitter or a GC from a parallel test cannot fail it.
        DrcStatePacket packet = null!;
        double bestMs = double.MaxValue;
        for (int run = 0; run < 7; run++)
        {
            packet = checker.EvaluateBoard(42, 5.8, new[] { led });
            bestMs = Math.Min(bestMs, packet.DrcComputeMs);
        }

        var violation = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcSameStripShort).Subject;

        violation.Severity.Should().Be(DrcSeverity.Critical);
        violation.OffendingStripId.Should().Be(115);
        violation.BadHole.HoleId.Should().Be("D15");
        violation.SuggestedTargetHole.HoleId.Should().Be("D16");
        violation.SuggestedTargetHole.StripId.Should().Be(116);

        bestMs.Should().BeLessThan(0.1,
            "the best of seven steady-state runs must fit the 100 microsecond reflex budget");
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
    public void Shipped_target_netlist_produces_ratsnest_air_wires()    {
        TargetSchematic? target = TargetSchematicLoader.TryLoadDefault(out string? error);
        target.Should().NotBeNull(error);

        var checker = new ElectricalRulesChecker(Lookup);
        checker.EvaluateBoard(30, 1.0, Array.Empty<DetectedComponent>(), target);

        var ratsnest = TargetSchematicLoader.ComputeRatsnest(target!, Lookup, checker);
        ratsnest.Should().NotBeEmpty();
        ratsnest.Should().OnlyContain(v => v.From.HoleId != v.To.HoleId);
    }

    [Fact]
    public void Vcc_gnd_bridge_names_the_offending_jumper_and_its_holes()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 10, out var vcc).Should().BeTrue();
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 12, out var gnd).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(9, 0.5, new[] { Create(ComponentKind.JumperWire, vcc, gnd, trackId: 7) });

        var shorted = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcVccGndDirectShort).Subject;

        shorted.ComponentId.Should().Be("JumperWire_7");
        shorted.BadHole.HoleId.Should().Be(vcc.HoleId);
        shorted.TechnicalMessage.Should().Contain(gnd.HoleId);
    }

    [Fact]
    public void Jumper_inside_one_metal_clip_is_reported_as_redundant()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 10, out var vcc10).Should().BeTrue();
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 20, out var vcc20).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(10, 0.4, new[] { Create(ComponentKind.JumperWire, vcc10, vcc20) });

        var redundant = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcRedundantJumper).Subject;

        redundant.Severity.Should().Be(DrcSeverity.Warning);
        redundant.OffendingStripId.Should().Be(BreadboardSpec.StripTopVcc);
        redundant.TechnicalMessage.Should().Contain("does nothing");
    }

    [Fact]
    public void Four_leads_in_one_hole_is_reported_as_mechanical_congestion()
    {
        Lookup.TryGetHole("A20", out var shared).Should().BeTrue();
        Lookup.TryGetHole("B30", out var b30).Should().BeTrue();
        Lookup.TryGetHole("C31", out var c31).Should().BeTrue();
        Lookup.TryGetHole("D32", out var d32).Should().BeTrue();
        Lookup.TryGetHole("E33", out var e33).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(11, 0.4, new[]
        {
            Create(ComponentKind.JumperWire, shared, b30, trackId: 1),
            Create(ComponentKind.JumperWire, shared, c31, trackId: 2),
            Create(ComponentKind.JumperWire, shared, d32, trackId: 3),
            Create(ComponentKind.JumperWire, shared, e33, trackId: 4)
        });

        var congestion = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.ErcHoleCollision).Subject;

        congestion.BadHole.HoleId.Should().Be("A20");
        congestion.SuggestedTargetHole.HoleId.Should().NotBeNullOrEmpty();
        congestion.SuggestedTargetHole.HoleId.Should().NotBe("A20");
        congestion.TechnicalMessage.Should().Contain("4 leads");
    }

    [Fact]
    public void Two_distinct_target_nets_bridged_together_emit_unexpected_link()
    {
        var target = TargetSchematicLoader.Parse("""
            { "name": "pair", "nets": [
                { "name": "NET_A", "holes": [ "A10", "B10" ] },
                { "name": "NET_B", "holes": [ "A20", "B20" ] } ] }
            """);

        Lookup.TryGetHole("A10", out var a10).Should().BeTrue();
        Lookup.TryGetHole("B10", out var b10).Should().BeTrue();
        Lookup.TryGetHole("A20", out var a20).Should().BeTrue();
        Lookup.TryGetHole("B20", out var b20).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(12, 0.4, new[]
        {
            Create(ComponentKind.JumperWire, a10, b10, trackId: 1),
            Create(ComponentKind.JumperWire, a20, b20, trackId: 2),
            Create(ComponentKind.JumperWire, a10, a20, trackId: 3)
        }, target);

        var bridge = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.DrcUnexpectedNetLink).Subject;

        bridge.Severity.Should().Be(DrcSeverity.Warning);
        bridge.ComponentId.Should().Contain("NET_A").And.Contain("NET_B");
        bridge.TechnicalMessage.Should()
            .Contain("Remove the jumper")
            .And.Contain("must stay separate");
    }

    [Fact]
    public void Open_target_net_message_names_both_holes_and_the_fix()
    {
        var target = TargetSchematicLoader.Parse("""
            { "name": "open", "nets": [ { "name": "NET_OUT", "holes": [ "A10", "F10" ] } ] }
            """);

        Lookup.TryGetHole("A10", out var a10).Should().BeTrue();
        Lookup.TryGetHole("F10", out var f10).Should().BeTrue();
        Lookup.TryGetHole("C20", out var c20).Should().BeTrue();
        Lookup.TryGetHole("J40", out var j40).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(13, 0.4, new[]
        {
            Create(ComponentKind.Resistor, a10, c20, trackId: 1),
            Create(ComponentKind.Resistor, f10, j40, trackId: 2)
        }, target);

        var open = packet.Violations.Should()
            .ContainSingle(v => v.Code == ViolationCode.DrcSchematicNetMismatch).Subject;

        open.TechnicalMessage.Should()
            .Contain("Add a jumper from A10 to F10")
            .And.Contain("is open");
    }

    [Fact]
    public void Netlist_report_groups_terminal_holes_by_resolved_net()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 12, out var vcc).Should().BeTrue();
        Lookup.TryGetHole("A12", out var a12).Should().BeTrue();
        Lookup.TryGetHole("B40", out var b40).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(14, 0.4, new[]
        {
            Create(ComponentKind.JumperWire, vcc, a12),
            Create(ComponentKind.JumperWire, b40, a12, trackId: 2)
        });

        var nets = NetlistReport.Build(packet);
        nets.Should().NotBeEmpty();
        nets[0].Name.Should().Be("NET_VCC");
        nets[0].Holes.Should().Contain(new[] { vcc.HoleId, a12.HoleId, b40.HoleId });

        NetlistReport.Format(packet).Should().Contain("NET_VCC");
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
