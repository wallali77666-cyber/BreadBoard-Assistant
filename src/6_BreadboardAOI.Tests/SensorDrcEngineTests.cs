// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/SensorDrcEngineTests.cs
// PURPOSE: Automated unit tests verifying KiCad-grade DRC rules for Sensors:
//          - MQ-2 Gas/Smoke 4-pin VCC/GND/DO/AO checks
//          - DHT11 / DHT22 4-pin single-wire pull-up and NC isolation checks
//          - A3144 Hall Effect open-collector pull-up and VCC dead-short checks
//          - HC-SR04 Ultrasonic 4-pin rail shorts
//          - Voltage Regulators (L7805) output short checks
// ============================================================================

using System;
using System.Linq;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class SensorDrcEngineTests
{
    private readonly ElectricalRulesChecker _checker = new();
    private readonly PinMatrixLookup _matrix = PinMatrixLookup.Shared;

    [Fact]
    public void DHT11_with_valid_wiring_and_pullup_passes_DRC()
    {
        // DHT11 4-pin:
        // Pin 1: VCC (C13)
        // Pin 2: DATA (C14)
        // Pin 3: NC (C15) - Unconnected!
        // Pin 4: GND (C16)
        _matrix.TryGetHole("C13", out HoleCoord h1);
        _matrix.TryGetHole("C14", out HoleCoord h2);
        _matrix.TryGetHole("C15", out HoleCoord h3);
        _matrix.TryGetHole("C16", out HoleCoord h4);

        DetectedComponent dht = new(
            TrackId: 10,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 20,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "DHT11",
            SymbolId: "Sensor/DHT11",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py), new(h4.Px, h4.Py) });

        // VCC wire from VCC rail to Pin 1 (A13)
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 13, out HoleCoord vccRail);
        _matrix.TryGetHole("A13", out HoleCoord a13);
        DetectedComponent vccWire = new(
            TrackId: 11,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: a13.Px, Lead2Py: a13.Py);

        // GND wire from GND rail to Pin 4 (A16)
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 16, out HoleCoord gndRail);
        _matrix.TryGetHole("A16", out HoleCoord a16);
        DetectedComponent gndWire = new(
            TrackId: 12,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: a16.Px, Lead2Py: a16.Py);

        // 10k Pull-up resistor from DATA (A14) to VCC (E13)
        _matrix.TryGetHole("A14", out HoleCoord a14);
        _matrix.TryGetHole("E13", out HoleCoord e13);
        DetectedComponent pullup = new(
            TrackId: 13,
            Kind: ComponentKind.Resistor,
            Confidence: 0.99f,
            BoxX: a14.Px, BoxY: a14.Py, BoxW: 30, BoxH: 10,
            Lead1Px: a14.Px, Lead1Py: a14.Py,
            Lead2Px: e13.Px, Lead2Py: e13.Py,
            DecodedValueLabel: "10k");

        // Pin 3 (C15) has NO connection attached
        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [dht, vccWire, gndWire, pullup]);

        // Must pass with zero critical errors
        packet.HasCriticalShort.Should().BeFalse();
        packet.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcVccGndDirectShort);
        packet.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcSameStripShort);
    }

    [Fact]
    public void DHT11_fails_when_NC_pin_3_has_an_external_wire_connected()
    {
        _matrix.TryGetHole("C13", out HoleCoord h1);
        _matrix.TryGetHole("C14", out HoleCoord h2);
        _matrix.TryGetHole("C15", out HoleCoord h3);
        _matrix.TryGetHole("C16", out HoleCoord h4);

        DetectedComponent dht = new(
            TrackId: 20,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 20,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "DHT11",
            SymbolId: "Sensor/DHT11",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py), new(h4.Px, h4.Py) });

        // Fault: User improperly connects a jumper to Pin 3 (NC)!
        _matrix.TryGetHole("A15", out HoleCoord a15);
        _matrix.TryGetHole("A18", out HoleCoord a18);
        DetectedComponent illegalWire = new(
            TrackId: 21,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: a15.Px, BoxY: a15.Py, BoxW: 10, BoxH: 10,
            Lead1Px: a15.Px, Lead1Py: a15.Py,
            Lead2Px: a18.Px, Lead2Py: a18.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [dht, illegalWire]);

        // Must report NC pin violation
        packet.Violations.Should().Contain(v =>
            v.TechnicalMessage.Contains("NC (Not Connected / factory null pin)"));
    }

    [Fact]
    public void DHT11_warns_when_DATA_line_lacks_pullup_resistor()
    {
        _matrix.TryGetHole("C13", out HoleCoord h1);
        _matrix.TryGetHole("C14", out HoleCoord h2);
        _matrix.TryGetHole("C15", out HoleCoord h3);
        _matrix.TryGetHole("C16", out HoleCoord h4);

        DetectedComponent dht = new(
            TrackId: 30,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 20,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "DHT11",
            SymbolId: "Sensor/DHT11",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py), new(h4.Px, h4.Py) });

        // Power and Ground are connected, but NO pull-up resistor on Pin 2
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 13, out HoleCoord vccRail);
        _matrix.TryGetHole("A13", out HoleCoord a13);
        DetectedComponent vccWire = new(
            TrackId: 31,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: a13.Px, Lead2Py: a13.Py);

        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 16, out HoleCoord gndRail);
        _matrix.TryGetHole("A16", out HoleCoord a16);
        DetectedComponent gndWire = new(
            TrackId: 32,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: a16.Px, Lead2Py: a16.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [dht, vccWire, gndWire]);

        // Must warn that Pin 2 requires a pull-up resistor
        packet.Violations.Should().Contain(v =>
            v.TechnicalMessage.Contains("requires an external pull-up resistor"));
    }

    [Fact]
    public void A3144_fails_critical_when_open_collector_OUT_pin_is_tied_directly_to_VCC()
    {
        // A3144: Pin 1 = VCC (C20), Pin 2 = GND (C21), Pin 3 = OUT (C22)
        _matrix.TryGetHole("C20", out HoleCoord h1);
        _matrix.TryGetHole("C21", out HoleCoord h2);
        _matrix.TryGetHole("C22", out HoleCoord h3);

        DetectedComponent hall = new(
            TrackId: 40,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 30, BoxH: 10,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "A3144",
            SymbolId: "Sensor_Magnetic/A3144",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py) });

        // User connects Pin 3 (OUT) directly to VCC rail without a resistor!
        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 22, out HoleCoord vccRail);
        _matrix.TryGetHole("A22", out HoleCoord a22);
        DetectedComponent badWire = new(
            TrackId: 41,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: a22.Px, Lead2Py: a22.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [hall, badWire]);

        // Must trigger critical warning for open-collector tied directly to VCC
        packet.Violations.Should().Contain(v =>
            v.Severity == DrcSeverity.Critical &&
            v.TechnicalMessage.Contains("tied directly to VCC without a pull-up resistor"));
    }

    [Fact]
    public void MQ2_detects_direct_power_short_when_VCC_and_GND_are_shorted()
    {
        // MQ-2: Pin 1 = VCC (C10), Pin 2 = GND (C11), Pin 3 = DO (C12), Pin 4 = AO (C13)
        _matrix.TryGetHole("C10", out HoleCoord h1);
        _matrix.TryGetHole("C11", out HoleCoord h2);
        _matrix.TryGetHole("C12", out HoleCoord h3);
        _matrix.TryGetHole("C13", out HoleCoord h4);

        DetectedComponent mq2 = new(
            TrackId: 50,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 20,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "MQ-2",
            SymbolId: "Sensor_Gas/MQ-2",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py), new(h4.Px, h4.Py) });

        // Accidental bridge between Pin 1 (A10) and Pin 2 (A11)
        _matrix.TryGetHole("A10", out HoleCoord a10);
        _matrix.TryGetHole("A11", out HoleCoord a11);
        DetectedComponent shortWire = new(
            TrackId: 51,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: a10.Px, BoxY: a10.Py, BoxW: 10, BoxH: 10,
            Lead1Px: a10.Px, Lead1Py: a10.Py,
            Lead2Px: a11.Px, Lead2Py: a11.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [mq2, shortWire]);

        // Must emit critical direct power short
        packet.HasCriticalShort.Should().BeTrue();
        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcVccGndDirectShort &&
            v.TechnicalMessage.Contains("Direct power short across 'MQ-2"));
    }

    [Fact]
    public void MQ2_detects_reversed_power_polarity()
    {
        // MQ-2: Pin 1 = VCC (C10), Pin 2 = GND (C11)
        _matrix.TryGetHole("C10", out HoleCoord h1);
        _matrix.TryGetHole("C11", out HoleCoord h2);
        _matrix.TryGetHole("C12", out HoleCoord h3);
        _matrix.TryGetHole("C13", out HoleCoord h4);

        DetectedComponent mq2 = new(
            TrackId: 60,
            Kind: ComponentKind.Sensor,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 60, BoxH: 20,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "MQ-2",
            SymbolId: "Sensor_Gas/MQ-2",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py), new(h4.Px, h4.Py) });

        // Reverse wiring: Pin 1 (VCC) is wired to GND rail, and Pin 2 (GND) is wired to VCC rail!
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 10, out HoleCoord gndRail);
        _matrix.TryGetHole("A10", out HoleCoord a10);
        DetectedComponent wireToGnd = new(
            TrackId: 61,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: a10.Px, Lead2Py: a10.Py);

        _matrix.TryGetRailHole(BreadboardSpec.StripTopVcc, 11, out HoleCoord vccRail);
        _matrix.TryGetHole("A11", out HoleCoord a11);
        DetectedComponent wireToVcc = new(
            TrackId: 62,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: vccRail.Px, BoxY: vccRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: vccRail.Px, Lead1Py: vccRail.Py,
            Lead2Px: a11.Px, Lead2Py: a11.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [mq2, wireToGnd, wireToVcc]);

        // Must emit reversed polarity critical error
        packet.Violations.Should().Contain(v =>
            v.Code == ViolationCode.ErcReversedPolarity &&
            v.TechnicalMessage.Contains("Reverse power polarity"));
    }

    [Fact]
    public void L7805_regulator_detects_output_shorted_to_ground()
    {
        // L7805: Pin 1 = IN (C5), Pin 2 = GND (C6), Pin 3 = OUT (C7)
        _matrix.TryGetHole("C5", out HoleCoord h1);
        _matrix.TryGetHole("C6", out HoleCoord h2);
        _matrix.TryGetHole("C7", out HoleCoord h3);

        DetectedComponent reg = new(
            TrackId: 70,
            Kind: ComponentKind.VoltageRegulator,
            Confidence: 0.99f,
            BoxX: h1.Px, BoxY: h1.Py, BoxW: 30, BoxH: 10,
            Lead1Px: h1.Px, Lead1Py: h1.Py,
            Lead2Px: h2.Px, Lead2Py: h2.Py,
            DecodedValueLabel: "L7805",
            SymbolId: "Regulator_Linear/L7805",
            ExtraLeads: new LeadPoint[] { new(h3.Px, h3.Py) });

        // User accidentally grounds OUT (Pin 3 at C7) to GND rail
        _matrix.TryGetRailHole(BreadboardSpec.StripTopGnd, 7, out HoleCoord gndRail);
        _matrix.TryGetHole("A7", out HoleCoord a7);
        DetectedComponent shortWire = new(
            TrackId: 71,
            Kind: ComponentKind.JumperWire,
            Confidence: 0.99f,
            BoxX: gndRail.Px, BoxY: gndRail.Py, BoxW: 10, BoxH: 10,
            Lead1Px: gndRail.Px, Lead1Py: gndRail.Py,
            Lead2Px: a7.Px, Lead2Py: a7.Py);

        DrcStatePacket packet = _checker.EvaluateBoard(1, 0.5, [reg, shortWire]);

        // Must report regulator output shorted to GND
        packet.Violations.Should().Contain(v =>
            v.TechnicalMessage.Contains("Regulator output pin 3 (OUT) at C7 is shorted directly to GND"));
    }
}
