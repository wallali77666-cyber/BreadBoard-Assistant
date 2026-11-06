// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/EnhancedDrcTests.cs
// PURPOSE: Automated unit tests for Enhanced Electrical Rules (DRC):
//          - Resistors: LED current limiting (<100Ω burnout, >100kΩ unlit)
//          - Sensors: 3.3V vs 5V supply mismatches, reverse polarity, I2C pullups
//          - Dev Boards: ESP32 input-only pins, boot strapping pin conflicts
//          - Dev Board Removal: MicrocontrollerBoardType.None
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class EnhancedDrcTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;
    private readonly ElectricalRulesChecker _checker = new(Lookup);
    private readonly DevBoardAndModuleRuleEngine _devBoardEngine = new();

    [Fact]
    public void Led_without_current_limiting_resistor_across_rails_triggers_critical_violation()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 1, out HoleCoord vcc);
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 1, out HoleCoord gnd);

        var led = Create(ComponentKind.Led, vcc, gnd, trackId: 1);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, new[] { led });

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitLow &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("without a current-limiting resistor"));
    }

    [Fact]
    public void Led_with_resistor_under_100_ohms_triggers_critical_burnout_violation()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 5, out HoleCoord vcc);
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 5, out HoleCoord gnd);
        Lookup.TryGetHole("A15", out HoleCoord a15);
        Lookup.TryGetHole("C15", out HoleCoord c15);

        // Resistor 47Ω from VCC to strip 115 (A15)
        var resistor = Create(ComponentKind.Resistor, vcc, a15, trackId: 1, valueLabel: "47R");
        // LED from strip 115 (C15) to GND
        var led = Create(ComponentKind.Led, c15, gnd, trackId: 2);

        DrcStatePacket packet = _checker.EvaluateBoard(2, 0.5, new[] { resistor, led });

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitLow &&
            v.TechnicalMessage.Contains("< 100Ω"));
    }

    [Fact]
    public void Led_with_resistor_over_100k_ohms_triggers_warning_violation()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 5, out HoleCoord vcc);
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 5, out HoleCoord gnd);
        Lookup.TryGetHole("A15", out HoleCoord a15);
        Lookup.TryGetHole("C15", out HoleCoord c15);

        // Resistor 220kΩ from VCC to strip 115 (A15)
        var resistor = Create(ComponentKind.Resistor, vcc, a15, trackId: 1, valueLabel: "220k");
        // LED from strip 115 (C15) to GND
        var led = Create(ComponentKind.Led, c15, gnd, trackId: 2);

        DrcStatePacket packet = _checker.EvaluateBoard(3, 0.5, new[] { resistor, led });

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitHigh &&
            v.Severity == DrcSeverity.Warning &&
            v.TechnicalMessage.Contains("> 100kΩ"));
    }

    [Fact]
    public void Led_with_correct_330_ohm_resistor_passes_without_current_limit_violations()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 5, out HoleCoord vcc);
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 5, out HoleCoord gnd);
        Lookup.TryGetHole("A15", out HoleCoord a15);
        Lookup.TryGetHole("C15", out HoleCoord c15);

        // Resistor 330Ω from VCC to strip 115 (A15)
        var resistor = Create(ComponentKind.Resistor, vcc, a15, trackId: 1, valueLabel: "330R");
        // LED from strip 115 (C15) to GND
        var led = Create(ComponentKind.Led, c15, gnd, trackId: 2);

        DrcStatePacket packet = _checker.EvaluateBoard(4, 0.5, new[] { resistor, led });

        packet.Violations.Should().NotContain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitLow ||
            v.Code == ViolationCode.ErcResistorCurrentLimitHigh);
    }

    [Fact]
    public void DevBoard_none_profile_returns_valid_empty_definition()
    {
        MicrocontrollerBoardProfile noneProfile = _devBoardEngine.GetBoardProfile(MicrocontrollerBoardType.None);

        noneProfile.Should().NotBeNull();
        noneProfile.BoardType.Should().Be(MicrocontrollerBoardType.None);
        noneProfile.Pins.Should().BeEmpty();
        noneProfile.DisplayName.Should().Contain("None");
    }

    private static DetectedComponent Create(
        ComponentKind kind,
        HoleCoord lead1,
        HoleCoord lead2,
        int trackId = 1,
        string? valueLabel = null)
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
            DecodedValueLabel: valueLabel);
    }
}
