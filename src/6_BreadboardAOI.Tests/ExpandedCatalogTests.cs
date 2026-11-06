using System;
using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Vision;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class ExpandedCatalogTests
{
    [Fact]
    public void ExpandedIcs_ShouldContainAllDefinedParts()
    {
        IReadOnlyDictionary<string, ExpandedIcDefinition> ics = IcPinoutCatalogExtensions.GetExpandedIcs();

        Assert.NotNull(ics);
        Assert.Equal(6, ics.Count);

        // 1. PIC16F877A: 40-pin DIP (VDD on pins 11 & 32, VSS on 12 & 31, MCLR on 1)
        Assert.True(ics.TryGetValue("PIC16F877A", out ExpandedIcDefinition? pic16));
        Assert.NotNull(pic16);
        Assert.Equal(40, pic16.PinCount);
        Assert.Equal(40, pic16.Pins.Count);
        Assert.Equal("MCLR", pic16.GetPin(1)?.Name);
        Assert.Equal(ExpandedIcPinRole.ResetActiveLow, pic16.GetPin(1)?.Role);
        Assert.Equal("VDD", pic16.GetPin(11)?.Name);
        Assert.Equal("VDD", pic16.GetPin(32)?.Name);
        Assert.Equal("VSS", pic16.GetPin(12)?.Name);
        Assert.Equal("VSS", pic16.GetPin(31)?.Name);
        Assert.Equal(2, pic16.PowerPins.Count);
        Assert.Equal(2, pic16.GroundPins.Count);

        // 2. MSP430G2553: 20-pin DIP (DVCC pin 1, DVSS pin 20, TEST pin 17, RST pin 16)
        Assert.True(ics.TryGetValue("MSP430G2553", out ExpandedIcDefinition? msp430));
        Assert.NotNull(msp430);
        Assert.Equal(20, msp430.PinCount);
        Assert.Equal(20, msp430.Pins.Count);
        Assert.False(msp430.Is5VTolerant);
        Assert.Equal("DVCC", msp430.GetPin(1)?.Name);
        Assert.Equal("RST", msp430.GetPin(16)?.Name);
        Assert.Equal("TEST", msp430.GetPin(17)?.Name);
        Assert.Equal("DVSS", msp430.GetPin(20)?.Name);

        // 3. ULN2003A: 16-pin DIP (Inputs 1..7, Outputs 16..10, GND 8, COM 9)
        Assert.True(ics.TryGetValue("ULN2003A", out ExpandedIcDefinition? uln2003));
        Assert.NotNull(uln2003);
        Assert.Equal(16, uln2003.PinCount);
        Assert.Equal(16, uln2003.Pins.Count);
        for (int ch = 1; ch <= 7; ch++)
        {
            Assert.Equal(ExpandedIcPinRole.DarlingtonBaseInput, uln2003.GetPin(ch)?.Role);
            Assert.Equal(ExpandedIcPinRole.DarlingtonCollectorOutput, uln2003.GetPin(17 - ch)?.Role);
        }
        Assert.Equal("GND", uln2003.GetPin(8)?.Name);
        Assert.Equal("COM", uln2003.GetPin(9)?.Name);

        // 4. MCP23017: 28-pin DIP (VDD 9, VSS 10, SCL 12, SDA 13, RESET 18)
        Assert.True(ics.TryGetValue("MCP23017", out ExpandedIcDefinition? mcp23017));
        Assert.NotNull(mcp23017);
        Assert.Equal(28, mcp23017.PinCount);
        Assert.Equal(28, mcp23017.Pins.Count);
        Assert.Equal("VDD", mcp23017.GetPin(9)?.Name);
        Assert.Equal("VSS", mcp23017.GetPin(10)?.Name);
        Assert.Equal("SCL", mcp23017.GetPin(12)?.Name);
        Assert.Equal("SDA", mcp23017.GetPin(13)?.Name);
        Assert.Equal("RESET", mcp23017.GetPin(18)?.Name);

        // 5. MAX7219: 24-pin DIP (DIN 1, CLK 13, LOAD/CS 12, V+ 19, GND 4 & 9)
        Assert.True(ics.TryGetValue("MAX7219", out ExpandedIcDefinition? max7219));
        Assert.NotNull(max7219);
        Assert.Equal(24, max7219.PinCount);
        Assert.Equal(24, max7219.Pins.Count);
        Assert.Equal("DIN", max7219.GetPin(1)?.Name);
        Assert.Equal("LOAD/CS", max7219.GetPin(12)?.Name);
        Assert.Equal("CLK", max7219.GetPin(13)?.Name);
        Assert.Equal("V+", max7219.GetPin(19)?.Name);
        Assert.Equal("GND", max7219.GetPin(4)?.Name);
        Assert.Equal("GND", max7219.GetPin(9)?.Name);
        Assert.Equal(2, max7219.GroundPins.Count);

        // 6. ADS1115: 10-pin breakout (VDD 1, GND 2, SCL 3, SDA 4, ADDR 5, ALRT 6, A0..A3)
        Assert.True(ics.TryGetValue("ADS1115", out ExpandedIcDefinition? ads1115));
        Assert.NotNull(ads1115);
        Assert.Equal(10, ads1115.PinCount);
        Assert.Equal(10, ads1115.Pins.Count);
        Assert.Equal("VDD", ads1115.GetPin(1)?.Name);
        Assert.Equal("GND", ads1115.GetPin(2)?.Name);
        Assert.Equal("SCL", ads1115.GetPin(3)?.Name);
        Assert.Equal("SDA", ads1115.GetPin(4)?.Name);
        Assert.Equal("ADDR", ads1115.GetPin(5)?.Name);
        Assert.Equal("ALRT", ads1115.GetPin(6)?.Name);
        Assert.Equal("A0", ads1115.GetPin(7)?.Name);
        Assert.Equal("A3", ads1115.GetPin(10)?.Name);
    }

    [Fact]
    public void ExpandedSensors_ShouldContainBme680AndMpu6050()
    {
        IReadOnlyDictionary<string, ExpandedSensorDefinition> sensors = SensorPinoutCatalogExtensions.GetExpandedSensors();

        Assert.NotNull(sensors);
        Assert.Equal(5, sensors.Count);

        string[] expectedSensorKeys = ["BME680", "MPU6050", "VL53L0X", "INA219", "RC522"];
        foreach (string key in expectedSensorKeys)
        {
            Assert.True(sensors.TryGetValue(key, out ExpandedSensorDefinition? sensor), $"Expected sensor '{key}' in catalog.");
            Assert.NotNull(sensor);
            Assert.Equal(sensor.PinCount, sensor.Pins.Count);
            Assert.NotEmpty(sensor.PowerPins);
            Assert.NotEmpty(sensor.GroundPins);
        }

        // BME680: 6 pins (VIN, GND, SCL, SDA, SDO, CS) - Strict 3.3V
        ExpandedSensorDefinition bme680 = sensors["BME680"];
        Assert.Equal(6, bme680.PinCount);
        Assert.False(bme680.Is5VCompatible);
        Assert.NotNull(bme680.FindPin("VIN"));
        Assert.NotNull(bme680.FindPin("GND"));
        Assert.NotNull(bme680.FindPin("SCL"));
        Assert.NotNull(bme680.FindPin("SDA"));
        Assert.NotNull(bme680.FindPin("SDO"));
        Assert.NotNull(bme680.FindPin("CS"));

        // MPU6050: 8 pins (VCC, GND, SCL, SDA, XDA, XCL, AD0, INT) - 3.3V/5V compatible
        ExpandedSensorDefinition mpu6050 = sensors["MPU6050"];
        Assert.Equal(8, mpu6050.PinCount);
        Assert.True(mpu6050.Is5VCompatible);
        Assert.NotNull(mpu6050.FindPin("VCC"));
        Assert.NotNull(mpu6050.FindPin("XDA"));
        Assert.NotNull(mpu6050.FindPin("XCL"));
        Assert.NotNull(mpu6050.FindPin("AD0"));
        Assert.NotNull(mpu6050.FindPin("INT"));

        // VL53L0X: 6 pins (VIN, GND, SCL, SDA, XSHUT, GPIO1) - 2.8V-5V compatible
        ExpandedSensorDefinition vl53 = sensors["VL53L0X"];
        Assert.Equal(6, vl53.PinCount);
        Assert.True(vl53.Is5VCompatible);
        Assert.NotNull(vl53.FindPin("XSHUT"));
        Assert.NotNull(vl53.FindPin("GPIO1"));

        // INA219: 6 pins (VCC, GND, SCL, SDA, VIN+, VIN-) - 3.3V/5V compatible
        ExpandedSensorDefinition ina219 = sensors["INA219"];
        Assert.Equal(6, ina219.PinCount);
        Assert.True(ina219.Is5VCompatible);
        Assert.NotNull(ina219.FindPin("VIN+"));
        Assert.NotNull(ina219.FindPin("VIN-"));

        // RC522: 8 pins (SDA, SCK, MOSI, MISO, IRQ, GND, RST, 3.3V) - Strict 3.3V
        ExpandedSensorDefinition rc522 = sensors["RC522"];
        Assert.Equal(8, rc522.PinCount);
        Assert.False(rc522.Is5VCompatible);
        Assert.NotNull(rc522.FindPin("SDA"));
        Assert.NotNull(rc522.FindPin("SCK"));
        Assert.NotNull(rc522.FindPin("MOSI"));
        Assert.NotNull(rc522.FindPin("MISO"));
        Assert.NotNull(rc522.FindPin("IRQ"));
        Assert.NotNull(rc522.FindPin("RST"));
        Assert.NotNull(rc522.FindPin("3.3V"));
    }

    [Theory]
    [InlineData("PIC16F877A", "PIC16F877A")]
    [InlineData("MSP430G2553", "MSP430")]
    [InlineData("MSP430G2553", "MSP430G2553IN20")]
    [InlineData("ULN2003A", "ULN2003")]
    [InlineData("ULN2003A", "ULN2003AN")]
    [InlineData("MCP23017", "MCP23017-E/SP")]
    [InlineData("MAX7219", "MAX7219CNG")]
    [InlineData("MAX7219", "MAX7221")]
    [InlineData("ADS1115", "ADS1115IDGSR")]
    [InlineData("ADS1115", "ADS1015")]
    [InlineData("ADS1115", "GY-ADS1115")]
    public void ExpandedIcs_ShouldResolveAliasesAccurately(string expectedPart, string lookupKey)
    {
        bool found = IcPinoutCatalogExtensions.TryGetExpandedIc(lookupKey, out ExpandedIcDefinition def);
        found.Should().BeTrue($"Expected alias '{lookupKey}' to resolve to '{expectedPart}'");
        def.PartNumber.Should().Be(expectedPart);
    }

    [Theory]
    [InlineData("BME680", "BME680")]
    [InlineData("BME680", "GY-BME680")]
    [InlineData("MPU6050", "MPU6050")]
    [InlineData("MPU6050", "MPU-6050")]
    [InlineData("MPU6050", "GY-521")]
    [InlineData("VL53L0X", "VL53L0X")]
    [InlineData("VL53L0X", "GY-VL53L0XV2")]
    [InlineData("VL53L0X", "TOF200C")]
    [InlineData("INA219", "INA219")]
    [InlineData("INA219", "GY-INA219")]
    [InlineData("INA219", "INA219B")]
    [InlineData("RC522", "RC522")]
    [InlineData("RC522", "MFRC522")]
    [InlineData("RC522", "RFID-RC522")]
    public void ExpandedSensors_ShouldResolveAliasesAccurately(string expectedModel, string lookupKey)
    {
        bool found = SensorPinoutCatalogExtensions.TryGetExpandedSensor(lookupKey, out ExpandedSensorDefinition def);
        found.Should().BeTrue($"Expected alias '{lookupKey}' to resolve to '{expectedModel}'");
        def.ModelId.Should().Be(expectedModel);
    }

    [Fact]
    public void ExpandedIcs_ConversionMethods_ShouldProduceValidDefinitions()
    {
        var registry = IcPinoutCatalogExtensions.GetExpandedIcs();
        foreach ((string part, ExpandedIcDefinition exp) in registry)
        {
            IcDefinition legacy = exp.ToIcDefinition();
            legacy.PartNumber.Should().Be(exp.PartNumber);
            legacy.PinCount.Should().Be(exp.PinCount);
            legacy.PrimaryVccPin.Should().BeGreaterThan(0);
            legacy.PrimaryGndPin.Should().BeGreaterThan(0);
            legacy.AllVccPins.Should().NotBeEmpty();
            legacy.AllGndPins.Should().NotBeEmpty();

            ComponentPinoutProfile profile = exp.ToComponentPinoutProfile();
            profile.PartNumber.Should().Be(exp.PartNumber);
            profile.PinCount.Should().Be(exp.PinCount);
            profile.Pins.Length.Should().Be(exp.PinCount);
        }
    }

    [Fact]
    public void ExpandedSensors_ConversionMethods_ShouldProduceValidDefinitions()
    {
        var registry = SensorPinoutCatalogExtensions.GetExpandedSensors();
        foreach ((string model, ExpandedSensorDefinition exp) in registry)
        {
            SensorDefinition legacy = exp.ToSensorDefinition();
            legacy.PartId.Should().Be(exp.ModelId);
            legacy.PinCount.Should().Be(exp.PinCount);
            legacy.PrimaryVccPin.Should().BeGreaterThan(0);
            legacy.PrimaryGndPin.Should().BeGreaterThan(0);

            ComponentPinoutProfile profile = exp.ToComponentPinoutProfile();
            profile.PartNumber.Should().Be(exp.ModelId);
            profile.PinCount.Should().Be(exp.PinCount);
            profile.Pins.Length.Should().Be(exp.PinCount);
        }
    }

    [Fact]
    public void IcPinoutCatalog_TryGetDefinition_ShouldResolveExpandedIcsAccurately()
    {
        // PIC16F877A: VDD pins 11, 32; VSS pins 12, 31
        IcPinoutCatalog.TryGetDefinition("PIC16F877A", 40, out IcDefinition pic).Should().BeTrue();
        pic.PrimaryVccPin.Should().Be(11);
        pic.PrimaryGndPin.Should().Be(12);
        pic.AllVccPins.Should().Contain([11, 32]);
        pic.AllGndPins.Should().Contain([12, 31]);

        // MSP430G2553: DVCC pin 1, DVSS pin 20
        IcPinoutCatalog.TryGetDefinition("MSP430G2553", 20, out IcDefinition msp).Should().BeTrue();
        msp.PrimaryVccPin.Should().Be(1);
        msp.PrimaryGndPin.Should().Be(20);

        // ULN2003A: COM pin 9, GND pin 8
        IcPinoutCatalog.TryGetDefinition("ULN2003A", 16, out IcDefinition uln).Should().BeTrue();
        uln.PrimaryVccPin.Should().Be(9);
        uln.PrimaryGndPin.Should().Be(8);

        // MCP23017: VDD pin 9, VSS pin 10
        IcPinoutCatalog.TryGetDefinition("MCP23017", 28, out IcDefinition mcp).Should().BeTrue();
        mcp.PrimaryVccPin.Should().Be(9);
        mcp.PrimaryGndPin.Should().Be(10);

        // MAX7219: V+ pin 19, GND pins 4 & 9
        IcPinoutCatalog.TryGetDefinition("MAX7219", 24, out IcDefinition max).Should().BeTrue();
        max.PrimaryVccPin.Should().Be(19);
        max.PrimaryGndPin.Should().Be(4);
        max.AllGndPins.Should().Contain([4, 9]);

        // ADS1115: VDD pin 1, GND pin 2
        IcPinoutCatalog.TryGetDefinition("ADS1115", 10, out IcDefinition ads).Should().BeTrue();
        ads.PrimaryVccPin.Should().Be(1);
        ads.PrimaryGndPin.Should().Be(2);
    }

    [Fact]
    public void SensorPinoutCatalog_TryGetDefinition_ShouldResolveExpandedSensorsAccurately()
    {
        // RC522: 3.3V is pin 8, GND is pin 6
        SensorPinoutCatalog.TryGetDefinition("RC522", 8, out SensorDefinition rc).Should().BeTrue();
        rc.PrimaryVccPin.Should().Be(8);
        rc.PrimaryGndPin.Should().Be(6);

        // BME680: VIN pin 1, GND pin 2
        SensorPinoutCatalog.TryGetDefinition("BME680", 6, out SensorDefinition bme).Should().BeTrue();
        bme.PrimaryVccPin.Should().Be(1);
        bme.PrimaryGndPin.Should().Be(2);

        // MPU6050: VCC pin 1, GND pin 2
        SensorPinoutCatalog.TryGetDefinition("MPU6050", 8, out SensorDefinition mpu).Should().BeTrue();
        mpu.PrimaryVccPin.Should().Be(1);
        mpu.PrimaryGndPin.Should().Be(2);

        // INA219: VCC pin 1, GND pin 2
        SensorPinoutCatalog.TryGetDefinition("INA219", 6, out SensorDefinition ina).Should().BeTrue();
        ina.PrimaryVccPin.Should().Be(1);
        ina.PrimaryGndPin.Should().Be(2);

        // VL53L0X: VIN pin 1, GND pin 2
        SensorPinoutCatalog.TryGetDefinition("VL53L0X", 6, out SensorDefinition vl).Should().BeTrue();
        vl.PrimaryVccPin.Should().Be(1);
        vl.PrimaryGndPin.Should().Be(2);
    }

    [Fact]
    public void ElectricalRulesChecker_DetectsReversedPolarity_OnExpandedIcsAndSensors()
    {
        var checker = new ElectricalRulesChecker();
        var matrix = PinMatrixLookup.Shared;

        // 1. PIC16F877A (40-pin DIP IC):
        // Pins 1..20 in row E columns 1..20
        // Pins 21..40 in row F columns 20..1
        // Pin 11 (VDD) is in row E11.
        // Pin 12 (VSS) is in row E12.
        var holes = new HoleCoord[40];
        for (int c = 1; c <= 20; c++)
        {
            matrix.TryGetHole($"E{c}", out holes[c - 1]);
        }
        for (int c = 20; c >= 1; c--)
        {
            int p = 21 + (20 - c);
            matrix.TryGetHole($"F{c}", out holes[p - 1]);
        }

        var extraLeads = new LeadPoint[38];
        for (int i = 2; i < 40; i++)
        {
            extraLeads[i - 2] = new LeadPoint(holes[i].Px, holes[i].Py);
        }

        DetectedComponent pic = new(
            TrackId: 1,
            Kind: ComponentKind.IntegratedCircuit,
            Confidence: 0.99f,
            BoxX: holes[0].Px, BoxY: holes[0].Py, BoxW: 100, BoxH: 40,
            Lead1Px: holes[0].Px, Lead1Py: holes[0].Py,
            Lead2Px: holes[1].Px, Lead2Py: holes[1].Py,
            DecodedValueLabel: "PIC16F877A",
            SymbolId: "MCU_Microchip_PIC16/PIC16F877A-I_P",
            ExtraLeads: extraLeads);

        // Pin 11 is VDD (holes[10], E11). Pin 12 is VSS (holes[11], E12).
        HoleCoord pin11 = holes[10];
        HoleCoord pin12 = holes[11];
        matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 11, out HoleCoord vccRail);
        matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 12, out HoleCoord gndRail);

        // Wire it in reverse: GND rail connected to pin 11 (VDD), and VCC rail connected to pin 12 (VSS)!
        DetectedComponent revVccWire = new(
            TrackId: 2,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: pin11.Px, BoxY: pin11.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: pin11.Px, Lead2Py: pin11.Py);

        DetectedComponent revGndWire = new(
            TrackId: 3,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: pin12.Px, BoxY: pin12.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: pin12.Px, Lead2Py: pin12.Py);

        DrcStatePacket packetPic = checker.EvaluateBoard(1, 0.5, [pic, revVccWire, revGndWire]);
        packetPic.Violations.Should().Contain(v => v.Code == ViolationCode.ErcReversedPolarity);

        // 2. RC522 (8-pin breakout): Pin 6 is GND, Pin 8 is 3.3V (VCC)
        // Place in row E columns 10..17
        var sensorHoles = new HoleCoord[8];
        for (int c = 10; c <= 17; c++)
        {
            matrix.TryGetHole($"E{c}", out sensorHoles[c - 10]);
        }

        var sensorExtra = new LeadPoint[6];
        for (int i = 2; i < 8; i++)
        {
            sensorExtra[i - 2] = new LeadPoint(sensorHoles[i].Px, sensorHoles[i].Py);
        }

        DetectedComponent rc522 = new(
            TrackId: 10,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: sensorHoles[0].Px, BoxY: sensorHoles[0].Py, BoxW: 80, BoxH: 20,
            Lead1Px: sensorHoles[0].Px, Lead1Py: sensorHoles[0].Py,
            Lead2Px: sensorHoles[1].Px, Lead2Py: sensorHoles[1].Py,
            DecodedValueLabel: "RC522",
            SymbolId: "Module/RC522",
            ExtraLeads: sensorExtra);

        // Pin 6 is GND (sensorHoles[5], E15). Pin 8 is 3.3V (sensorHoles[7], E17).
        HoleCoord gndPin = sensorHoles[5];
        HoleCoord vccPin = sensorHoles[7];
        matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 15, out HoleCoord vccRailSensor);
        matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 17, out HoleCoord gndRailSensor);

        // Reversed polarity on RC522: connect VCC rail to Pin 6 (GND) and GND rail to Pin 8 (3.3V)
        DetectedComponent revWire1 = new(
            TrackId: 11,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndPin.Px, BoxY: gndPin.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRailSensor.Px, Lead1Py: vccRailSensor.Py,
            Lead2Px: gndPin.Px, Lead2Py: gndPin.Py);

        DetectedComponent revWire2 = new(
            TrackId: 12,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccPin.Px, BoxY: vccPin.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRailSensor.Px, Lead1Py: gndRailSensor.Py,
            Lead2Px: vccPin.Px, Lead2Py: vccPin.Py);

        DrcStatePacket packetSensor = checker.EvaluateBoard(2, 0.5, [rc522, revWire1, revWire2]);
        packetSensor.Violations.Should().Contain(v => v.Code == ViolationCode.ErcReversedPolarity);
    }

    [Theory]
    [InlineData("PIC16F877A", "PIC16F877A")]
    [InlineData("PIC16F877A-I/P", "PIC16F877A")]
    [InlineData("MSP430G2553", "MSP430G2553")]
    [InlineData("MSP430G2553IN20", "MSP430G2553")]
    [InlineData("ULN2003A", "ULN2003")]
    [InlineData("MCP23017", "MCP23017")]
    [InlineData("MCP23017-E/SP", "MCP23017")]
    [InlineData("MAX7219", "MAX7219")]
    [InlineData("MAX7219CNG", "MAX7219")]
    [InlineData("ADS1115", "ADS1115")]
    [InlineData("ADS1115IDGSR", "ADS1115")]
    [InlineData("BME680", "BME680")]
    [InlineData("MPU6050", "MPU6050")]
    [InlineData("VL53L0X", "VL53L0X")]
    [InlineData("INA219", "INA219")]
    [InlineData("INA219B", "INA219")]
    [InlineData("RC522", "RC522")]
    public void PaddleIcPartNumberOcr_ShouldRecognizeAndNormalizeAllExpandedParts(string input, string expected)
    {
        string actual = PaddleIcPartNumberOcr.NormalizeAndMatchPartNumber(input);
        actual.Should().Be(expected);
    }

    [Fact]
    public void Ads1115_ShouldHaveSingleInLineGeometry_AndResolveInSensorCatalog()
    {
        bool found = IcPinoutCatalogExtensions.TryGetExpandedIc("ADS1115", out ExpandedIcDefinition adsIc);
        found.Should().BeTrue();
        adsIc.PackageType.Should().Be("BREAKOUT-10");

        ComponentPinoutProfile profile = adsIc.ToComponentPinoutProfile();
        profile.WidthCategory.Should().Be(DipWidthCategory.SingleInLine100Mil);
        profile.RowSpacingInches.Should().Be(0.1);
        profile.Pin1Marker.Should().Be(Pin1OrientationMarker.SilkscreenPin1SquarePad);

        bool sensorFound = SensorPinoutCatalog.TryGetDefinition("ADS1115", 10, out SensorDefinition adsSensor);
        sensorFound.Should().BeTrue();
        adsSensor.PinCount.Should().Be(10);
    }

    [Fact]
    public void ExpandedIcs_PhysicalWidthCategories_ShouldMatchPackageSpecifications()
    {
        // MCP23017 (Skinny DIP-28) -> Narrow 300 mil
        IcPinoutCatalogExtensions.TryGetExpandedIc("MCP23017", out ExpandedIcDefinition mcp).Should().BeTrue();
        ComponentPinoutProfile mcpProf = mcp.ToComponentPinoutProfile();
        mcpProf.WidthCategory.Should().Be(DipWidthCategory.Narrow300Mil);
        mcpProf.RowSpacingInches.Should().Be(0.3);

        // MAX7219 (DIP-24) -> Narrow 300 mil
        IcPinoutCatalogExtensions.TryGetExpandedIc("MAX7219", out ExpandedIcDefinition max).Should().BeTrue();
        ComponentPinoutProfile maxProf = max.ToComponentPinoutProfile();
        maxProf.WidthCategory.Should().Be(DipWidthCategory.Narrow300Mil);
        maxProf.RowSpacingInches.Should().Be(0.3);

        // MSP430G2553 (DIP-20) -> Narrow 300 mil
        IcPinoutCatalogExtensions.TryGetExpandedIc("MSP430G2553", out ExpandedIcDefinition msp).Should().BeTrue();
        ComponentPinoutProfile mspProf = msp.ToComponentPinoutProfile();
        mspProf.WidthCategory.Should().Be(DipWidthCategory.Narrow300Mil);
        mspProf.RowSpacingInches.Should().Be(0.3);

        // ULN2003A (DIP-16) -> Narrow 300 mil
        IcPinoutCatalogExtensions.TryGetExpandedIc("ULN2003A", out ExpandedIcDefinition uln).Should().BeTrue();
        ComponentPinoutProfile ulnProf = uln.ToComponentPinoutProfile();
        ulnProf.WidthCategory.Should().Be(DipWidthCategory.Narrow300Mil);
        ulnProf.RowSpacingInches.Should().Be(0.3);

        // PIC16F877A (DIP-40) -> Wide 600 mil
        IcPinoutCatalogExtensions.TryGetExpandedIc("PIC16F877A", out ExpandedIcDefinition pic).Should().BeTrue();
        ComponentPinoutProfile picProf = pic.ToComponentPinoutProfile();
        picProf.WidthCategory.Should().Be(DipWidthCategory.Wide600Mil);
        picProf.RowSpacingInches.Should().Be(0.6);
    }

    [Fact]
    public void Ina219_ShuntPins_ShouldHavePassiveLeadType()
    {
        SensorPinoutCatalogExtensions.TryGetExpandedSensor("INA219", out ExpandedSensorDefinition exp).Should().BeTrue();
        exp.GetPin(5)!.Role.Should().Be(ExpandedSensorPinRole.HighSideShuntPositive);
        exp.GetPin(6)!.Role.Should().Be(ExpandedSensorPinRole.HighSideShuntNegative);

        SensorPinoutCatalog.TryGetDefinition("INA219", 6, out SensorDefinition inaDef).Should().BeTrue();
        inaDef.Pins[4].Name.Should().Be("VIN+");
        inaDef.Pins[4].PinType.Should().Be(SensorPinType.PassiveLead);
        inaDef.Pins[5].Name.Should().Be("VIN-");
        inaDef.Pins[5].PinType.Should().Be(SensorPinType.PassiveLead);
    }

    [Fact]
    public void DevBoardRuleEngine_ShouldNotHijackMcuICs()
    {
        var h1 = new HoleCoord("E1", 101, 10f, 10f);
        var h2 = new HoleCoord("E2", 102, 20f, 10f);

        var picDetection = new DetectedComponent(
            TrackId: 1,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: 0, BoxY: 0, BoxW: 100, BoxH: 50,
            Lead1Px: 0, Lead1Py: 0,
            Lead2Px: 10, Lead2Py: 0,
            DecodedValueLabel: "PIC16F877A",
            SymbolId: "MCU_Microchip_PIC16/PIC16F877A-I_P");

        var snappedPic = new SnappedComponent(
            RawDetection: picDetection,
            Pin1Hole: h1,
            Pin2Hole: h2,
            Net1Name: "E1",
            Net2Name: "E2",
            ExtraPins: new SnappedPin[38]); // 40 pins total

        DevBoardAndModuleRuleEngine.IsDevBoard(snappedPic).Should().BeFalse();
        DevBoardAndModuleRuleEngine.TryGetBoardDefinition("PIC16F877A", 40, out _).Should().BeFalse();

        var mspDetection = new DetectedComponent(
            TrackId: 2,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: 0, BoxY: 0, BoxW: 100, BoxH: 50,
            Lead1Px: 0, Lead1Py: 0,
            Lead2Px: 10, Lead2Py: 0,
            DecodedValueLabel: "MSP430G2553",
            SymbolId: "MCU_TexasInstruments/MSP430G2553");

        var snappedMsp = new SnappedComponent(
            RawDetection: mspDetection,
            Pin1Hole: h1,
            Pin2Hole: h2,
            Net1Name: "E1",
            Net2Name: "E2",
            ExtraPins: new SnappedPin[18]); // 20 pins total

        DevBoardAndModuleRuleEngine.IsDevBoard(snappedMsp).Should().BeFalse();
        DevBoardAndModuleRuleEngine.TryGetBoardDefinition("MSP430G2553", 20, out _).Should().BeFalse();

        var picoDetection = new DetectedComponent(
            TrackId: 3,
            Kind: ComponentKind.Microcontroller,
            Confidence: 0.99f,
            BoxX: 0, BoxY: 0, BoxW: 100, BoxH: 50,
            Lead1Px: 0, Lead1Py: 0,
            Lead2Px: 10, Lead2Py: 0,
            DecodedValueLabel: "Pico",
            SymbolId: "Board/RaspberryPi_Pico");

        var snappedPico = new SnappedComponent(
            RawDetection: picoDetection,
            Pin1Hole: h1,
            Pin2Hole: h2,
            Net1Name: "E1",
            Net2Name: "E2",
            ExtraPins: new SnappedPin[38]);

        DevBoardAndModuleRuleEngine.IsDevBoard(snappedPico).Should().BeTrue();
        DevBoardAndModuleRuleEngine.TryGetBoardDefinition("Pico", 40, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(ComponentKind.IntegratedCircuit, "PIC16F877A", 0, 40, "MCLR")]
    [InlineData(ComponentKind.IntegratedCircuit, "PIC16F877A", 10, 40, "VDD")]
    [InlineData(ComponentKind.IntegratedCircuit, "PIC16F877A", 11, 40, "VSS")]
    [InlineData(ComponentKind.IntegratedCircuit, "PIC16F877A", 30, 40, "VSS")]
    [InlineData(ComponentKind.IntegratedCircuit, "PIC16F877A", 31, 40, "VDD")]
    [InlineData(ComponentKind.IntegratedCircuit, "MCP23017", 11, 28, "SCL")]
    [InlineData(ComponentKind.IntegratedCircuit, "MCP23017", 12, 28, "SDA")]
    [InlineData(ComponentKind.IntegratedCircuit, "MCP23017", 8, 28, "VDD")]
    [InlineData(ComponentKind.IntegratedCircuit, "MCP23017", 9, 28, "VSS")]
    [InlineData(ComponentKind.IntegratedCircuit, "MAX7219", 0, 24, "DIN")]
    [InlineData(ComponentKind.IntegratedCircuit, "MAX7219", 11, 24, "LOAD/CS")]
    [InlineData(ComponentKind.IntegratedCircuit, "MAX7219", 12, 24, "CLK")]
    [InlineData(ComponentKind.IntegratedCircuit, "MAX7219", 18, 24, "V+")]
    [InlineData(ComponentKind.Sensor, "RC522", 0, 8, "SDA")]
    [InlineData(ComponentKind.Sensor, "RC522", 1, 8, "SCK")]
    [InlineData(ComponentKind.Sensor, "RC522", 2, 8, "MOSI")]
    [InlineData(ComponentKind.Sensor, "RC522", 3, 8, "MISO")]
    [InlineData(ComponentKind.Sensor, "RC522", 4, 8, "IRQ")]
    [InlineData(ComponentKind.Sensor, "RC522", 5, 8, "GND")]
    [InlineData(ComponentKind.Sensor, "RC522", 6, 8, "RST")]
    [InlineData(ComponentKind.Sensor, "RC522", 7, 8, "3.3V")]
    [InlineData(ComponentKind.Sensor, "INA219", 0, 6, "VCC")]
    [InlineData(ComponentKind.Sensor, "INA219", 1, 6, "GND")]
    [InlineData(ComponentKind.Sensor, "INA219", 2, 6, "SCL")]
    [InlineData(ComponentKind.Sensor, "INA219", 3, 6, "SDA")]
    [InlineData(ComponentKind.Sensor, "INA219", 4, 6, "VIN+")]
    [InlineData(ComponentKind.Sensor, "INA219", 5, 6, "VIN-")]
    [InlineData(ComponentKind.Sensor, "BME680", 0, 6, "VIN")]
    [InlineData(ComponentKind.Sensor, "BME680", 1, 6, "GND")]
    [InlineData(ComponentKind.Sensor, "BME680", 2, 6, "SCL")]
    [InlineData(ComponentKind.Sensor, "BME680", 3, 6, "SDA")]
    [InlineData(ComponentKind.Sensor, "MPU6050", 0, 8, "VCC")]
    [InlineData(ComponentKind.Sensor, "MPU6050", 1, 8, "GND")]
    [InlineData(ComponentKind.Sensor, "VL53L0X", 4, 6, "XSHUT")]
    [InlineData(ComponentKind.Sensor, "VL53L0X", 5, 6, "GPIO1")]
    [InlineData(ComponentKind.IntegratedCircuit, "ADS1115", 0, 10, "VDD")]
    [InlineData(ComponentKind.IntegratedCircuit, "ADS1115", 1, 10, "GND")]
    [InlineData(ComponentKind.IntegratedCircuit, "ADS1115", 4, 10, "ADDR")]
    [InlineData(ComponentKind.IntegratedCircuit, "ADS1115", 5, 10, "ALRT")]
    public void SkiaArOverlayRenderer_GetPinFunctionLabel_ShouldReturnAccuratePinNames(
        ComponentKind kind, string label, int pinIndex, int pinCount, string expectedPinName)
    {
        string actual = SkiaArOverlayRenderer.GetPinFunctionLabel(kind, label, pinIndex, pinCount);
        actual.Should().Be(expectedPinName);
    }
}

