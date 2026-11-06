// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/DevBoardRuleEngineTests.cs
// PURPOSE: Automated unit tests verifying DevBoardAndModuleRuleEngine:
//          - 5V-to-3.3V GPIO Overvoltages on ESP32
//          - Output attempted on Input-Only pins (ESP32 GPIO 34, 35, 36, 39)
//          - UART Crossover Errors (TX-TX, RX-RX)
//          - I2C Crossover Errors (SDA-to-SCL swapped)
//          - Trench straddling violation on Dev Boards
// ============================================================================

using System;
using System.Linq;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class DevBoardRuleEngineTests
{
    private readonly ElectricalRulesChecker _checker = new();
    private readonly PinMatrixLookup _matrix = PinMatrixLookup.Shared;

    [Fact]
    public void ESP32_GPIO_connected_to_5V_rail_triggers_ErcVoltageOverload()
    {
        // Place ESP32 30-pin:
        // Left side pins 1..15 on E10..E24
        // Right side pins 16..30 on F24..F10
        // Pin 26 is GPIO21 (SDA) on F14 (Strip 214)
        _matrix.TryGetHole("E10", out HoleCoord p1);
        _matrix.TryGetHole("E11", out HoleCoord p2);
        _matrix.TryGetHole("F14", out HoleCoord p26_gpio21);

        var extra = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = i < 13 ? $"E{12 + i}" : $"F{24 - (i - 13)}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extra[i] = new LeadPoint(h.Px, h.Py);
        }

        var esp32 = new DetectedComponent(
            TrackId: 1,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: p1.Px, BoxY: p1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: p1.Px, Lead1Py: p1.Py,
            Lead2Px: p2.Px, Lead2Py: p2.Py,
            DecodedValueLabel: "ESP32",
            SymbolId: "MCU_Espressif/ESP32-WROOM-32",
            ExtraLeads: extra);

        // Fault: User runs a jumper wire directly from +5V rail to ESP32 Pin 26 (GPIO21)!
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 14, out HoleCoord vccRail);
        _matrix.TryGetHole("A14", out HoleCoord a14);
        var wireTo5VRail = new DetectedComponent(
            TrackId: 2,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: a14.Px, Lead2Py: a14.Py);

        // Bridge A14 (5V rail) to F14 (ESP32 GPIO21)
        var wireToGpio = new DetectedComponent(
            TrackId: 3,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: a14.Px, BoxY: a14.Py, BoxW: 10, BoxH: 10,
            Lead1Px: a14.Px, Lead1Py: a14.Py,
            Lead2Px: p26_gpio21.Px, Lead2Py: p26_gpio21.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [esp32, wireTo5VRail, wireToGpio]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcVoltageOverload &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("5V Overvoltage Overload"));
    }

    [Fact]
    public void ESP32_input_only_pin_driving_LED_triggers_ErcInputOnlyViolation()
    {
        // ESP32 Pin 4 is GPIO34 (Input-only GPI, NO output driver!)
        // Place pin 4 at E13 (Strip 113)
        _matrix.TryGetHole("E10", out HoleCoord p1);
        _matrix.TryGetHole("E11", out HoleCoord p2);
        _matrix.TryGetHole("E13", out HoleCoord p4_gpio34);

        var extra = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = i < 13 ? $"E{12 + i}" : $"F{24 - (i - 13)}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extra[i] = new LeadPoint(h.Px, h.Py);
        }

        var esp32 = new DetectedComponent(
            TrackId: 10,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: p1.Px, BoxY: p1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: p1.Px, Lead1Py: p1.Py,
            Lead2Px: p2.Px, Lead2Py: p2.Py,
            DecodedValueLabel: "ESP32",
            SymbolId: "MCU_Espressif/ESP32-WROOM-32",
            ExtraLeads: extra);

        // Fault: User connects an LED anode to E13 (GPIO34) and cathode to GND!
        _matrix.TryGetHole("A13", out HoleCoord a13);
        _matrix.TryGetHole("A17", out HoleCoord a17);
        var led = new DetectedComponent(
            TrackId: 11,
            Kind: ComponentKind.Led,
            Confidence: 0.99f,
            BoxX: a13.Px, BoxY: a13.Py, BoxW: 20, BoxH: 10,
            Lead1Px: a13.Px, Lead1Py: a13.Py,
            Lead2Px: a17.Px, Lead2Py: a17.Py,
            DecodedValueLabel: "RED");

        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 17, out HoleCoord gndRail);
        var gndWire = new DetectedComponent(
            TrackId: 12,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: a17.Px, Lead2Py: a17.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [esp32, led, gndWire]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcInputOnlyViolation &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("input-only (GPI)"));
    }

    [Fact]
    public void UART_TX_to_TX_connection_triggers_ErcProtocolCrossoverError()
    {
        // Two MCUs: ESP32 and Arduino Nano
        // ESP32 TX2 is Pin 22 at F18
        // Arduino Nano TX is Pin 1 at A30
        _matrix.TryGetHole("E10", out HoleCoord e10);
        _matrix.TryGetHole("E11", out HoleCoord e11);
        _matrix.TryGetHole("F18", out HoleCoord esp_tx2);

        var extraEsp = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = i < 13 ? $"E{12 + i}" : $"F{24 - (i - 13)}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extraEsp[i] = new LeadPoint(h.Px, h.Py);
        }

        var esp32 = new DetectedComponent(
            TrackId: 20,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: e10.Px, BoxY: e10.Py, BoxW: 60, BoxH: 60,
            Lead1Px: e10.Px, Lead1Py: e10.Py,
            Lead2Px: e11.Px, Lead2Py: e11.Py,
            DecodedValueLabel: "ESP32",
            SymbolId: "MCU_Espressif/ESP32-WROOM-32",
            ExtraLeads: extraEsp);

        _matrix.TryGetHole("A30", out HoleCoord nano_tx);
        _matrix.TryGetHole("A31", out HoleCoord nano_rx);
        var extraNano = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = $"A{32 + i}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extraNano[i] = new LeadPoint(h.Px, h.Py);
        }

        var nano = new DetectedComponent(
            TrackId: 21,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: nano_tx.Px, BoxY: nano_tx.Py, BoxW: 60, BoxH: 60,
            Lead1Px: nano_tx.Px, Lead1Py: nano_tx.Py,
            Lead2Px: nano_rx.Px, Lead2Py: nano_rx.Py,
            DecodedValueLabel: "ARDUINO_NANO",
            SymbolId: "MCU_Module/Arduino_Nano_v3.x",
            ExtraLeads: extraNano);

        // Fault: User connects ESP32 TX2 directly to Arduino TX! (TX to TX error)
        var badUartWire = new DetectedComponent(
            TrackId: 22,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: esp_tx2.Px, BoxY: esp_tx2.Py, BoxW: 10, BoxH: 10,
            Lead1Px: esp_tx2.Px, Lead1Py: esp_tx2.Py,
            Lead2Px: nano_tx.Px, Lead2Py: nano_tx.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [esp32, nano, badUartWire]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcProtocolCrossoverError &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("UART Protocol Crossover Error"));
    }

    [Fact]
    public void I2C_SDA_to_SCL_swapped_connection_triggers_ErcProtocolCrossoverError()
    {
        // ESP32 SDA is Pin 26 (F14)
        // Arduino Nano SCL is Pin 24 (A53)
        _matrix.TryGetHole("E10", out HoleCoord e10);
        _matrix.TryGetHole("E11", out HoleCoord e11);
        _matrix.TryGetHole("F14", out HoleCoord esp_sda);

        var extraEsp = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = i < 13 ? $"E{12 + i}" : $"F{24 - (i - 13)}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extraEsp[i] = new LeadPoint(h.Px, h.Py);
        }

        var esp32 = new DetectedComponent(
            TrackId: 30,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: e10.Px, BoxY: e10.Py, BoxW: 60, BoxH: 60,
            Lead1Px: e10.Px, Lead1Py: e10.Py,
            Lead2Px: e11.Px, Lead2Py: e11.Py,
            DecodedValueLabel: "ESP32",
            SymbolId: "MCU_Espressif/ESP32-WROOM-32",
            ExtraLeads: extraEsp);

        _matrix.TryGetHole("A30", out HoleCoord nano_p1);
        _matrix.TryGetHole("A31", out HoleCoord nano_p2);
        _matrix.TryGetHole("A53", out HoleCoord nano_scl); // Pin 24 = SCL
        var extraNano = new LeadPoint[28];
        for (int i = 0; i < 28; i++)
        {
            string hId = $"A{32 + i}";
            _matrix.TryGetHole(hId, out HoleCoord h);
            extraNano[i] = new LeadPoint(h.Px, h.Py);
        }

        var nano = new DetectedComponent(
            TrackId: 31,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: nano_p1.Px, BoxY: nano_p1.Py, BoxW: 60, BoxH: 60,
            Lead1Px: nano_p1.Px, Lead1Py: nano_p1.Py,
            Lead2Px: nano_p2.Px, Lead2Py: nano_p2.Py,
            DecodedValueLabel: "ARDUINO_NANO",
            SymbolId: "MCU_Module/Arduino_Nano_v3.x",
            ExtraLeads: extraNano);

        // Fault: User connects ESP32 SDA directly to Arduino SCL!
        var badI2cWire = new DetectedComponent(
            TrackId: 32,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: esp_sda.Px, BoxY: esp_sda.Py, BoxW: 10, BoxH: 10,
            Lead1Px: esp_sda.Px, Lead1Py: esp_sda.Py,
            Lead2Px: nano_scl.Px, Lead2Py: nano_scl.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [esp32, nano, badI2cWire]);

        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcProtocolCrossoverError &&
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("I2C Bus Mismatch"));
    }

    [Theory]
    [InlineData(MicrocontrollerBoardType.ArduinoUnoR3R4)]
    [InlineData(MicrocontrollerBoardType.ArduinoMega2560)]
    [InlineData(MicrocontrollerBoardType.Esp32DevKitV1)]
    [InlineData(MicrocontrollerBoardType.WemosEsp32S2Mini)]
    [InlineData(MicrocontrollerBoardType.Stm32BluePillF103C8)]
    public void Microcontroller_Top_And_Bottom_Pins_Have_Outer_Facing_Labels(MicrocontrollerBoardType boardType)
    {
        var engine = new DevBoardAndModuleRuleEngine();
        MicrocontrollerBoardProfile profile = engine.GetBoardProfile(boardType);

        foreach (McuPinDefinition pin in profile.Pins)
        {
            // Top row pins must point AboveSocket (extending UP, outside PCB)
            if (pin.LocalYMm <= 5.1f && pin.LabelAlignment != PinLabelAlignment.LeftOfSocket && pin.LabelAlignment != PinLabelAlignment.RightOfSocket)
            {
                pin.LabelAlignment.Should().Be(
                    PinLabelAlignment.AboveSocket,
                    $"Top pin {pin.DisplayLabel} on {profile.DisplayName} at Y={pin.LocalYMm} must point outward/above so it doesn't obscure components");
            }

            // Bottom row pins must point BelowSocket (extending DOWN, outside PCB)
            if (pin.LocalYMm >= 20.0f && pin.LabelAlignment != PinLabelAlignment.LeftOfSocket && pin.LabelAlignment != PinLabelAlignment.RightOfSocket)
            {
                pin.LabelAlignment.Should().Be(
                    PinLabelAlignment.BelowSocket,
                    $"Bottom pin {pin.DisplayLabel} on {profile.DisplayName} at Y={pin.LocalYMm} must point outward/below so it doesn't obscure components");
            }
        }
    }

    [Fact]
    public void OffBoard_JumperWire_Power_Short_Is_Detected_By_McuRuleEngine()
    {
        var engine = new DevBoardAndModuleRuleEngine();

        var net = new OffBoardElectricalNet(
            NetName: "FAULTY_PWR_NET",
            ConnectedMcuPins: new[]
            {
                new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, "5V"),
                new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, "GND_1")
            },
            BreadboardComponents: Array.Empty<BreadboardNetComponent>(),
            BreadboardTiePoints: new[] { "TOP_VCC_1", "TOP_GND_1" });

        var violations = engine.EvaluateCircuit(new[] { net });
        violations.Should().Contain(v =>
            v.RuleCode == "DRC-PWR-GND-SHORT" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk);
    }

    [Fact]
    public void OffBoard_JumperWire_Cross_Overvoltage_Into_3V3_Is_Detected()
    {
        var engine = new DevBoardAndModuleRuleEngine();

        // 5V from Arduino Uno connected directly to ESP32 non-tolerant pin (e.g. GPIO21 / D21)
        var net = new OffBoardElectricalNet(
            NetName: "OVERVOLTAGE_NET",
            ConnectedMcuPins: new[]
            {
                new McuPinEndpoint("UNO", MicrocontrollerBoardType.ArduinoUnoR3R4, "5V"),
                new McuPinEndpoint("ESP", MicrocontrollerBoardType.Esp32DevKitV1, "D21")
            },
            BreadboardComponents: Array.Empty<BreadboardNetComponent>(),
            BreadboardTiePoints: new[] { "E15" });

        var violations = engine.EvaluateCircuit(new[] { net });
        violations.Should().Contain(v =>
            v.RuleCode == "DRC-OVERVOLTAGE-5V-TO-3V3" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk);
    }

    [Fact]
    public void OffBoard_Led_Without_Series_Resistor_Triggers_Violation()
    {
        var engine = new DevBoardAndModuleRuleEngine();

        var net = new OffBoardElectricalNet(
            NetName: "UNPROTECTED_LED",
            ConnectedMcuPins: new[]
            {
                new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D13")
            },
            BreadboardComponents: new[]
            {
                new BreadboardNetComponent("LED1", ExternalLoadType.StandardLed, CurrentDrawMa: 20.0, ResistanceOhms: 0.0, ConnectedBreadboardTiePoint: "E15")
            },
            BreadboardTiePoints: new[] { "E15" });

        var violations = engine.EvaluateCircuit(new[] { net });
        violations.Should().Contain(v =>
            v.RuleCode == "DRC-LED-NO-RESISTOR");
    }
}
