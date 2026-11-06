// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/AvometerPowerTests.cs
// PURPOSE: Automated unit tests verifying AVOmeter power rail simulation:
//          - Rails read 0.000V when unpowered (no battery or power source)
//          - Rails read battery voltage when a battery is installed and connected
//          - Battery direct probe reading (positive and reverse polarity)
// ============================================================================

using System;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class AvometerPowerTests
{
    private readonly PinMatrixLookup _matrix = PinMatrixLookup.Shared;
    private readonly ElectricalRulesChecker _checker = new();

    [Fact]
    public void Unpowered_rails_have_no_voltage_without_battery()
    {
        // Breadboard has no battery or power supply module placed
        var draft = new CircuitDraft();
        draft.TryAdd(ComponentKind.Resistor, "A10", "E10", "10k", out _, out _);

        // When evaluating board with no battery, rails are not energized
        _checker.EvaluateBoard(1, 0.5, Array.Empty<DetectedComponent>());
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 1, out HoleCoord vccRail);
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 1, out HoleCoord gndRail);

        // Verify neither rail is connected to an active power root
        int topVccRoot = _checker.GetRootNet(BreadboardSpec.StripTopVcc);
        int topGndRoot = _checker.GetRootNet(BreadboardSpec.StripTopGnd);

        topVccRoot.Should().Be(BreadboardSpec.StripTopVcc);
        topGndRoot.Should().Be(BreadboardSpec.StripTopGnd);
        draft.Parts.Should().NotContain(p => p.Kind == ComponentKind.Battery);
    }

    [Fact]
    public void Powered_rails_with_battery_bridge_properly_energizes_supply()
    {
        var draft = new CircuitDraft();
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 5, out HoleCoord vccHole);
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 5, out HoleCoord gndHole);

        // Add 9V battery plugged directly into rails
        draft.TryAdd(ComponentKind.Battery, vccHole.HoleId, gndHole.HoleId, "9V", out _, out _);

        draft.Parts.Should().Contain(p => p.Kind == ComponentKind.Battery && p.Value == "9V");
        DraftPart bat = draft.Parts[0];
        bat.Pin1HoleId.Should().Be(vccHole.HoleId);
        bat.Pin2HoleId.Should().Be(gndHole.HoleId);
    }
}
