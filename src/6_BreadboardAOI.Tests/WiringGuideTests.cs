// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/WiringGuideTests.cs
// PURPOSE: Automated unit tests for DevBoard-Sensor Wiring Guide & Interface Tutor:
//          - Logic level shifting rules (5V vs 3.3V)
//          - Board-specific I2C and GPIO pin assignments
//          - Step-by-step pin maps and jumper colors
//          - Markdown documentation generation
//          - CircuitDraft auto-detection
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class WiringGuideTests
{
    [Fact]
    public void GenerateGuide_ArduinoUno_HCSR04_maps_direct_5V_without_level_shift_alert()
    {
        var guide = WiringGuideService.GenerateGuide("HC-SR04", MicrocontrollerBoardType.ArduinoUnoR3R4);

        guide.Should().NotBeNull();
        guide.BoardName.Should().Contain("Arduino Uno");
        guide.IsLevelShiftingRequired.Should().BeFalse();
        guide.LevelShiftingAdvice.Should().BeNull();

        guide.Steps.Should().Contain(s => s.SensorPinName == "VCC" && s.TargetBoardPin.Contains("5V"));
        guide.Steps.Should().Contain(s => s.SensorPinName == "TRIG" && s.TargetBoardPin.Contains("D9"));
        guide.Steps.Should().Contain(s => s.SensorPinName == "ECHO" && s.TargetBoardPin.Contains("D10"));
        guide.Steps.Should().Contain(s => s.SensorPinName == "GND" && s.TargetBoardPin.Contains("GND"));
    }

    [Fact]
    public void GenerateGuide_ESP32_HCSR04_triggers_critical_level_shift_alert_for_echo_pin()
    {
        var guide = WiringGuideService.GenerateGuide("HC-SR04", MicrocontrollerBoardType.Esp32DevKitV1);

        guide.Should().NotBeNull();
        guide.BoardName.Should().Contain("ESP32");
        guide.IsLevelShiftingRequired.Should().BeTrue();
        guide.LevelShiftingAdvice.Should().Contain("voltage divider");

        var echoStep = guide.Steps.First(s => s.SensorPinName == "ECHO");
        echoStep.Warning.Should().NotBeNullOrEmpty();
        echoStep.Warning.Should().Contain("divider");
        echoStep.HasWarning.Should().BeTrue();
    }

    [Fact]
    public void GenerateGuide_I2C_OLED_assigns_authentic_hardware_I2C_pins()
    {
        var unoGuide = WiringGuideService.GenerateGuide("OLED_SSD1306", MicrocontrollerBoardType.ArduinoUnoR3R4);
        unoGuide.Steps.Should().Contain(s => s.SensorPinName == "SDA" && s.TargetBoardPin.Contains("A4"));
        unoGuide.Steps.Should().Contain(s => s.SensorPinName == "SCL" && s.TargetBoardPin.Contains("A5"));

        var espGuide = WiringGuideService.GenerateGuide("OLED_SSD1306", MicrocontrollerBoardType.Esp32DevKitV1);
        espGuide.Steps.Should().Contain(s => s.SensorPinName == "SDA" && s.TargetBoardPin.Contains("21"));
        espGuide.Steps.Should().Contain(s => s.SensorPinName == "SCL" && s.TargetBoardPin.Contains("22"));

        var megaGuide = WiringGuideService.GenerateGuide("OLED_SSD1306", MicrocontrollerBoardType.ArduinoMega2560);
        megaGuide.Steps.Should().Contain(s => s.SensorPinName == "SDA" && s.TargetBoardPin.Contains("20"));
        megaGuide.Steps.Should().Contain(s => s.SensorPinName == "SCL" && s.TargetBoardPin.Contains("21"));
    }

    [Fact]
    public void AutoDetectSensorFromParts_identifies_sensor_from_circuit_draft()
    {
        var parts = new List<DraftPart>
        {
            new(TrackId: 1, Kind: ComponentKind.Led, Pins: new[] { "A10", "A11" }, Value: "Red LED"),
            new(TrackId: 2, Kind: ComponentKind.Sensor, Pins: new[] { "B5", "B6", "B7", "B8" }, Value: "DHT22 Environmental Sensor")
        };

        var detected = WiringGuideService.AutoDetectSensorFromParts(parts);
        detected.Should().NotBeNull();
        detected!.SensorId.Should().Be("DHT22");
    }

    [Fact]
    public void FormattedMarkdown_contains_working_principle_wiring_table_and_verification_code()
    {
        var guide = WiringGuideService.GenerateGuide("DHT11", MicrocontrollerBoardType.ArduinoUnoR3R4);

        guide.FormattedMarkdown.Should().Contain("1. What It Does & How It Works");
        guide.FormattedMarkdown.Should().Contain("2. Step-by-Step Connection & Jumper Wire Map");
        guide.FormattedMarkdown.Should().Contain("5. Verification Test Firmware");
        guide.FormattedMarkdown.Should().Contain("void setup()");
        guide.FormattedMarkdown.Should().Contain("dht.readTemperature()");
    }

    [Fact]
    public void SensorPinoutCatalog_has_accurate_pin_specifications_for_sensors()
    {
        SensorPinoutCatalog.TryGetSensorOrBoard("HC-SR04", out var hcsr04).Should().BeTrue();
        hcsr04.Should().NotBeNull();
        hcsr04!.Pins.Select(p => p.Name).Should().ContainInOrder("VCC", "TRIG", "ECHO", "GND");

        SensorPinoutCatalog.TryGetSensorOrBoard("DHT11", out var dht11).Should().BeTrue();
        dht11.Should().NotBeNull();
        dht11!.Pins.Select(p => p.Name).Should().Contain(n => n.Contains("VCC"));
        dht11!.Pins.Select(p => p.Name).Should().Contain("DATA");
        dht11!.Pins.Select(p => p.Name).Should().Contain("GND");

        SensorPinoutCatalog.TryGetSensorOrBoard("MPU6050", out var mpu).Should().BeTrue();
        mpu.Should().NotBeNull();
        mpu!.Pins.Select(p => p.Name).Should().Contain("VCC", "GND", "SCL", "SDA");
    }

    [Fact]
    public void OverlayLayerSettings_defaults_pin_labels_enabled_and_supports_disabling()
    {
        var settings = new BreadboardAOI.App.Rendering.OverlayLayerSettings();
        settings.ShowSensorPinLabels.Should().BeTrue();

        settings.ShowSensorPinLabels = false;
        settings.ShowSensorPinLabels.Should().BeFalse();
    }

    [Fact]
    public void WiringGuideService_GenerateGuide_LCD1602_I2C_on_ArduinoUno_generates_direct_I2C_steps()
    {
        var guide = WiringGuideService.GenerateGuide("LCD1602_I2C", MicrocontrollerBoardType.ArduinoUnoR3R4);

        guide.Should().NotBeNull();
        guide.Sensor.SensorId.Should().Be("LCD1602_I2C");
        guide.IsLevelShiftingRequired.Should().BeFalse();
        guide.Steps.Should().HaveCount(4);

        guide.Steps[0].SensorPinName.Should().Be("GND");
        guide.Steps[0].TargetBoardPin.Should().Be("GND Rail");

        guide.Steps[1].SensorPinName.Should().Be("VCC");
        guide.Steps[1].TargetBoardPin.Should().Be("5V Power Rail");

        guide.Steps[2].SensorPinName.Should().Be("SDA");
        guide.Steps[2].TargetBoardPin.Should().Contain("A4");

        guide.Steps[3].SensorPinName.Should().Be("SCL");
        guide.Steps[3].TargetBoardPin.Should().Contain("A5");

        guide.FormattedMarkdown.Should().Contain("LiquidCrystal_I2C.h");
        guide.FormattedMarkdown.Should().Contain("0x27");
    }

    [Fact]
    public void WiringGuideService_GenerateGuide_LCD1602_I2C_on_ESP32_requires_3V3_level_shifter()
    {
        var guide = WiringGuideService.GenerateGuide("LCD1602_I2C", MicrocontrollerBoardType.Esp32DevKitV1);

        guide.Should().NotBeNull();
        guide.IsLevelShiftingRequired.Should().BeTrue();
        guide.LevelShiftingAdvice.Should().Contain("LEVEL SHIFTER");
        guide.Steps.Should().HaveCount(4);

        guide.Steps[2].SensorPinName.Should().Be("SDA");
        guide.Steps[2].TargetBoardPin.Should().Contain("GPIO 21");

        guide.Steps[3].SensorPinName.Should().Be("SCL");
        guide.Steps[3].TargetBoardPin.Should().Contain("GPIO 22");
    }

    [Fact]
    public void WiringGuideService_AutoDetectSensorFromParts_detects_LCD1602()
    {
        var parts = new[]
        {
            new DraftPart(1, ComponentKind.Sensor, new[] { "D1", "D2", "D3", "D4" }, "LCD1602 16x2 CHARACTER LCD I2C MODULE (4-PIN)")
        };

        var detected = WiringGuideService.AutoDetectSensorFromParts(parts);
        detected.Should().NotBeNull();
        detected!.SensorId.Should().Be("LCD1602_I2C");
        detected.DisplayName.Should().Contain("LCD1602");
    }

    [Fact]
    public void SensorPinoutCatalog_TryGetDefinition_resolves_LCD1602_full_catalog_string()
    {
        bool found = SensorPinoutCatalog.TryGetDefinition("LCD1602 16x2 CHARACTER LCD I2C MODULE (4-PIN)", 4, out var def);
        found.Should().BeTrue();
        def.Should().NotBeNull();
        def.PartId.Should().Be("LCD1602-I2C");
        def.DisplayName.Should().Contain("16x2");
        def.PrimaryGndPin.Should().Be(1);
        def.PrimaryVccPin.Should().Be(2);
        def.Pins.Should().HaveCount(4);
    }

    [Fact]
    public void SkiaArOverlayRenderer_GetPinFunctionLabel_resolves_LCD1602_pins()
    {
        BreadboardAOI.App.Rendering.SkiaArOverlayRenderer.GetPinFunctionLabel(ComponentKind.Sensor, "LCD1602", 0, 4).Should().Be("GND");
        BreadboardAOI.App.Rendering.SkiaArOverlayRenderer.GetPinFunctionLabel(ComponentKind.Sensor, "LCD1602", 1, 4).Should().Be("VCC");
        BreadboardAOI.App.Rendering.SkiaArOverlayRenderer.GetPinFunctionLabel(ComponentKind.Sensor, "LCD1602", 2, 4).Should().Be("SDA");
        BreadboardAOI.App.Rendering.SkiaArOverlayRenderer.GetPinFunctionLabel(ComponentKind.Sensor, "LCD1602", 3, 4).Should().Be("SCL");
    }
}
