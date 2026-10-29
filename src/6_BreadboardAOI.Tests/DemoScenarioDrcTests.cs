using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

/// <summary>
/// End-to-end verdicts for the four shipped demo circuits. These are the boards
/// the tutorial shows on launch, so a regression here is immediately visible.
/// </summary>
public sealed class DemoScenarioDrcTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;

    [Theory]
    [InlineData(DemoScenario.SameStripLedShort, ViolationCode.ErcSameStripShort)]
    [InlineData(DemoScenario.VccGndShort, ViolationCode.ErcVccGndDirectShort)]
    [InlineData(DemoScenario.FloatingLead, ViolationCode.ErcFloatingLead)]
    public void Demo_scenario_emits_its_documented_fault(DemoScenario scenario, ViolationCode expected)
    {
        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(1, 0, DemoScenarioCatalog.GetDetections(scenario));

        packet.Violations.Should().Contain(v => v.Code == expected);
    }

    [Fact]
    public void Valid_555_demo_passes_every_rule_against_the_shipped_netlist()
    {
        TargetSchematic? target = TargetSchematicLoader.TryLoadDefault(out string? error);
        target.Should().NotBeNull(error);

        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(
            2,
            0,
            DemoScenarioCatalog.GetDetections(DemoScenario.Valid555Blinker),
            target);

        packet.Violations.Should().BeEmpty(
            "the reference 555 build is the tutorial's clean example: {0}",
            string.Join(" | ", System.Linq.Enumerable.Select(packet.Violations, v => v.Code + " " + v.TechnicalMessage)));
    }

    [Fact]
    public void Valid_555_demo_has_no_floating_leads_or_redundant_jumpers()
    {
        var checker = new ElectricalRulesChecker(Lookup);
        var packet = checker.EvaluateBoard(
            3,
            0,
            DemoScenarioCatalog.GetDetections(DemoScenario.Valid555Blinker));

        System.Linq.Enumerable.Where(
                packet.Violations,
                v => v.Code is ViolationCode.ErcFloatingLead
                    or ViolationCode.ErcRedundantJumper
                    or ViolationCode.ErcHoleCollision)
            .Should()
            .BeEmpty();
    }
}
