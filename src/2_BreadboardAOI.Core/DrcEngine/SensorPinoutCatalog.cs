// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/SensorPinoutCatalog.cs
// PURPOSE: Pinout specifications and electrical characteristics for modular
//          and discrete sensors and microcontroller development boards.
// ============================================================================

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace BreadboardAOI.Core.DrcEngine;

public enum SensorPinType : byte
{
    PowerVcc = 0,       // Must connect to positive supply net (e.g. NET_VCC)
    Ground = 1,         // Must connect to 0V ground net (e.g. NET_GND)
    DigitalOutput = 2,  // Active push-pull or totem-pole output (cannot tie to VCC/GND rails)
    AnalogOutput = 3,   // Low-impedance analog voltage output (cannot tie to VCC/GND rails)
    DigitalInput = 4,   // Logic input (e.g. TRIG)
    OpenCollector = 5,  // NPN open-collector output; strictly requires pull-up resistor to VCC
    OneWireBus = 6,     // Single-wire bidirectional open-drain bus; strictly requires pull-up to VCC
    NotConnected = 7,   // NC pin; MUST NOT have any external connection or jumper attached
    RegulatorIn = 8,    // Voltage regulator raw DC input
    RegulatorOut = 9,   // Regulated DC voltage output
    RegulatorAdj = 10,  // Adjustable regulator reference divider pin
    PassiveLead = 11    // Passive two-terminal sensor lead (LDR, NTC, Tilt)
}

public sealed record SensorPinSpec(
    int PinNumber,             // 1-based index (1..PinCount)
    string Name,               // e.g. "VCC", "GND", "DATA", "NC", "TRIG", "ECHO", "OUT"
    SensorPinType PinType,
    string Description
);

public sealed record SensorDefinition(
    string PartId,
    string DisplayName,
    int PinCount,
    IReadOnlyList<SensorPinSpec> Pins,
    double NominalVoltage,
    double MinVoltage,
    double MaxVoltage,
    string Description
)
{
    public SensorPinSpec? GetPin(int oneBasedPin)
    {
        if (oneBasedPin < 1 || oneBasedPin > Pins.Count)
            return null;
        return Pins[oneBasedPin - 1];
    }

    public int PrimaryVccPin => FindFirstPinOf(SensorPinType.PowerVcc, SensorPinType.RegulatorIn);
    public int PrimaryGndPin => FindFirstPinOf(SensorPinType.Ground);

    private int FindFirstPinOf(params SensorPinType[] types)
    {
        for (int i = 0; i < Pins.Count; i++)
        {
            for (int t = 0; t < types.Length; t++)
            {
                if (Pins[i].PinType == types[t])
                    return Pins[i].PinNumber;
            }
        }
        return 0;
    }
}

/// <summary>
/// Production catalog of 21 popular breadboard Sensor Breakouts, Display/Motor Modules, and Microcontroller Development Boards.
/// Together with <see cref="IcPinoutCatalog"/> (25 ICs), this provides 46 complete component definitions.
/// </summary>
public static class SensorPinoutCatalog
{
    private static readonly VoltageRange Logic3V3Only = new(3.0, 3.3, 3.6, 3.6, IsFiveVoltTolerant: false);
    private static readonly VoltageRange Logic3V3FiveVoltTolerant = new(3.0, 3.3, 3.6, 5.5, IsFiveVoltTolerant: true);
    private static readonly VoltageRange Logic5VStandard = new(4.5, 5.0, 5.5, 6.0, IsFiveVoltTolerant: true);
    private static readonly VoltageRange Wide3V3To5V = new(3.0, 5.0, 5.5, 6.0, IsFiveVoltTolerant: true);

    public static FrozenDictionary<string, ComponentPinoutProfile> All { get; } = CreateCatalog();
    private static readonly Dictionary<string, SensorDefinition> LegacySensors = CreateLegacySensors();

    public static bool TryGetSensorOrBoard(string partNumberOrAlias, out ComponentPinoutProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(partNumberOrAlias))
        {
            profile = null;
            return false;
        }

        string trimmed = partNumberOrAlias.Trim();
        if (All.TryGetValue(trimmed, out profile))
            return true;

        if (trimmed.Contains("LCD", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("1602", StringComparison.OrdinalIgnoreCase))
        {
            if (All.TryGetValue("LCD1602-I2C", out profile))
                return true;
        }

        if (SensorPinoutCatalogExtensions.TryGetExpandedSensor(trimmed, out ExpandedSensorDefinition? expSensor))
        {
            profile = expSensor.ToComponentPinoutProfile();
            return true;
        }

        profile = null;
        return false;
    }

    /// <summary>
    /// Looks up sensor definition by label, symbol id, or fallback.
    /// Provides 100% backward compatibility for ElectricalRulesChecker.
    /// </summary>
    public static bool TryGetDefinition(string? partNameOrSymbol, int pinCount, out SensorDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(partNameOrSymbol))
        {
            string clean = partNameOrSymbol.Trim();
            int slash = clean.LastIndexOf('/');
            if (slash >= 0)
                clean = clean[(slash + 1)..];

            int space = clean.IndexOf(' ');
            if (space > 0)
                clean = clean[..space];

            if (All.TryGetValue(clean, out ComponentPinoutProfile? profile) &&
                !profile.Category.StartsWith("Microcontroller", StringComparison.OrdinalIgnoreCase))
            {
                definition = ProfileToSensorDefinition(profile);
                return true;
            }

            if (SensorPinoutCatalogExtensions.TryGetExpandedSensor(clean, out ExpandedSensorDefinition? expSensor))
            {
                definition = expSensor.ToSensorDefinition();
                return true;
            }

            if (LegacySensors.TryGetValue(clean, out definition!))
                return true;

            // Direct check for LCD1602 display modules
            if (partNameOrSymbol.Contains("LCD", StringComparison.OrdinalIgnoreCase) ||
                partNameOrSymbol.Contains("1602", StringComparison.OrdinalIgnoreCase) ||
                partNameOrSymbol.Contains("HD44780", StringComparison.OrdinalIgnoreCase))
            {
                if (All.TryGetValue("LCD1602-I2C", out var lcdProfile))
                {
                    definition = ProfileToSensorDefinition(lcdProfile);
                    return true;
                }
            }

            foreach (var pair in All)
            {
                if (!pair.Value.Category.StartsWith("Microcontroller", StringComparison.OrdinalIgnoreCase) &&
                    clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = ProfileToSensorDefinition(pair.Value);
                    return true;
                }
            }

            foreach (var pair in LegacySensors)
            {
                if (clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = pair.Value;
                    return true;
                }
            }

            foreach (var pair in SensorPinoutCatalogExtensions.GetExpandedSensors())
            {
                if (clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = pair.Value.ToSensorDefinition();
                    return true;
                }
            }

            if (IcPinoutCatalog.TryGetIc(clean, out ComponentPinoutProfile? icProfile) && icProfile != null)
            {
                definition = ProfileToSensorDefinition(icProfile);
                return true;
            }
        }

        // Generic fallback by pin count if unknown sensor
        switch (pinCount)
        {
            case 3:
                definition = new SensorDefinition(
                    "SENSOR-3P",
                    "Generic 3-Pin Sensor / Regulator",
                    3,
                    [
                        new(1, "VCC", SensorPinType.PowerVcc, "Power Supply"),
                        new(2, "OUT", SensorPinType.DigitalOutput, "Signal Output"),
                        new(3, "GND", SensorPinType.Ground, "Ground")
                    ],
                    5.0, 3.0, 12.0, "Generic 3-pin sensor footprint");
                return true;

            case 4:
                definition = new SensorDefinition(
                    "SENSOR-4P",
                    "Generic 4-Pin Sensor Module",
                    4,
                    [
                        new(1, "VCC", SensorPinType.PowerVcc, "Power Supply"),
                        new(2, "GND", SensorPinType.Ground, "Ground"),
                        new(3, "DO", SensorPinType.DigitalOutput, "Digital Output"),
                        new(4, "AO", SensorPinType.AnalogOutput, "Analog Output")
                    ],
                    5.0, 3.3, 5.5, "Generic 4-pin sensor module");
                return true;

            default:
                definition = null!;
                return false;
        }
    }

    private static SensorDefinition ProfileToSensorDefinition(ComponentPinoutProfile p)
    {
        var pins = new List<SensorPinSpec>();
        for (int i = 0; i < p.Pins.Length; i++)
        {
            var pin = p.Pins[i];
            SensorPinType pinType = SensorPinType.DigitalInput;
            if (pin.Functions.HasFlag(PinFunction.RegulatedVoltageOut))
                pinType = SensorPinType.RegulatorOut;
            else if (pin.Functions.HasFlag(PinFunction.Ground))
                pinType = SensorPinType.Ground;
            else if (pin.Functions.HasFlag(PinFunction.PowerVcc) || pin.Functions.HasFlag(PinFunction.PowerVdd))
                pinType = SensorPinType.PowerVcc;
            else if (pin.Functions.HasFlag(PinFunction.NoConnect))
                pinType = SensorPinType.NotConnected;
            else if (pin.Functions.HasFlag(PinFunction.OneWireData))
                pinType = SensorPinType.OneWireBus;
            else if (pin.Functions.HasFlag(PinFunction.OpenCollectorOut))
                pinType = SensorPinType.OpenCollector;
            else if (pin.Functions.HasFlag(PinFunction.AnalogOutput))
                pinType = SensorPinType.AnalogOutput;
            else if (pin.Functions.HasFlag(PinFunction.DigitalOutput))
                pinType = SensorPinType.DigitalOutput;

            pins.Add(new SensorPinSpec(pin.PinNumber, pin.Name, pinType, pin.ElectricalNotes));
        }

        return new SensorDefinition(
            PartId: p.PartNumber,
            DisplayName: p.Description,
            PinCount: p.PinCount,
            Pins: pins,
            NominalVoltage: p.SupplyVoltage.NominalVolts,
            MinVoltage: p.SupplyVoltage.MinVolts,
            MaxVoltage: p.SupplyVoltage.MaxVolts,
            Description: p.Description);
    }

    private static Dictionary<string, SensorDefinition> CreateLegacySensors()
    {
        var dict = new Dictionary<string, SensorDefinition>(StringComparer.OrdinalIgnoreCase);

        void Add(SensorDefinition def, params string[] aliases)
        {
            dict[def.PartId] = def;
            foreach (var alias in aliases) dict[alias] = def;
        }

        Add(new SensorDefinition("L7805", "L7805 +5V 1.5A Positive Voltage Regulator (TO-220)", 3,
            [
                new(1, "IN", SensorPinType.RegulatorIn, "Unregulated DC Input Voltage (+7V to +25V DC)"),
                new(2, "GND", SensorPinType.Ground, "Common 0V Ground Reference"),
                new(3, "OUT", SensorPinType.RegulatorOut, "Regulated +5.0V DC Output")
            ], 5.0, 7.0, 35.0, "Three-terminal 5V positive linear voltage regulator (TO-220)"), "7805", "LM7805", "MC7805");

        Add(new SensorDefinition("LM317", "LM317 Adjustable Positive Voltage Regulator (TO-220)", 3,
            [
                new(1, "ADJ", SensorPinType.PassiveLead, "Output Voltage Adjust Terminal"),
                new(2, "OUT", SensorPinType.RegulatorOut, "Adjustable Regulated Output (+1.25V to +37V)"),
                new(3, "IN", SensorPinType.RegulatorIn, "Unregulated DC Input Voltage (+3V to +40V)")
            ], 5.0, 3.0, 40.0, "1.5A adjustable positive linear regulator (TO-220)"), "LM317T");

        Add(new SensorDefinition("MQ-2", "MQ-2 Flammable Gas & Smoke Sensor Module", 4,
            [
                new(1, "VCC", SensorPinType.PowerVcc, "+5V DC Power Supply"),
                new(2, "GND", SensorPinType.Ground, "System 0V Ground Return"),
                new(3, "DO", SensorPinType.DigitalOutput, "Digital Output"),
                new(4, "AO", SensorPinType.AnalogOutput, "Analog Output")
            ], 5.0, 4.8, 5.2, "MQ-2 gas/smoke sensor"), "MQ2", "GAS", "SMOKE");

        Add(new SensorDefinition("A3144", "A3144 Hall Effect Sensitive Switch (TO-92)", 3,
            [
                new(1, "VCC", SensorPinType.PowerVcc, "Power Supply (4.5V to 24V DC)"),
                new(2, "GND", SensorPinType.Ground, "System 0V Ground Return"),
                new(3, "OUT", SensorPinType.OpenCollector, "Open-Collector NPN Output")
            ], 5.0, 4.5, 24.0, "A3144 Hall switch"), "HALL", "OH49E");

        Add(new SensorDefinition("TMP36", "TMP36 Linear Analog Temperature Sensor (TO-92)", 3,
            [
                new(1, "+Vs", SensorPinType.PowerVcc, "Power Supply (2.7V to 5.5V DC)"),
                new(2, "VOUT", SensorPinType.AnalogOutput, "Analog Output Voltage"),
                new(3, "GND", SensorPinType.Ground, "System 0V Ground Return")
            ], 3.3, 2.7, 5.5, "TMP36 temp sensor"), "TMP", "LM35");

        Add(new SensorDefinition("KY-038", "KY-038 Microphone Sound Sensor Module", 4,
            [
                new(1, "AO", SensorPinType.AnalogOutput, "Microphone Analog Output"),
                new(2, "GND", SensorPinType.Ground, "System 0V Ground Return"),
                new(3, "VCC", SensorPinType.PowerVcc, "Power Supply (3.3V to 5.5V DC)"),
                new(4, "DO", SensorPinType.DigitalOutput, "Digital Threshold Output")
            ], 5.0, 3.3, 5.5, "KY-038 sound sensor"), "KY038", "SOUND");

        Add(new SensorDefinition("LD1117V33", "LD1117V33 +3.3V 800mA LDO Regulator (TO-220)", 3,
            [
                new(1, "GND", SensorPinType.Ground, "Common 0V Ground Reference"),
                new(2, "OUT", SensorPinType.RegulatorOut, "Regulated +3.3V DC Output"),
                new(3, "IN", SensorPinType.RegulatorIn, "Unregulated Input DC Voltage (+4.75V to +15V DC)")
            ], 3.3, 4.75, 15.0, "1117 LDO regulator"), "1117", "LM1117", "AMS1117");

        Add(new SensorDefinition("LDR GL5528", "LDR GL5528 Photoresistor", 2,
            [
                new(1, "1", SensorPinType.PassiveLead, "Lead 1"),
                new(2, "2", SensorPinType.PassiveLead, "Lead 2")
            ], 5.0, 0.0, 150.0, "Two-terminal photoresistor"), "LDR", "GL5528");

        Add(new SensorDefinition("NTC 10K", "NTC 10K Thermistor", 2,
            [
                new(1, "1", SensorPinType.PassiveLead, "Lead 1"),
                new(2, "2", SensorPinType.PassiveLead, "Lead 2")
            ], 5.0, 0.0, 30.0, "Two-terminal NTC thermistor"), "NTC", "THERMISTOR");

        Add(new SensorDefinition("SW-520D", "SW-520D Ball Tilt & Vibration Sensor Switch", 2,
            [
                new(1, "1", SensorPinType.PassiveLead, "Switch Contact 1"),
                new(2, "2", SensorPinType.PassiveLead, "Switch Contact 2")
            ], 5.0, 0.0, 12.0, "Two-terminal tilt switch"), "SW520D", "TILT");

        Add(new SensorDefinition("BPW34", "BPW34 PIN Photodiode", 2,
            [
                new(1, "ANODE", SensorPinType.PassiveLead, "Anode"),
                new(2, "CATHODE", SensorPinType.PassiveLead, "Cathode")
            ], 5.0, 0.0, 32.0, "Two-terminal PIN photodiode"));

        Add(new SensorDefinition("MIC", "Electret Condenser Microphone Capsule (2-Pin)", 2,
            [
                new(1, "V+", SensorPinType.PassiveLead, "Positive terminal"),
                new(2, "GND", SensorPinType.Ground, "Negative terminal")
            ], 3.3, 1.5, 10.0, "Two-terminal electret mic"));

        return dict;
    }

    private static FrozenDictionary<string, ComponentPinoutProfile> CreateCatalog()
    {
        var profiles = new ComponentPinoutProfile[]
        {
            // Sensors & Modules (15)
            BuildHCSR04(),
            BuildDHT11(),
            BuildDHT22(),
            BuildBMP280(),
            BuildBME280(),
            BuildMPU6050(),
            BuildDS3231(),
            BuildRC522(),
            BuildSSD1306I2C4Pin(),
            BuildLCD1602I2C(),
            BuildL298NModule(),
            BuildTB6612FNG(),
            BuildADS1115(),
            BuildDS18B20(),
            BuildHCSR501(),

            // Microcontrollers & Development Boards (6)
            BuildArduinoNano(),
            BuildArduinoProMini(),
            BuildRaspberryPiPico(),
            BuildESP32DevKitV1_30Pin(),
            BuildESP32DevKitC_38Pin(),
            BuildSTM32BluePill()
        };

        var dict = new Dictionary<string, ComponentPinoutProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            dict[p.PartNumber] = p;
            foreach (var alias in p.Aliases)
            {
                dict[alias] = p;
            }
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 1. HC-SR04 - Ultrasonic Distance Sensor Module
    // =========================================================================
    private static ComponentPinoutProfile BuildHCSR04() => new(
        PartNumber: "HC-SR04",
        Aliases: ["HCSR04", "US-100-4PIN"],
        Category: "Sensor Module - Ultrasonic Ranging",
        Description: "4-Pin 40kHz Ultrasonic Distance Measurement Sensor Module (2cm - 400cm)",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "Looking at the front transducer cans with 4-pin header pointing down: Leftmost pin is Pin 1 (VCC), labeled on silkscreen.",
        SupplyVoltage: Logic5VStandard,
        LogicVoltage: Logic5VStandard,
        Pins:
        [
            new(1, "VCC",  PinFunction.PowerVcc,      Logic5VStandard, "5.0V DC supply voltage (classic HC-SR04 fails to range below 4.5V)"),
            new(2, "TRIG", PinFunction.DigitalInput,  Wide3V3To5V,     "Trigger pulse input (apply >= 10us HIGH pulse; accepts 3.3V or 5V logic)"),
            new(3, "ECHO", PinFunction.DigitalOutput, Logic5VStandard, "Echo pulse-width output (drives +5.0V HIGH proportional to round-trip flight time)"),
            new(4, "GND",  PinFunction.Ground,        null,            "0V ground reference")
        ],
        CommonWiringTraps:
        [
            new("HCSR04-TRAP-01", "Direct 5V ECHO Output Connected to 3.3V-Only MCU (ESP32, Pico, Raspberry Pi)", WiringTrapSeverity.CriticalDamageRisk, [3],
                "Classic HC-SR04 is powered by 5V and drives its ECHO pin (Pin 3) to +5.0V. Connecting ECHO directly to an ESP32, RP2040, or STM32 non-5V-tolerant pin injects 5V into the 3.3V rail.",
                "Require a 1k Ohm + 2k Ohm voltage divider on Pin 3 (ECHO) when interfacing with 3.3V-only microcontrollers."),
            new("HCSR04-TRAP-02", "Powering Classic HC-SR04 from 3.3V Rail", WiringTrapSeverity.LogicFailure, [1],
                "Standard HC-SR04 modules (unlike HC-SR04P/RCWL-1601) require a full 5.0V supply for their MAX232/charge-pump transmitter stage; at 3.3V they time out or return 0cm.",
                "Warn if Pin 1 (VCC) is connected to a 3.3V rail instead of 5.0V.")
        ]);

    // =========================================================================
    // 2. DHT11 - Digital Temperature & Humidity Sensor (4-Pin SIP)
    // =========================================================================
    private static ComponentPinoutProfile BuildDHT11() => new(
        PartNumber: "DHT11",
        Aliases: ["DHT11-4PIN"],
        Category: "Sensor - Temperature & Humidity",
        Description: "Single-Bus Digital Temperature (0-50C) and Relative Humidity (20-90% RH) Sensor",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.FlatFaceFront,
        Pin1MarkerDescription: "Looking at the front perforated plastic grille with leads pointing down: Leftmost pin is Pin 1 (VCC).",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Wide3V3To5V,
        Pins:
        [
            new(1, "VCC (VDD)", PinFunction.PowerVcc,             Wide3V3To5V, "Positive supply voltage (3.0V to 5.5V)"),
            new(2, "DATA",      PinFunction.DigitalBidirectional | PinFunction.OneWireData, Wide3V3To5V, "Open-drain single-wire serial data bus (requires 4.7k-10k pull-up resistor to VCC)"),
            new(3, "NC",        PinFunction.NoConnect,            null,        "No Connect (leave unconnected)"),
            new(4, "GND",       PinFunction.Ground,               null,        "0V ground reference")
        ],
        CommonWiringTraps:
        [
            new("DHT11-TRAP-01", "Missing 4.7k-10k Pull-Up Resistor on DATA (Pin 2)", WiringTrapSeverity.LogicFailure, [1, 2],
                "Bare 4-pin blue DHT11 sensors have an open-drain data pin. Without an external 4.7k-10k Ohm pull-up resistor between Pin 2 (DATA) and Pin 1 (VCC), communication always times out.",
                "Require a pull-up resistor between Pin 2 and Pin 1 on bare 4-pin DHT11 packages."),
            new("DHT11-TRAP-02", "Connecting GND to Pin 3 (NC) Instead of Pin 4", WiringTrapSeverity.LogicFailure, [3, 4],
                "Users counting 3 wires (VCC, DATA, GND) often plug GND into Pin 3 (NC) instead of Pin 4.",
                "Flag any wire connected to Pin 3; verify GND is connected to Pin 4.")
        ]);

    // =========================================================================
    // 3. DHT22 (AM2302) - High-Precision Temperature & Humidity Sensor
    // =========================================================================
    private static ComponentPinoutProfile BuildDHT22() => new(
        PartNumber: "DHT22",
        Aliases: ["AM2302", "RHT03"],
        Category: "Sensor - Temperature & Humidity",
        Description: "High-Precision Single-Bus Digital Temperature (-40 to +80C) and Humidity (0-100% RH) Sensor",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.FlatFaceFront,
        Pin1MarkerDescription: "Looking at the front white lattice grille with leads pointing down: Leftmost pin is Pin 1 (VDD).",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Wide3V3To5V,
        Pins:
        [
            new(1, "VDD",  PinFunction.PowerVdd,             Wide3V3To5V, "Positive supply voltage (3.3V to 5.5V)"),
            new(2, "SDA (DATA)", PinFunction.DigitalBidirectional | PinFunction.OneWireData, Wide3V3To5V, "Single-bus bidirectional data (NOT I2C despite 'SDA' datasheet label; requires 4.7k-10k pull-up)"),
            new(3, "NC",   PinFunction.NoConnect,            null,        "No Connect (leave unconnected or tied to GND per datasheet)"),
            new(4, "GND",  PinFunction.Ground,               null,        "0V ground reference")
        ],
        CommonWiringTraps:
        [
            new("DHT22-TRAP-01", "Wiring Pin 2 ('SDA') to an I2C Bus Alongside Other I2C Devices", WiringTrapSeverity.LogicFailure, [2],
                "Aosong's datasheet labels Pin 2 as 'SDA', tricking users into wiring DHT22 onto hardware I2C lines (A4/A5). DHT22 uses a proprietary timing-sensitive 1-Wire-like protocol that corrupts I2C bus traffic.",
                "Flag if DHT22 Pin 2 shares a net with I2C SCL/SDA devices."),
            new("DHT22-TRAP-02", "Missing Pull-Up Resistor on Pin 2", WiringTrapSeverity.LogicFailure, [1, 2],
                "Requires a 4.7k-10k Ohm pull-up resistor from Pin 2 to Pin 1 (VDD).",
                "Verify pull-up resistor is present between Pin 2 and Pin 1.")
        ]);

    // =========================================================================
    // 4. BMP280 (6-Pin Breakout) - Barometric Pressure & Temperature Sensor
    // =========================================================================
    private static ComponentPinoutProfile BuildBMP280() => new(
        PartNumber: "BMP280",
        Aliases: ["GY-BMP280-3.3", "BMP280-6PIN"],
        Category: "Sensor Module - Barometric Pressure & Temperature",
        Description: "Digital Barometric Pressure and Temperature Sensor Breakout (I2C / SPI 6-Pin Module)",
        PinCount: 6,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "6-pin header along PCB edge; Pin 1 (VCC) has a square solder pad.",
        SupplyVoltage: Logic3V3Only,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1, "VCC",       PinFunction.PowerVcc, Logic3V3Only, "3.3V supply input (most purple GY-BMP280-3.3 boards lack a 3.3V LDO regulator!)"),
            new(2, "GND",       PinFunction.Ground,   null,         "0V ground reference"),
            new(3, "SCL (SCK)", PinFunction.I2cScl | PinFunction.SpiSck | PinFunction.DigitalInput, Logic3V3Only, "I2C Clock / SPI Clock (3.3V logic ONLY)"),
            new(4, "SDA (SDI)", PinFunction.I2cSda | PinFunction.SpiMosi | PinFunction.DigitalBidirectional, Logic3V3Only, "I2C Data / SPI MOSI (3.3V logic ONLY)"),
            new(5, "CSB",       PinFunction.SpiCs | PinFunction.DigitalInput, Logic3V3Only, "Interface select: tie HIGH (or leave pulled up) for I2C mode; drive LOW for SPI mode"),
            new(6, "SDO",       PinFunction.SpiMiso | PinFunction.DigitalBidirectional, Logic3V3Only, "I2C Address LSB (GND = 0x76, 3.3V = 0x77) or SPI MISO")
        ],
        CommonWiringTraps:
        [
            new("BMP280-TRAP-01", "Applying 5.0V to Purple GY-BMP280-3.3 Breakout Without Onboard LDO", WiringTrapSeverity.CriticalDamageRisk, [1, 3, 4],
                "The ubiquitous purple 6-pin GY-BMP280-3.3 breakout does NOT include a 3.3V voltage regulator or level shifter. Applying 5V from an Arduino Uno permanently destroys the Bosch BMP280 MEMS die (Vdd_max = 3.6V).",
                "CRITICAL: Flag any connection > 3.6V on Pin 1 (VCC), Pin 3 (SCL), or Pin 4 (SDA)."),
            new("BMP280-TRAP-02", "I2C Address Mismatch (0x76 vs 0x77) Due to SDO (Pin 6)", WiringTrapSeverity.LogicFailure, [6],
                "Adafruit libraries default to I2C address 0x77 (SDO HIGH), whereas generic GY-BMP280 boards pull SDO (Pin 6) to GND (address 0x76).",
                "Info check on Pin 6 state to report active I2C address (0x76 if LOW/unconnected on GY-BMP280, 0x77 if tied to 3.3V).")
        ]);

    // =========================================================================
    // 5. BME280 (4-Pin I2C Breakout) - Humidity, Pressure & Temperature Sensor
    // =========================================================================
    private static ComponentPinoutProfile BuildBME280() => new(
        PartNumber: "BME280",
        Aliases: ["GY-BME280", "BME280-I2C"],
        Category: "Sensor Module - Environmental (Humidity, Pressure, Temp)",
        Description: "Integrated Digital Humidity, Barometric Pressure, and Temperature Sensor (4-Pin I2C Breakout)",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "4-pin header along edge; Pin 1 is VIN/VCC.",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Logic3V3FiveVoltTolerant,
        Pins:
        [
            new(1, "VIN (VCC)", PinFunction.PowerVcc, Wide3V3To5V, "Supply voltage (3.3V recommended; 5.0V supported only on 4-pin boards with onboard 662K LDO)"),
            new(2, "GND",       PinFunction.Ground,   null,        "0V ground reference"),
            new(3, "SCL",       PinFunction.I2cScl | PinFunction.DigitalInput, Logic3V3FiveVoltTolerant, "I2C Serial Clock"),
            new(4, "SDA",       PinFunction.I2cSda | PinFunction.DigitalBidirectional, Logic3V3FiveVoltTolerant, "I2C Serial Data")
        ],
        CommonWiringTraps:
        [
            new("BME280-TRAP-01", "Counterfeit BMP280 Sold as BME280 (Chip ID 0x58 vs 0x60)", WiringTrapSeverity.Warning, [1, 2, 3, 4],
                "Many inexpensive modules sold as BME280 actually populate a BMP280 (rectangular package with chip ID 0x58 instead of square package with chip ID 0x60), causing BME280 initialization to fail.",
                "AOI vision check: verify square metal-lid package geometry (2.5 x 2.5 mm) vs rectangular BMP280 (2.0 x 2.5 mm).")
        ]);

    // =========================================================================
    // 6. MPU6050 (GY-521) - 6-Axis Accelerometer & Gyroscope Module
    // =========================================================================
    private static ComponentPinoutProfile BuildMPU6050() => new(
        PartNumber: "MPU6050",
        Aliases: ["GY-521", "MPU-6050"],
        Category: "Sensor Module - 6-DoF IMU",
        Description: "3-Axis MEMS Accelerometer and 3-Axis MEMS Gyroscope with I2C Digital Motion Processor (GY-521)",
        PinCount: 8,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "8-pin header on GY-521 PCB; Pin 1 (VCC) is marked with a square solder pad.",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1, "VCC", PinFunction.PowerVcc, Wide3V3To5V, "Power input (3.3V to 5.0V feeding onboard KB33/4B2X 3.3V LDO regulator)"),
            new(2, "GND", PinFunction.Ground,   null,        "0V ground reference"),
            new(3, "SCL", PinFunction.I2cScl | PinFunction.DigitalInput, Logic3V3Only, "Primary I2C Serial Clock (pulled up to onboard 3.3V via 4.7k resistor)"),
            new(4, "SDA", PinFunction.I2cSda | PinFunction.DigitalBidirectional, Logic3V3Only, "Primary I2C Serial Data (pulled up to onboard 3.3V via 4.7k resistor)"),
            new(5, "XDA", PinFunction.I2cSda,   Logic3V3Only, "Auxiliary master I2C Data for external magnetometer (e.g. HMC5883L); do NOT connect MCU SDA here!"),
            new(6, "XCL", PinFunction.I2cScl,   Logic3V3Only, "Auxiliary master I2C Clock for external magnetometer; do NOT connect MCU SCL here!"),
            new(7, "AD0", PinFunction.DigitalInput, Logic3V3Only, "I2C Slave Address LSB (LOW/floating = 0x68, tied to 3.3V = 0x69; do NOT apply 5V directly to AD0!)"),
            new(8, "INT", PinFunction.DigitalOutput, Logic3V3Only, "Interrupt output (3.3V push-pull or open-drain configurable)")
        ],
        CommonWiringTraps:
        [
            new("MPU6050-TRAP-01", "Connecting MCU I2C Bus to XDA/XCL (Pins 5, 6) Instead of SDA/SCL (Pins 3, 4)", WiringTrapSeverity.LogicFailure, [3, 4, 5, 6],
                "Users mistake XDA/XCL (Auxiliary I2C master bus for external compass) for the primary slave I2C bus. The MCU must connect to SCL (Pin 3) and SDA (Pin 4).",
                "Flag if MCU I2C pins are wired to Pin 5 (XDA) or Pin 6 (XCL)."),
            new("MPU6050-TRAP-02", "Applying 5.0V Directly to AD0 (Pin 7) to Select Address 0x69", WiringTrapSeverity.CriticalDamageRisk, [7],
                "While GY-521 has a 3.3V LDO on Pin 1 (VCC), Pin 7 (AD0) goes straight to the 3.3V MPU6050 ASIC without level shifting. Tying AD0 to a 5V rail damages the chip.",
                "Flag if Pin 7 (AD0) is connected to a > 3.6V rail; require 3.3V logic high on AD0.")
        ]);

    // =========================================================================
    // 7. DS3231 (ZS-042 Module) - Precision I2C Real-Time Clock
    // =========================================================================
    private static ComponentPinoutProfile BuildDS3231() => new(
        PartNumber: "DS3231",
        Aliases: ["ZS-042", "DS3231-MODULE", "DS3231SN"],
        Category: "Sensor / Timing Module - Real-Time Clock (RTC)",
        Description: "Extremely Accurate I2C Integrated TCXO Real-Time Clock Module with AT24C32 EEPROM",
        PinCount: 6,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "6-pin primary header on ZS-042 board: Pin 1 is 32K, Pin 5 is VCC, Pin 6 is GND.",
        SupplyVoltage: new VoltageRange(2.3, 3.3, 5.5, 5.5, true),
        LogicVoltage: new VoltageRange(2.3, 3.3, 5.5, 5.5, true),
        Pins:
        [
            new(1, "32K",     PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, Wide3V3To5V, "32.768 kHz open-drain clock output"),
            new(2, "SQW/INT", PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, Wide3V3To5V, "Active-LOW alarm interrupt or programmable square-wave open-drain output"),
            new(3, "SCL",     PinFunction.I2cScl | PinFunction.DigitalInput, Wide3V3To5V, "I2C Serial Clock (Fixed RTC address 0x68, AT24C32 EEPROM 0x57)"),
            new(4, "SDA",     PinFunction.I2cSda | PinFunction.DigitalBidirectional, Wide3V3To5V, "I2C Serial Data"),
            new(5, "VCC",     PinFunction.PowerVcc, Wide3V3To5V, "Supply voltage (2.3V to 5.5V; 3.3V strongly recommended on ZS-042 modules with CR2032)"),
            new(6, "GND",     PinFunction.Ground,   null,        "0V ground reference")
        ],
        CommonWiringTraps:
        [
            new("DS3231-TRAP-01", "ZS-042 Trickle-Charging Circuit Overcharging Non-Rechargeable CR2032 at 5V VCC", WiringTrapSeverity.CriticalDamageRisk, [5],
                "The popular ZS-042 DS3231 module includes a 200-Ohm resistor and LL4148 diode from VCC directly to the coin cell holder intended for rechargeable LIR2032 cells. When powered at 5.0V with a standard non-rechargeable CR2032 battery installed, it forces ~4.3V into the 3.0V lithium primary cell, causing battery swelling or rupture.",
                "Warn when ZS-042 VCC (Pin 5) is connected to 5.0V: either power VCC from 3.3V or remove the onboard trickle-charge resistor/diode.")
        ]);

    // =========================================================================
    // 8. RC522 (MFRC522) - 13.56MHz RFID SPI Reader/Writer Module
    // =========================================================================
    private static ComponentPinoutProfile BuildRC522() => new(
        PartNumber: "RC522",
        Aliases: ["MFRC522", "RFID-RC522"],
        Category: "Sensor / RF Module - 13.56MHz RFID/NFC",
        Description: "MFRC522 13.56MHz Contactless RFID/NFC Reader and Writer Module (8-Pin Header)",
        PinCount: 8,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "8-pin header at bottom of blue PCB; Pin 1 is SDA (SPI SS/CS).",
        SupplyVoltage: Logic3V3Only,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1, "SDA (SS/CS)", PinFunction.SpiCs | PinFunction.DigitalInput,   Logic3V3Only, "SPI Chip Select (labeled 'SDA' on silkscreen, but functions as SPI SS/CS in default SPI strapping!)"),
            new(2, "SCK",         PinFunction.SpiSck | PinFunction.DigitalInput,  Logic3V3Only, "SPI Serial Clock"),
            new(3, "MOSI",        PinFunction.SpiMosi | PinFunction.DigitalInput, Logic3V3Only, "SPI Master Out Slave In"),
            new(4, "MISO",        PinFunction.SpiMiso | PinFunction.DigitalOutput,Logic3V3Only, "SPI Master In Slave Out"),
            new(5, "IRQ",         PinFunction.DigitalOutput,                      Logic3V3Only, "Interrupt output (usually left unconnected in polling libraries)"),
            new(6, "GND",         PinFunction.Ground,                             null,         "0V ground reference"),
            new(7, "RST",         PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic3V3Only, "Reset and Power-Down input (3.3V logic ONLY)"),
            new(8, "3.3V (VCC)",  PinFunction.PowerVcc,                           Logic3V3Only, "3.3V supply ONLY (NO onboard 5V regulator; 5.0V destroys MFRC522!)")
        ],
        CommonWiringTraps:
        [
            new("RC522-TRAP-01", "Applying 5.0V to Pin 8 (3.3V) or 5V Logic Directly to SPI/RST Pins", WiringTrapSeverity.CriticalDamageRisk, [1, 2, 3, 7, 8],
                "NXP MFRC522 has a strict 3.6V maximum supply and is NOT 5V-tolerant. Connecting Pin 8 to 5V kills the chip immediately.",
                "CRITICAL: Reject any connection > 3.6V on Pin 8 (3.3V) and warn on 5V MCU GPIO connections without level shifting."),
            new("RC522-TRAP-02", "Misinterpreting Silkscreen 'SDA' (Pin 1) as I2C Data Instead of SPI Chip Select", WiringTrapSeverity.LogicFailure, [1],
                "Because the PCB silkscreen prints 'SDA' on Pin 1, beginners connect Pin 1 to Arduino A4 (I2C SDA). On standard RC522 modules, EA/I2C strapping pins are hardwired on the PCB for SPI mode, so Pin 1 is SPI Slave Select (SS/CS).",
                "Flag if Pin 1 is connected to an I2C bus without SPI SCK/MOSI/MISO connected.")
        ]);

    // =========================================================================
    // 9. SSD1306 (4-Pin I2C OLED) - 0.96" 128x64 Monochrome OLED Display
    // =========================================================================
    private static ComponentPinoutProfile BuildSSD1306I2C4Pin() => new(
        PartNumber: "SSD1306-I2C-4PIN",
        Aliases: ["SSD1306", "OLED-128X64-I2C", "SH1106-I2C"],
        Category: "Display Module - I2C OLED",
        Description: "0.96-Inch 128x64 Monochrome OLED Display Module with 4-Pin I2C Interface (Address 0x3C/0x3D)",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "4-pin top header; verify silkscreen carefully because GND-VCC vs VCC-GND order varies by manufacturer! Standard modern blue/white PCB order: Pin 1=GND, Pin 2=VCC, Pin 3=SCL, Pin 4=SDA.",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Wide3V3To5V,
        Pins:
        [
            new(1, "GND",       PinFunction.Ground,   null,        "0V ground reference (WARNING: some clone boards swap Pin 1 and Pin 2!)"),
            new(2, "VCC (VDD)", PinFunction.PowerVcc, Wide3V3To5V, "Supply voltage (3.3V to 5.0V via onboard 662K 3.3V regulator)"),
            new(3, "SCL (SCK)", PinFunction.I2cScl | PinFunction.DigitalInput, Wide3V3To5V, "I2C Serial Clock"),
            new(4, "SDA",       PinFunction.I2cSda | PinFunction.DigitalBidirectional, Wide3V3To5V, "I2C Serial Data")
        ],
        CommonWiringTraps:
        [
            new("SSD1306-TRAP-01", "Reversed GND and VCC on Pin 1 vs Pin 2 Across Clone Variants", WiringTrapSeverity.CriticalDamageRisk, [1, 2],
                "SSD1306 4-pin OLED modules exist in two incompatible pinouts in the wild: Type A (GND-VCC-SCL-SDA) and Type B (VCC-GND-SCL-SDA). Reversing Pins 1 and 2 burns out the onboard 662K LDO regulator and OLED charge pump within 2 seconds.",
                "AOI OCR check: read silkscreen text under Pins 1 and 2 before certifying power rail polarity.")
        ]);

    // =========================================================================
    // 10. LCD1602-I2C - 16x2 Character LCD with PCF8574 I2C Backpack
    // =========================================================================
    private static ComponentPinoutProfile BuildLCD1602I2C() => new(
        PartNumber: "LCD1602-I2C",
        Aliases: ["1602-LCD-I2C", "LCD2004-I2C", "HD44780-I2C"],
        Category: "Display Module - Character LCD (I2C)",
        Description: "16x2 HD44780 Character Liquid Crystal Display with Soldered PCF8574 I2C Backpack (0x27 or 0x3F)",
        PinCount: 4,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "4-pin right-angle male header protruding from black PCF8574 backpack PCB: Top/Pin 1 = GND, Pin 2 = VCC, Pin 3 = SDA, Pin 4 = SCL.",
        SupplyVoltage: Logic5VStandard,
        LogicVoltage: Logic5VStandard,
        Pins:
        [
            new(1, "GND", PinFunction.Ground,   null,            "0V ground reference"),
            new(2, "VCC", PinFunction.PowerVcc, Logic5VStandard, "5.0V DC supply (standard HD44780 fluid requires ~4.5V VLCD contrast potential; invisible characters at 3.3V)"),
            new(3, "SDA", PinFunction.I2cSda | PinFunction.DigitalBidirectional, Logic5VStandard, "I2C Serial Data (pulled up to backpack 5V VCC via 4.7k resistor)"),
            new(4, "SCL", PinFunction.I2cScl | PinFunction.DigitalInput, Logic5VStandard, "I2C Serial Clock (pulled up to backpack 5V VCC via 4.7k resistor)")
        ],
        CommonWiringTraps:
        [
            new("LCD1602-TRAP-01", "Powering Standard 5V LCD1602 from 3.3V Rail (Backlight Lights Up, Characters Invisible)", WiringTrapSeverity.LogicFailure, [2],
                "When powered from 3.3V, the LED backlight illuminates and the PCF8574 responds on I2C, but the nematic LCD fluid lacks enough voltage even with the blue contrast potentiometer turned fully to GND.",
                "Warn if Pin 2 (VCC) is connected to 3.3V instead of 5.0V."),
            new("LCD1602-TRAP-02", "5V I2C Pull-Ups on Backpack Connected Directly to 3.3V-Only MCU", WiringTrapSeverity.CriticalDamageRisk, [2, 3, 4],
                "The black I2C backpack pulls SDA and SCL up to its VCC (5.0V) via onboard 4.7k resistors. Connecting SDA/SCL directly to a 3.3V-only MCU while VCC is 5V pulls the MCU pins toward 5V.",
                "Recommend bidirectional I2C level shifter when pairing 5V LCD1602-I2C with 3.3V-only MCUs.")
        ]);

    // =========================================================================
    // 11. L298N-MODULE - Dual Full-Bridge Motor Driver Module
    // =========================================================================
    private static ComponentPinoutProfile BuildL298NModule()
    {
        var vmRange = new VoltageRange(5.0, 12.0, 35.0, 46.0, true);
        return new(
            PartNumber: "L298N-MODULE",
            Aliases: ["L298N", "HW-095"],
            Category: "Motor Driver Module - Dual H-Bridge",
            Description: "L298N 2A Dual Full-Bridge DC/Stepper Motor Driver Module (Logic Header + Power Terminal Block)",
            PinCount: 9,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "Pins 1-6 represent the 6-pin logic header (ENA, IN1, IN2, IN3, IN4, ENB); Pins 7-9 represent the 3-position blue screw terminal (+12V/VMS, GND, +5V).",
            SupplyVoltage: vmRange,
            LogicVoltage: Logic5VStandard,
            Pins:
            [
                new(1, "ENA",      PinFunction.DigitalInput | PinFunction.PwmOutput | PinFunction.ActiveHighEnable, Logic5VStandard, "Motor A Enable / PWM speed input (remove jumper cap to drive with PWM)"),
                new(2, "IN1",      PinFunction.DigitalInput, Logic5VStandard, "Motor A direction control 1"),
                new(3, "IN2",      PinFunction.DigitalInput, Logic5VStandard, "Motor A direction control 2"),
                new(4, "IN3",      PinFunction.DigitalInput, Logic5VStandard, "Motor B direction control 1"),
                new(5, "IN4",      PinFunction.DigitalInput, Logic5VStandard, "Motor B direction control 2"),
                new(6, "ENB",      PinFunction.DigitalInput | PinFunction.PwmOutput | PinFunction.ActiveHighEnable, Logic5VStandard, "Motor B Enable / PWM speed input (remove jumper cap to drive with PWM)"),
                new(7, "VMS (+12V)",PinFunction.MotorSupplyVs, vmRange,       "Motor power supply screw terminal (+5V to +35V DC; ~2V internal bipolar Darlington drop)"),
                new(8, "GND",      PinFunction.Ground,       null,            "Common Ground screw terminal (MUST connect to both motor battery negative AND MCU GND)"),
                new(9, "+5V_LOGIC",PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic5VStandard, "5V logic terminal (OUTPUT when 5V-EN jumper is installed and VMS <= 12V; INPUT when 5V-EN jumper is removed)")
            ],
            CommonWiringTraps:
            [
                new("L298N-TRAP-01", "Feeding External 5V from Arduino into Pin 9 (+5V Terminal) While Onboard 5V-EN Jumper Is Installed", WiringTrapSeverity.CriticalDamageRisk, [7, 9],
                    "When the onboard 5V-EN jumper cap is installed, the L298N module's onboard 78M05 regulator drives +5V OUT on Pin 9. Connecting the Arduino's 5V output to Pin 9 at the same time causes two 5V voltage regulators to fight each other.",
                    "Flag if Pin 9 is connected to an external 5V supply while the onboard 5V regulator is enabled."),
                new("L298N-TRAP-02", "Applying PWM to ENA/ENB (Pins 1, 6) Without Removing the Enable Jumper Caps", WiringTrapSeverity.CriticalDamageRisk, [1, 6],
                    "L298N modules ship with black jumper caps shorting ENA and ENB directly to the +5V rail. Driving an MCU PWM pin into ENA/ENB while that jumper is still installed shorts the MCU GPIO directly to +5V whenever the PWM signal goes LOW!",
                    "Require ENA/ENB jumper caps to be removed before connecting MCU GPIO pins to Pin 1 or Pin 6.")
            ]);
    }

    // =========================================================================
    // 12. TB6612FNG - Dual MOSFET H-Bridge Motor Driver Breakout
    // =========================================================================
    private static ComponentPinoutProfile BuildTB6612FNG()
    {
        var vmRange = new VoltageRange(2.5, 12.0, 13.5, 15.0, true);
        var vccRange = new VoltageRange(2.7, 3.3, 5.5, 6.0, true);
        return new(
            PartNumber: "TB6612FNG",
            Aliases: ["TB6612", "DRV8833-BREAKOUT"],
            Category: "Motor Driver Module - Dual MOSFET H-Bridge",
            Description: "Dual 1.2A Continuous (3.2A Peak) Low-Dropout MOSFET H-Bridge Motor Driver Breakout (16-Pin 0.6\" Wide)",
            PinCount: 16,
            WidthCategory: DipWidthCategory.Wide600Mil,
            RowSpacingInches: 0.6,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "16-pin dual-row breakout (8 pins per side); Pin 1 is VM (Top-Left).",
            SupplyVoltage: vmRange,
            LogicVoltage: vccRange,
            Pins:
            [
                new(1,  "VM",    PinFunction.MotorSupplyVs, vmRange,  "Motor supply voltage (2.5V to 13.5V)"),
                new(2,  "VCC",   PinFunction.PowerVcc,      vccRange, "Logic supply voltage (2.7V to 5.5V)"),
                new(3,  "GND_1", PinFunction.Ground,        null,     "Ground 1"),
                new(4,  "AO1",   PinFunction.DigitalOutput, vmRange,  "Motor A output 1"),
                new(5,  "AO2",   PinFunction.DigitalOutput, vmRange,  "Motor A output 2"),
                new(6,  "BO2",   PinFunction.DigitalOutput, vmRange,  "Motor B output 2"),
                new(7,  "BO1",   PinFunction.DigitalOutput, vmRange,  "Motor B output 1"),
                new(8,  "GND_2", PinFunction.Ground,        null,     "Ground 2"),
                new(9,  "GND_3", PinFunction.Ground,        null,     "Ground 3"),
                new(10, "PWMB",  PinFunction.DigitalInput | PinFunction.PwmOutput, vccRange, "Motor B PWM speed input (tie HIGH if not using speed control)"),
                new(11, "BIN2",  PinFunction.DigitalInput,  vccRange, "Motor B direction control 2"),
                new(12, "BIN1",  PinFunction.DigitalInput,  vccRange, "Motor B direction control 1"),
                new(13, "STBY",  PinFunction.DigitalInput | PinFunction.ActiveHighEnable, vccRange, "Standby control (active-LOW standby; MUST be pulled HIGH to VCC to run motors!)"),
                new(14, "AIN1",  PinFunction.DigitalInput,  vccRange, "Motor A direction control 1"),
                new(15, "AIN2",  PinFunction.DigitalInput,  vccRange, "Motor A direction control 2"),
                new(16, "PWMA",  PinFunction.DigitalInput | PinFunction.PwmOutput, vccRange, "Motor A PWM speed input (tie HIGH if not using speed control)")
            ],
            CommonWiringTraps:
            [
                new("TB6612-TRAP-01", "Leaving STBY (Pin 13) Unconnected", WiringTrapSeverity.LogicFailure, [13],
                    "Pin 13 (STBY) has an internal 200k pull-down resistor that holds the entire driver in low-power standby mode unless Pin 13 is explicitly tied to VCC or driven HIGH by a GPIO.",
                    "Require Pin 13 (STBY) to be connected to VCC or an MCU digital output.")
            ]);
    }

    // =========================================================================
    // 13. ADS1115 - 16-Bit 4-Channel I2C ADC with Programmable Gain Amplifier
    // =========================================================================
    private static ComponentPinoutProfile BuildADS1115()
    {
        var supply = new VoltageRange(2.0, 3.3, 5.5, 5.5, true);
        return new(
            PartNumber: "ADS1115",
            Aliases: ["ADS1015", "GY-ADS1115"],
            Category: "Sensor / Converter Module - 16-Bit I2C ADC",
            Description: "Ultra-Small 16-Bit 860SPS 4-Channel Single-Ended / 2-Channel Differential I2C ADC with PGA",
            PinCount: 10,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "10-pin SIP header; Pin 1 is VDD.",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "VDD",  PinFunction.PowerVdd, supply, "Supply voltage (2.0V to 5.5V; sets absolute ceiling for analog inputs A0-A3)"),
                new(2,  "GND",  PinFunction.Ground,   null,   "0V ground reference"),
                new(3,  "SCL",  PinFunction.I2cScl | PinFunction.DigitalInput, supply, "I2C Serial Clock"),
                new(4,  "SDA",  PinFunction.I2cSda | PinFunction.DigitalBidirectional, supply, "I2C Serial Data"),
                new(5,  "ADDR", PinFunction.DigitalInput, supply, "I2C Address select (GND=0x48, VDD=0x49, SDA=0x4A, SCL=0x4B)"),
                new(6,  "ALRT", PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, supply, "Comparator Alert / Conversion Ready open-drain output"),
                new(7,  "A0",   PinFunction.AnalogInput, supply, "Analog input 0 (WARNING: must remain between GND and VDD + 0.3V regardless of PGA gain setting!)"),
                new(8,  "A1",   PinFunction.AnalogInput, supply, "Analog input 1"),
                new(9,  "A2",   PinFunction.AnalogInput, supply, "Analog input 2"),
                new(10, "A3",   PinFunction.AnalogInput, supply, "Analog input 3")
            ],
            CommonWiringTraps:
            [
                new("ADS1115-TRAP-01", "Applying 5V Analog Signal to A0-A3 While ADS1115 VDD Is Powered at 3.3V (PGA +/-6.144V Trap)", WiringTrapSeverity.CriticalDamageRisk, [1, 7, 8, 9, 10],
                    "The software PGA setting of +/-6.144V is purely a full-scale digital scaling factor! Electrically, no analog input pin (A0-A3) may EVER exceed VDD + 0.3V. Feeding a 5.0V sensor into A0 when VDD is 3.3V destroys the ESD diodes.",
                    "CRITICAL: Flag any analog source on Pins 7-10 whose max voltage exceeds Pin 1 (VDD) + 0.3V.")
            ]);
    }

    // =========================================================================
    // 14. DS18B20 - Programmable Resolution 1-Wire Digital Thermometer
    // =========================================================================
    private static ComponentPinoutProfile BuildDS18B20() => new(
        PartNumber: "DS18B20",
        Aliases: ["DS18B20+PAR", "DS18S20", "DS1820"],
        Category: "Sensor - 1-Wire Digital Thermometer",
        Description: "Programmable 9-to-12 Bit 1-Wire Digital Temperature Sensor (-55C to +125C, TO-92-3)",
        PinCount: 3,
        WidthCategory: DipWidthCategory.SingleInLine100Mil,
        RowSpacingInches: 0.1,
        Pin1Marker: Pin1OrientationMarker.FlatFaceFront,
        Pin1MarkerDescription: "Looking at the flat plastic front face of the TO-92 package with leads pointing down: Left=Pin 1 (GND), Center=Pin 2 (DQ), Right=Pin 3 (VDD).",
        SupplyVoltage: Wide3V3To5V,
        LogicVoltage: Wide3V3To5V,
        Pins:
        [
            new(1, "GND", PinFunction.Ground, null, "0V ground reference"),
            new(2, "DQ",  PinFunction.DigitalBidirectional | PinFunction.OneWireData, Wide3V3To5V, "1-Wire Open-Drain Data Input/Output (requires 4.7k Ohm pull-up resistor to VDD)"),
            new(3, "VDD", PinFunction.PowerVdd, Wide3V3To5V, "Positive supply voltage (3.0V to 5.5V; tie to GND for parasite power mode)")
        ],
        CommonWiringTraps:
        [
            new("DS18B20-TRAP-01", "Reversed TO-92 Orientation (Swapping GND Pin 1 and VDD Pin 3)", WiringTrapSeverity.CriticalDamageRisk, [1, 3],
                "Inserting the TO-92 DS18B20 backwards swaps GND (Pin 1) and VDD (Pin 3), forward-biasing the substrate diode and heating the sensor to > 100C within seconds.",
                "AOI check: verify flat TO-92 face orientation matches Pin 1 = GND, Pin 3 = VDD."),
            new("DS18B20-TRAP-02", "Missing 4.7k Ohm Pull-Up Resistor on DQ (Pin 2)", WiringTrapSeverity.LogicFailure, [2, 3],
                "1-Wire communication requires an external 4.7k Ohm pull-up resistor from Pin 2 (DQ) to Pin 3 (VDD); internal MCU pull-ups (30k-50k) are too weak for 1-Wire rise times.",
                "Require a 2.2k-10k Ohm pull-up resistor between Pin 2 and VDD.")
        ]);

    // =========================================================================
    // 15. HC-SR501 - Passive Infrared (PIR) Motion Sensor Module
    // =========================================================================
    private static ComponentPinoutProfile BuildHCSR501()
    {
        var vccRange = new VoltageRange(4.5, 5.0, 20.0, 20.0, true);
        return new(
            PartNumber: "HC-SR501",
            Aliases: ["HCSR501", "PIR-HCSR501"],
            Category: "Sensor Module - PIR Motion Detector",
            Description: "BISS0001 Pyroelectric Passive Infrared (PIR) Motion Sensor Module with Fresnel Lens",
            PinCount: 3,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "With white Fresnel dome on back and looking at PCB components with 3-pin header at bottom: Left (next to protection diode) = Pin 1 (VCC), Center = Pin 2 (OUT), Right (next to green capacitor) = Pin 3 (GND).",
            SupplyVoltage: vccRange,
            LogicVoltage: Logic3V3Only,
            Pins:
            [
                new(1, "VCC", PinFunction.PowerVcc,      vccRange,     "DC supply input (4.5V to 20V via onboard diode + HT7133-1 3.3V regulator)"),
                new(2, "OUT", PinFunction.DigitalOutput, Logic3V3Only, "Digital motion output (3.3V HIGH when motion detected, 0V LOW when idle; safe for both 3.3V and 5V MCUs)"),
                new(3, "GND", PinFunction.Ground,        null,         "0V ground reference")
            ],
            CommonWiringTraps:
            [
                new("HCSR501-TRAP-01", "Powering HC-SR501 from 3.3V Rail on Pin 1 (VCC)", WiringTrapSeverity.LogicFailure, [1],
                    "Pin 1 passes through a series reverse-polarity diode (~0.7V drop) and a 3.3V linear regulator before feeding the BISS0001 IC. Supplying 3.3V on Pin 1 drops the internal rail to ~2.4V, causing continuous false triggers.",
                    "Require >= 4.5V (typically 5.0V) on Pin 1 (VCC).")
            ]);
    }

    // =========================================================================
    // 16. ARDUINO-NANO - ATmega328P Nano V3.0 Development Board (DIP-30)
    // =========================================================================
    private static ComponentPinoutProfile BuildArduinoNano()
    {
        var vinRange = new VoltageRange(7.0, 9.0, 12.0, 20.0, true);
        return new(
            PartNumber: "ARDUINO-NANO",
            Aliases: ["NANO-V3", "ARDUINO-NANO-EVERY", "NANO-ATMEGA328P"],
            Category: "Microcontroller Devboard - 8-Bit AVR (5V)",
            Description: "Arduino Nano V3.0 (ATmega328P, 5V 16MHz, 30-Pin 0.6\" Wide DIP Footprint)",
            PinCount: 30,
            WidthCategory: DipWidthCategory.Wide600Mil,
            RowSpacingInches: 0.6,
            Pin1Marker: Pin1OrientationMarker.UsbConnectorTopEdge,
            Pin1MarkerDescription: "With USB connector at top edge: Pin 1 (D1/TX) is Top-Left; Pin 15 (VIN) is Bottom-Left; Pin 16 (D13) is Bottom-Right; Pin 30 (D12) is Top-Right.",
            SupplyVoltage: Logic5VStandard,
            LogicVoltage: Logic5VStandard,
            Pins:
            [
                new(1,  "D1 (TX)",    PinFunction.DigitalBidirectional | PinFunction.UartTx, Logic5VStandard, "Hardware UART TX (shared with USB-Serial CH340/FT232)"),
                new(2,  "D0 (RX)",    PinFunction.DigitalBidirectional | PinFunction.UartRx, Logic5VStandard, "Hardware UART RX (shared with USB-Serial CH340/FT232)"),
                new(3,  "RESET_1",    PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic5VStandard, "Active-LOW MCU Reset"),
                new(4,  "GND_1",      PinFunction.Ground,               null,            "Ground reference 1"),
                new(5,  "D2 (INT0)",  PinFunction.DigitalBidirectional, Logic5VStandard, "Digital I/O 2 / External Interrupt 0"),
                new(6,  "D3~ (INT1)", PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 3 / PWM / External Interrupt 1"),
                new(7,  "D4",         PinFunction.DigitalBidirectional, Logic5VStandard, "Digital I/O 4"),
                new(8,  "D5~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 5 / PWM"),
                new(9,  "D6~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 6 / PWM"),
                new(10, "D7",         PinFunction.DigitalBidirectional, Logic5VStandard, "Digital I/O 7"),
                new(11, "D8",         PinFunction.DigitalBidirectional, Logic5VStandard, "Digital I/O 8"),
                new(12, "D9~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 9 / PWM"),
                new(13, "D10~ (SS)",  PinFunction.DigitalBidirectional | PinFunction.SpiCs | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 10 / SPI SS / PWM"),
                new(14, "D11~ (MOSI)",PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic5VStandard, "Digital I/O 11 / SPI MOSI / PWM"),
                new(15, "D12 (MISO)", PinFunction.DigitalBidirectional | PinFunction.SpiMiso, Logic5VStandard, "Digital I/O 12 / SPI MISO"),
                new(16, "VIN",        PinFunction.PowerVcc,             vinRange,        "Unregulated 7V-12V DC input to onboard AMS1117-5.0 / UA78M05 regulator"),
                new(17, "GND_2",      PinFunction.Ground,               null,            "Ground reference 2"),
                new(18, "RESET_2",    PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic5VStandard, "Active-LOW MCU Reset"),
                new(19, "+5V",        PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic5VStandard, "Regulated +5.0V rail (input or output)"),
                new(20, "A7",         PinFunction.AnalogInput,          Logic5VStandard, "ADC Channel 7 (ANALOG INPUT ONLY - cannot be used as digital I/O!)"),
                new(21, "A6",         PinFunction.AnalogInput,          Logic5VStandard, "ADC Channel 6 (ANALOG INPUT ONLY - cannot be used as digital I/O!)"),
                new(22, "A5 (SCL)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cScl, Logic5VStandard, "I2C SCL / Analog 5"),
                new(23, "A4 (SDA)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cSda, Logic5VStandard, "I2C SDA / Analog 4"),
                new(24, "A3",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Logic5VStandard, "Analog 3 / Digital I/O"),
                new(25, "A2",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Logic5VStandard, "Analog 2 / Digital I/O"),
                new(26, "A1",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Logic5VStandard, "Analog 1 / Digital I/O"),
                new(27, "A0",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Logic5VStandard, "Analog 0 / Digital I/O"),
                new(28, "AREF",       PinFunction.AnalogInput | PinFunction.VoltageReference, Logic5VStandard, "External ADC voltage reference"),
                new(29, "3V3",        PinFunction.RegulatedVoltageOut,  Logic3V3Only,    "3.3V auxiliary output from USB-Serial chip (MAX 30-50mA ONLY!)"),
                new(30, "D13 (SCK)",  PinFunction.DigitalBidirectional | PinFunction.SpiSck, Logic5VStandard, "Digital I/O 13 / SPI SCK / Onboard LED")
            ],
            CommonWiringTraps:
            [
                new("NANO-TRAP-01", "Using A6 (Pin 21) or A7 (Pin 20) as Digital Inputs/Outputs", WiringTrapSeverity.LogicFailure, [20, 21],
                    "On the TQFP-32 ATmega328P used on the Arduino Nano, pins A6 and A7 are wired exclusively to the ADC multiplexer and have NO digital input buffer or output driver (digitalRead/digitalWrite silently fail).",
                    "Flag any digital actuator, LED, I2C, or switch without ADC reading connected to Pin 20 (A7) or Pin 21 (A6)."),
                new("NANO-TRAP-02", "Powering High-Current 3.3V Modules (ESP8266, nRF24L01+, Servos) from Nano 3V3 Pin (Pin 29)", WiringTrapSeverity.CriticalDamageRisk, [29],
                    "The 3V3 pin on an Arduino Nano comes from the internal LDO inside the FT232RL or CH340G USB-serial chip, rated for only 30-50mA.",
                    "Warn if high-current 3.3V peripherals are powered from Pin 29 (3V3)."),
                new("NANO-TRAP-03", "Supplying 5.0V into VIN (Pin 16) Instead of +5V (Pin 19)", WiringTrapSeverity.LogicFailure, [16, 19],
                    "Pin 16 (VIN) feeds a linear 5V regulator with a ~1.5V dropout. Supplying 5.0V to VIN drops the MCU rail to ~3.5V, causing unstable operation at 16MHz.",
                    "Flag if a 5.0V supply is connected to Pin 16 (VIN) instead of Pin 19 (+5V).")
            ]);
    }

    // =========================================================================
    // 17. ARDUINO-PRO-MINI - ATmega328P Pro Mini Board (24-Pin Breadboard Header)
    // =========================================================================
    private static ComponentPinoutProfile BuildArduinoProMini()
    {
        var rawRange = new VoltageRange(3.35, 5.0, 12.0, 12.0, true);
        return new(
            PartNumber: "ARDUINO-PRO-MINI",
            Aliases: ["PRO-MINI-5V", "PRO-MINI-3V3", "PROMINI"],
            Category: "Microcontroller Devboard - 8-Bit AVR (3.3V / 5V)",
            Description: "Arduino Pro Mini ATmega328P Minimalist Development Board (24 Breadboard Side-Header Pins, 0.6\" Wide)",
            PinCount: 24,
            WidthCategory: DipWidthCategory.Wide600Mil,
            RowSpacingInches: 0.6,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "With 6-pin FTDI programming header at top edge: Pin 1 (TXO) is Top-Left; Pin 12 (D9) is Bottom-Left; Pin 13 (D10) is Bottom-Right; Pin 24 (RAW) is Top-Right.",
            SupplyVoltage: Wide3V3To5V,
            LogicVoltage: Wide3V3To5V,
            Pins:
            [
                new(1,  "TXO (D1)",   PinFunction.DigitalBidirectional | PinFunction.UartTx, Wide3V3To5V, "UART TX / Digital 1"),
                new(2,  "RXI (D0)",   PinFunction.DigitalBidirectional | PinFunction.UartRx, Wide3V3To5V, "UART RX / Digital 0"),
                new(3,  "RST_1",      PinFunction.DigitalInput | PinFunction.ActiveLowReset, Wide3V3To5V, "Active-LOW Reset"),
                new(4,  "GND_1",      PinFunction.Ground,               null,        "Ground 1"),
                new(5,  "D2 (INT0)",  PinFunction.DigitalBidirectional, Wide3V3To5V, "Digital I/O 2"),
                new(6,  "D3~ (INT1)", PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 3 / PWM"),
                new(7,  "D4",         PinFunction.DigitalBidirectional, Wide3V3To5V, "Digital I/O 4"),
                new(8,  "D5~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 5 / PWM"),
                new(9,  "D6~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 6 / PWM"),
                new(10, "D7",         PinFunction.DigitalBidirectional, Wide3V3To5V, "Digital I/O 7"),
                new(11, "D8",         PinFunction.DigitalBidirectional, Wide3V3To5V, "Digital I/O 8"),
                new(12, "D9~",        PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 9 / PWM"),
                new(13, "D10~ (SS)",  PinFunction.DigitalBidirectional | PinFunction.SpiCs | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 10 / SPI SS / PWM"),
                new(14, "D11~ (MOSI)",PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, Wide3V3To5V, "Digital I/O 11 / SPI MOSI / PWM"),
                new(15, "D12 (MISO)", PinFunction.DigitalBidirectional | PinFunction.SpiMiso, Wide3V3To5V, "Digital I/O 12 / SPI MISO"),
                new(16, "D13 (SCK)",  PinFunction.DigitalBidirectional | PinFunction.SpiSck, Wide3V3To5V, "Digital I/O 13 / SPI SCK"),
                new(17, "A0",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Wide3V3To5V, "Analog 0 / Digital 14"),
                new(18, "A1",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Wide3V3To5V, "Analog 1 / Digital 15"),
                new(19, "A2",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Wide3V3To5V, "Analog 2 / Digital 16"),
                new(20, "A3",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional, Wide3V3To5V, "Analog 3 / Digital 17"),
                new(21, "VCC",        PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Wide3V3To5V, "Regulated MCU rail (3.3V on 8MHz version, 5.0V on 16MHz version) - do NOT exceed!"),
                new(22, "RST_2",      PinFunction.DigitalInput | PinFunction.ActiveLowReset, Wide3V3To5V, "Active-LOW Reset"),
                new(23, "GND_2",      PinFunction.Ground,               null,        "Ground 2"),
                new(24, "RAW",        PinFunction.PowerVcc,             rawRange,    "Unregulated voltage input to onboard MIC5205 regulator (up to 12V)")
            ],
            CommonWiringTraps:
            [
                new("PROMINI-TRAP-01", "Confusing RAW (Pin 24) and VCC (Pin 21) With Battery Input", WiringTrapSeverity.CriticalDamageRisk, [21, 24],
                    "Pin 24 (RAW) feeds the onboard regulator, while Pin 21 (VCC) connects directly to the ATmega328P power pin. Connecting a 9V battery to Pin 21 (VCC) instead of Pin 24 (RAW) destroys the MCU immediately.",
                    "CRITICAL: Flag any supply > 5.5V connected to Pin 21 (VCC); route unregulated batteries to Pin 24 (RAW)."),
                new("PROMINI-TRAP-02", "I2C Pins A4 and A5 Are Off-Grid Inner Pads (Not on the 24 Outer Breadboard Pins)", WiringTrapSeverity.LogicFailure, [17, 18, 19, 20],
                    "On the Pro Mini, A4 (SDA) and A5 (SCL) are located on interior off-grid pads above A2/A3 rather than the outer 24 breadboard pins.",
                    "Remind user that I2C SDA/SCL require top-side jumper wires to the inner A4/A5 pads.")
            ]);
    }

    // =========================================================================
    // 18. RPI-PICO - Raspberry Pi Pico (RP2040, DIP-40)
    // =========================================================================
    private static ComponentPinoutProfile BuildRaspberryPiPico()
    {
        var vsysRange = new VoltageRange(1.8, 5.0, 5.5, 5.5, true);
        return new(
            PartNumber: "RPI-PICO",
            Aliases: ["RASPBERRY-PI-PICO", "PICO-W", "RP2040-PICO", "PICO-2"],
            Category: "Microcontroller Devboard - 32-Bit Dual ARM Cortex-M0+ (3.3V)",
            Description: "Raspberry Pi Pico RP2040 Microcontroller Board (40-Pin 0.6\" Wide DIP Footprint)",
            PinCount: 40,
            WidthCategory: DipWidthCategory.Wide600Mil,
            RowSpacingInches: 0.6,
            Pin1Marker: Pin1OrientationMarker.UsbConnectorTopEdge,
            Pin1MarkerDescription: "With Micro-USB connector at top edge: Pin 1 (GP0) is Top-Left; Pin 20 (GP15) is Bottom-Left; Pin 21 (GP16) is Bottom-Right; Pin 40 (VBUS) is Top-Right.",
            SupplyVoltage: vsysRange,
            LogicVoltage: Logic3V3Only,
            Pins:
            [
                new(1,  "GP0",       PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.I2cSda | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "GPIO0 / UART0 TX / I2C0 SDA / SPI0 RX"),
                new(2,  "GP1",       PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.I2cScl | PinFunction.SpiCs | PinFunction.PwmOutput,   Logic3V3Only, "GPIO1 / UART0 RX / I2C0 SCL / SPI0 CSn"),
                new(3,  "GND_1",     PinFunction.Ground, null, "Ground 1"),
                new(4,  "GP2",       PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "GPIO2 / I2C1 SDA / SPI0 SCK"),
                new(5,  "GP3",       PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "GPIO3 / I2C1 SCL / SPI0 TX"),
                new(6,  "GP4",       PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.I2cSda | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "GPIO4 / UART1 TX / I2C0 SDA / SPI0 RX"),
                new(7,  "GP5",       PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.I2cScl | PinFunction.SpiCs | PinFunction.PwmOutput,   Logic3V3Only, "GPIO5 / UART1 RX / I2C0 SCL / SPI0 CSn"),
                new(8,  "GND_2",     PinFunction.Ground, null, "Ground 2"),
                new(9,  "GP6",       PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "GPIO6 / I2C1 SDA / SPI0 SCK"),
                new(10, "GP7",       PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "GPIO7 / I2C1 SCL / SPI0 TX"),
                new(11, "GP8",       PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.I2cSda | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "GPIO8 / UART1 TX / I2C0 SDA / SPI1 RX"),
                new(12, "GP9",       PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.I2cScl | PinFunction.SpiCs | PinFunction.PwmOutput,   Logic3V3Only, "GPIO9 / UART1 RX / I2C0 SCL / SPI1 CSn"),
                new(13, "GND_3",     PinFunction.Ground, null, "Ground 3"),
                new(14, "GP10",      PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "GPIO10 / I2C1 SDA / SPI1 SCK"),
                new(15, "GP11",      PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "GPIO11 / I2C1 SCL / SPI1 TX"),
                new(16, "GP12",      PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.I2cSda | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "GPIO12 / UART0 TX / I2C0 SDA / SPI1 RX"),
                new(17, "GP13",      PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.I2cScl | PinFunction.SpiCs | PinFunction.PwmOutput,   Logic3V3Only, "GPIO13 / UART0 RX / I2C0 SCL / SPI1 CSn"),
                new(18, "GND_4",     PinFunction.Ground, null, "Ground 4"),
                new(19, "GP14",      PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "GPIO14 / I2C1 SDA / SPI1 SCK"),
                new(20, "GP15",      PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "GPIO15 / I2C1 SCL / SPI1 TX"),
                new(21, "GP16",      PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.I2cSda | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "GPIO16 / UART0 TX / I2C0 SDA / SPI0 RX"),
                new(22, "GP17",      PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.I2cScl | PinFunction.SpiCs | PinFunction.PwmOutput,   Logic3V3Only, "GPIO17 / UART0 RX / I2C0 SCL / SPI0 CSn"),
                new(23, "GND_5",     PinFunction.Ground, null, "Ground 5"),
                new(24, "GP18",      PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "GPIO18 / I2C1 SDA / SPI0 SCK"),
                new(25, "GP19",      PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "GPIO19 / I2C1 SCL / SPI0 TX"),
                new(26, "GP20",      PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.PwmOutput, Logic3V3Only, "GPIO20 / I2C0 SDA"),
                new(27, "GP21",      PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.PwmOutput, Logic3V3Only, "GPIO21 / I2C0 SCL"),
                new(28, "GND_6",     PinFunction.Ground, null, "Ground 6"),
                new(29, "GP22",      PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "GPIO22"),
                new(30, "RUN",       PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic3V3Only, "Active-LOW RP2040 Reset (pull to GND to reset; internal pull-up to 3.3V)"),
                new(31, "GP26 (A0)", PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.PwmOutput, Logic3V3Only, "ADC0 / GPIO26 / I2C1 SDA (3.3V MAX!)"),
                new(32, "GP27 (A1)", PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.PwmOutput, Logic3V3Only, "ADC1 / GPIO27 / I2C1 SCL (3.3V MAX!)"),
                new(33, "GND (AGND)",PinFunction.Ground, null, "ADC Analog Ground return reference"),
                new(34, "GP28 (A2)", PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC2 / GPIO28 (3.3V MAX!)"),
                new(35, "ADC_VREF",  PinFunction.AnalogInput | PinFunction.VoltageReference, Logic3V3Only, "ADC Reference Voltage (filtered 3.3V)"),
                new(36, "3V3(OUT)",  PinFunction.RegulatedVoltageOut, Logic3V3Only, "Regulated 3.3V output from onboard RT6150 buck-boost (up to 300mA)"),
                new(37, "3V3_EN",    PinFunction.DigitalInput | PinFunction.ActiveHighEnable, vsysRange, "Pull LOW to GND to disable onboard 3.3V buck-boost regulator"),
                new(38, "GND_8",     PinFunction.Ground, null, "Ground 8"),
                new(39, "VSYS",      PinFunction.PowerVcc, vsysRange, "Main system input voltage (1.8V to 5.5V) feeding onboard 3.3V buck-boost"),
                new(40, "VBUS",      PinFunction.PowerVcc, Logic5VStandard, "5.0V micro-USB bus voltage direct pin")
            ],
            CommonWiringTraps:
            [
                new("PICO-TRAP-01", "Applying 5V Logic Signals to Strictly 3.3V RP2040 GPIOs (Especially ADC Pins GP26-GP28)", WiringTrapSeverity.CriticalDamageRisk, [31, 32, 34],
                    "RP2040 GPIOs are NOT 5V-tolerant. ADC pins GP26-GP28 (Pins 31, 32, 34) have an internal ESD diode directly to IOVDD (3.3V) and will be permanently damaged if driven above 3.6V.",
                    "CRITICAL: Reject any > 3.6V signal connected to any GP0-GP28 pin."),
                new("PICO-TRAP-02", "Shorting 3V3_EN (Pin 37) to GND Expecting It to Be a Ground Pin", WiringTrapSeverity.LogicFailure, [36, 37, 38],
                    "Because Pico ground pins repeat every 5 pins on the left edge (3, 8, 13, 18, 23, 28, 33, 38), users miscount by 1 hole on the top-right and connect GND to Pin 37 (3V3_EN), shutting down the 3.3V regulator.",
                    "Flag if Pin 37 (3V3_EN) is permanently tied to GND without a power switch.")
            ]);
    }

    // =========================================================================
    // 19. ESP32-DEVKIT-V1-30PIN - ESP-WROOM-32 30-Pin Development Board
    // =========================================================================
    private static ComponentPinoutProfile BuildESP32DevKitV1_30Pin() => new(
        PartNumber: "ESP32-DEVKIT-V1-30PIN",
        Aliases: ["ESP32-30PIN", "DOIT-ESP32-DEVKIT-V1", "ESP32-WROOM-30PIN"],
        Category: "Microcontroller Devboard - 32-Bit Dual-Core Wi-Fi/BLE (3.3V)",
        Description: "DOIT ESP32 DevKit V1 (ESP-WROOM-32, 30-Pin 0.9\"-1.0\" Extra-Wide DIP Footprint)",
        PinCount: 30,
        WidthCategory: DipWidthCategory.ExtraWide900Mil,
        RowSpacingInches: 1.0,
        Pin1Marker: Pin1OrientationMarker.UsbConnectorTopEdge,
        Pin1MarkerDescription: "With RF metal shield at top and USB connector at bottom, or standard IC numbering starting Top-Left at EN (Pin 1) down to VIN (Pin 15), and Bottom-Right GND (Pin 16) up to Top-Right D23 (Pin 30).",
        SupplyVoltage: Logic5VStandard,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1,  "EN (RST)",       PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic3V3Only, "Chip Enable / Active-LOW Reset"),
            new(2,  "VP (GPIO36/A0)", PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH0 / GPIO36 (INPUT ONLY - NO internal pull-up/pull-down!)"),
            new(3,  "VN (GPIO39/A3)", PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH3 / GPIO39 (INPUT ONLY - NO internal pull-up/pull-down!)"),
            new(4,  "D34 (GPIO34)",   PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH6 / GPIO34 (INPUT ONLY - NO internal pull-up/pull-down!)"),
            new(5,  "D35 (GPIO35)",   PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH7 / GPIO35 (INPUT ONLY - NO internal pull-up/pull-down!)"),
            new(6,  "D32 (GPIO32)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC1_CH4 / Touch9 / GPIO32"),
            new(7,  "D33 (GPIO33)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC1_CH5 / Touch8 / GPIO33"),
            new(8,  "D25 (GPIO25)",   PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, Logic3V3Only, "DAC1 / ADC2_CH8 / GPIO25"),
            new(9,  "D26 (GPIO26)",   PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, Logic3V3Only, "DAC2 / ADC2_CH9 / GPIO26"),
            new(10, "D27 (GPIO27)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC2_CH7 / Touch7 / GPIO27"),
            new(11, "D14 (GPIO14)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "HSPI_CLK / ADC2_CH6 / GPIO14 (outputs brief PWM pulse at boot)"),
            new(12, "D12 (GPIO12)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "BOOT STRAPPING PIN (MTDI): Boot FAILS if pulled HIGH at reset (sets flash voltage to 1.8V)!"),
            new(13, "D13 (GPIO13)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "HSPI_MOSI / ADC2_CH4 / GPIO13"),
            new(14, "GND_1",          PinFunction.Ground, null, "Ground 1"),
            new(15, "VIN (5V)",       PinFunction.PowerVcc, Logic5VStandard, "5.0V input feeding onboard AMS1117-3.3 regulator"),
            new(16, "3V3",            PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic3V3Only, "Regulated 3.3V rail"),
            new(17, "GND_2",          PinFunction.Ground, null, "Ground 2"),
            new(18, "D15 (GPIO15)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "BOOT STRAPPING PIN (MTDO): silences boot log if pulled LOW"),
            new(19, "D2 (GPIO2)",     PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "BOOT STRAPPING PIN / Onboard Blue LED: must be LOW or floating to enter serial bootloader"),
            new(20, "D4 (GPIO4)",     PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC2_CH0 / Touch0 / GPIO4"),
            new(21, "RX2 (GPIO16)",   PinFunction.DigitalBidirectional | PinFunction.UartRx, Logic3V3Only, "UART2 RX / GPIO16"),
            new(22, "TX2 (GPIO17)",   PinFunction.DigitalBidirectional | PinFunction.UartTx, Logic3V3Only, "UART2 TX / GPIO17"),
            new(23, "D5 (GPIO5)",     PinFunction.DigitalBidirectional | PinFunction.SpiCs | PinFunction.PwmOutput, Logic3V3Only, "VSPI_CS0 / Boot strapping pin / GPIO5"),
            new(24, "D18 (GPIO18)",   PinFunction.DigitalBidirectional | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "VSPI_CLK / GPIO18"),
            new(25, "D19 (GPIO19)",   PinFunction.DigitalBidirectional | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "VSPI_MISO / GPIO19"),
            new(26, "D21 (GPIO21)",   PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.PwmOutput, Logic3V3Only, "Default I2C SDA / GPIO21"),
            new(27, "RX0 (GPIO3)",    PinFunction.DigitalBidirectional | PinFunction.UartRx, Logic3V3Only, "UART0 RX (connected to USB-UART bridge)"),
            new(28, "TX0 (GPIO1)",    PinFunction.DigitalBidirectional | PinFunction.UartTx, Logic3V3Only, "UART0 TX (connected to USB-UART bridge)"),
            new(29, "D22 (GPIO22)",   PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.PwmOutput, Logic3V3Only, "Default I2C SCL / GPIO22"),
            new(30, "D23 (GPIO23)",   PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "VSPI_MOSI / GPIO23")
        ],
        CommonWiringTraps:
        [
            new("ESP32-30-TRAP-01", "Using Input-Only Pins GPIO34, GPIO35, GPIO36 (VP), or GPIO39 (VN) as Outputs or Expecting Internal Pull-Ups", WiringTrapSeverity.LogicFailure, [2, 3, 4, 5],
                "GPIOs 34, 35, 36, and 39 are strictly input-only on the ESP32 silicon and lack internal pull-up or pull-down resistors. Driving LEDs, relays, I2C SDA/SCL, or switches without external pull-ups on Pins 2-5 always fails.",
                "Flag any output load or switch without an external pull resistor on Pins 2, 3, 4, or 5."),
            new("ESP32-30-TRAP-02", "Pulling Boot Strapping Pin GPIO12 (Pin 12) HIGH at Reset", WiringTrapSeverity.LogicFailure, [12],
                "GPIO12 (MTDI) selects internal SPI flash voltage at boot (LOW = 3.3V flash, HIGH = 1.8V flash). External pull-ups on GPIO12 cause a brownout/flash-read boot loop ('flash read err, 1000').",
                "Flag any pull-up resistor or active-HIGH sensor idling HIGH on Pin 12 (GPIO12)."),
            new("ESP32-30-TRAP-03", "Using ADC2 Pins (GPIO 2, 4, 12-15, 25-27) for Analog Reads While Wi-Fi Is Active", WiringTrapSeverity.Warning, [8, 9, 10, 11, 12, 13, 18, 19, 20],
                "The ESP32 Wi-Fi radio hardware locks the ADC2 peripheral whenever Wi-Fi is enabled. Analog sensors must be wired to ADC1 pins (GPIO 32-39, Pins 2-7) if Wi-Fi is used.",
                "Recommend moving analog sensors from ADC2 pins to ADC1 (Pins 2-7).")
        ]);

    // =========================================================================
    // 20. ESP32-DEVKITC-38PIN - ESP-WROOM-32 38-Pin Development Board
    // =========================================================================
    private static ComponentPinoutProfile BuildESP32DevKitC_38Pin() => new(
        PartNumber: "ESP32-DEVKITC-38PIN",
        Aliases: ["ESP32-38PIN", "ESP32-DEVKITC-V4", "NODEMCU-32S-38PIN"],
        Category: "Microcontroller Devboard - 32-Bit Dual-Core Wi-Fi/BLE (3.3V)",
        Description: "Espressif ESP32-DevKitC / NodeMCU-32S 38-Pin Development Board (Exposes Internal SPI Flash Pins 6-11)",
        PinCount: 38,
        WidthCategory: DipWidthCategory.ExtraWide900Mil,
        RowSpacingInches: 1.0,
        Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
        Pin1MarkerDescription: "38-pin dual-row layout (19 pins per side): Top-Left Pin 1 = 3V3, Bottom-Left Pin 19 = 5V/EXT_5V, Bottom-Right Pin 20 = CLK(GPIO6), Top-Right Pin 38 = GND.",
        SupplyVoltage: Logic5VStandard,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1,  "3V3",            PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic3V3Only, "Regulated 3.3V supply rail"),
            new(2,  "EN (RST)",       PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic3V3Only, "Chip Enable / Reset"),
            new(3,  "VP (GPIO36)",    PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH0 / GPIO36 (INPUT ONLY, no internal pull-up)"),
            new(4,  "VN (GPIO39)",    PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH3 / GPIO39 (INPUT ONLY, no internal pull-up)"),
            new(5,  "IO34 (GPIO34)",  PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH6 / GPIO34 (INPUT ONLY, no internal pull-up)"),
            new(6,  "IO35 (GPIO35)",  PinFunction.AnalogInput | PinFunction.DigitalInput, Logic3V3Only, "ADC1_CH7 / GPIO35 (INPUT ONLY, no internal pull-up)"),
            new(7,  "IO32 (GPIO32)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC1_CH4 / GPIO32"),
            new(8,  "IO33 (GPIO33)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC1_CH5 / GPIO33"),
            new(9,  "IO25 (GPIO25)",  PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, Logic3V3Only, "DAC1 / GPIO25"),
            new(10, "IO26 (GPIO26)",  PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, Logic3V3Only, "DAC2 / GPIO26"),
            new(11, "IO27 (GPIO27)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC2_CH7 / GPIO27"),
            new(12, "IO14 (GPIO14)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "HSPI_CLK / GPIO14"),
            new(13, "IO12 (GPIO12)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "BOOT STRAPPING PIN (MTDI): Must NOT be pulled HIGH at boot!"),
            new(14, "GND_1",          PinFunction.Ground, null, "Ground 1"),
            new(15, "IO13 (GPIO13)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "HSPI_MOSI / GPIO13"),
            new(16, "SD2 (GPIO9)",    PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(17, "SD3 (GPIO10)",   PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(18, "CMD (GPIO11)",   PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(19, "5V (VIN)",       PinFunction.PowerVcc, Logic5VStandard, "5.0V power input to onboard 3.3V LDO"),
            new(20, "CLK (GPIO6)",    PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(21, "SD0 (GPIO7)",    PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(22, "SD1 (GPIO8)",    PinFunction.NoConnect, Logic3V3Only, "FORBIDDEN: Connected to internal WROOM-32 SPI Flash! Do NOT wire on breadboard!"),
            new(23, "IO15 (GPIO15)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "Boot strapping pin (MTDO) / GPIO15"),
            new(24, "IO2 (GPIO2)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "Boot strapping pin / GPIO2"),
            new(25, "IO0 (GPIO0)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "BOOT STRAPPING PIN: Hold LOW at reset for Firmware Download mode, HIGH for SPI Flash boot"),
            new(26, "IO4 (GPIO4)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC2_CH0 / GPIO4"),
            new(27, "IO16 (GPIO16)",  PinFunction.DigitalBidirectional | PinFunction.UartRx, Logic3V3Only, "UART2 RX / GPIO16"),
            new(28, "IO17 (GPIO17)",  PinFunction.DigitalBidirectional | PinFunction.UartTx, Logic3V3Only, "UART2 TX / GPIO17"),
            new(29, "IO5 (GPIO5)",    PinFunction.DigitalBidirectional | PinFunction.SpiCs | PinFunction.PwmOutput, Logic3V3Only, "VSPI_CS0 / GPIO5"),
            new(30, "IO18 (GPIO18)",  PinFunction.DigitalBidirectional | PinFunction.SpiSck | PinFunction.PwmOutput, Logic3V3Only, "VSPI_CLK / GPIO18"),
            new(31, "IO19 (GPIO19)",  PinFunction.DigitalBidirectional | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "VSPI_MISO / GPIO19"),
            new(32, "GND_2",          PinFunction.Ground, null, "Ground 2"),
            new(33, "IO21 (GPIO21)",  PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.PwmOutput, Logic3V3Only, "Default I2C SDA / GPIO21"),
            new(34, "RXD0 (GPIO3)",   PinFunction.DigitalBidirectional | PinFunction.UartRx, Logic3V3Only, "UART0 RX / GPIO3"),
            new(35, "TXD0 (GPIO1)",   PinFunction.DigitalBidirectional | PinFunction.UartTx, Logic3V3Only, "UART0 TX / GPIO1"),
            new(36, "IO22 (GPIO22)",  PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.PwmOutput, Logic3V3Only, "Default I2C SCL / GPIO22"),
            new(37, "IO23 (GPIO23)",  PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "VSPI_MOSI / GPIO23"),
            new(38, "GND_3",          PinFunction.Ground, null, "Ground 3")
        ],
        CommonWiringTraps:
        [
            new("ESP32-38-TRAP-01", "Connecting Any External Wire to Internal SPI Flash Pins GPIO 6-11 (Pins 16, 17, 18, 20, 21, 22)", WiringTrapSeverity.CriticalDamageRisk, [16, 17, 18, 20, 21, 22],
                "The 38-pin ESP32 board breaks out GPIOs 6, 7, 8, 9, 10, and 11 (labeled CLK, SD0, SD1, SD2, SD3, CMD) at the bottom corners. These 6 pins are wired directly to the ESP-WROOM-32 module's internal SPI NOR Flash! Connecting anything to Pins 16-18 or 20-22 crashes the CPU with Guru Meditation / IllegalInstruction faults.",
                "CRITICAL: Forbid any external breadboard connection to Pins 16, 17, 18, 20, 21, or 22.")
        ]);

    // =========================================================================
    // 21. STM32-BLUEPILL - STM32F103C8T6 Minimum System Board (DIP-40)
    // =========================================================================
    private static ComponentPinoutProfile BuildSTM32BluePill() => new(
        PartNumber: "STM32-BLUEPILL",
        Aliases: ["STM32F103C8T6", "BLUEPILL", "STM32F103"],
        Category: "Microcontroller Devboard - 32-Bit ARM Cortex-M3 (3.3V)",
        Description: "STM32F103C8T6 72MHz ARM Cortex-M3 'BluePill' Minimum System Board (40-Pin 0.6\" Wide DIP Footprint)",
        PinCount: 40,
        WidthCategory: DipWidthCategory.Wide600Mil,
        RowSpacingInches: 0.6,
        Pin1Marker: Pin1OrientationMarker.UsbConnectorTopEdge,
        Pin1MarkerDescription: "With Micro-USB connector at top edge and 4-pin SWD header at bottom: Pin 1 (VBAT) is Top-Left; Pin 20 (PB11) is Bottom-Left; Pin 21 (PB12) is Bottom-Right; Pin 40 (3.3V) is Top-Right.",
        SupplyVoltage: Logic5VStandard,
        LogicVoltage: Logic3V3Only,
        Pins:
        [
            new(1,  "VBAT",        PinFunction.PowerVcc, new(1.8, 3.0, 3.6, 3.6, false), "RTC backup battery input (1.8V-3.6V ONLY; do NOT connect 5V)"),
            new(2,  "PC13",        PinFunction.DigitalBidirectional, Logic3V3Only, "Onboard LED (active-LOW) / Tamper-RTC (3.3V ONLY, max 3mA source/sink!)"),
            new(3,  "PC14",        PinFunction.DigitalBidirectional | PinFunction.ClockInput, Logic3V3Only, "OSC32_IN (3.3V ONLY, max 3mA)"),
            new(4,  "PC15",        PinFunction.DigitalBidirectional | PinFunction.ClockInput, Logic3V3Only, "OSC32_OUT (3.3V ONLY, max 3mA)"),
            new(5,  "PA0 (WKUP)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC_IN0 / TIM2_CH1 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(6,  "PA1",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC_IN1 / TIM2_CH2 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(7,  "PA2 (TX2)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.PwmOutput, Logic3V3Only, "USART2_TX / ADC_IN2 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(8,  "PA3 (RX2)",   PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.PwmOutput, Logic3V3Only, "USART2_RX / ADC_IN3 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(9,  "PA4 (NSS1)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.SpiCs, Logic3V3Only, "SPI1_NSS / ADC_IN4 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(10, "PA5 (SCK1)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.SpiSck, Logic3V3Only, "SPI1_SCK / ADC_IN5 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(11, "PA6 (MISO1)", PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.SpiMiso | PinFunction.PwmOutput, Logic3V3Only, "SPI1_MISO / ADC_IN6 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(12, "PA7 (MOSI1)", PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, Logic3V3Only, "SPI1_MOSI / ADC_IN7 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(13, "PB0",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC_IN8 / TIM3_CH3 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(14, "PB1",         PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3Only, "ADC_IN9 / TIM3_CH4 (NOT 5V-tolerant: 3.3V MAX!)"),
            new(15, "PB10 (SCL2)", PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.UartTx, Logic3V3FiveVoltTolerant, "I2C2_SCL / USART3_TX (5V-tolerant FT digital pin)"),
            new(16, "PB11 (SDA2)", PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.UartRx, Logic3V3FiveVoltTolerant, "I2C2_SDA / USART3_RX (5V-tolerant FT digital pin)"),
            new(17, "NRST",        PinFunction.DigitalInput | PinFunction.ActiveLowReset, Logic3V3Only, "Active-LOW MCU Reset"),
            new(18, "3.3V_1",      PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic3V3Only, "3.3V Regulated Rail 1"),
            new(19, "GND_1",       PinFunction.Ground, null, "Ground 1"),
            new(20, "GND_2",       PinFunction.Ground, null, "Ground 2"),
            new(21, "PB12 (NSS2)", PinFunction.DigitalBidirectional | PinFunction.SpiCs, Logic3V3FiveVoltTolerant, "SPI2_NSS (5V-tolerant FT pin)"),
            new(22, "PB13 (SCK2)", PinFunction.DigitalBidirectional | PinFunction.SpiSck, Logic3V3FiveVoltTolerant, "SPI2_SCK (5V-tolerant FT pin)"),
            new(23, "PB14 (MISO2)",PinFunction.DigitalBidirectional | PinFunction.SpiMiso, Logic3V3FiveVoltTolerant, "SPI2_MISO (5V-tolerant FT pin)"),
            new(24, "PB15 (MOSI2)",PinFunction.DigitalBidirectional | PinFunction.SpiMosi, Logic3V3FiveVoltTolerant, "SPI2_MOSI (5V-tolerant FT pin)"),
            new(25, "PA8",         PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "TIM1_CH1 / MCO (5V-tolerant FT pin)"),
            new(26, "PA9 (TX1)",   PinFunction.DigitalBidirectional | PinFunction.UartTx | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "USART1_TX / Bootloader UART TX (5V-tolerant FT pin)"),
            new(27, "PA10 (RX1)",  PinFunction.DigitalBidirectional | PinFunction.UartRx | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "USART1_RX / Bootloader UART RX (5V-tolerant FT pin)"),
            new(28, "PA11 (USB-)", PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "USB D- / CAN_RX (5V-tolerant FT pin)"),
            new(29, "PA12 (USB+)", PinFunction.DigitalBidirectional | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "USB D+ / CAN_TX (5V-tolerant FT pin)"),
            new(30, "PA15",        PinFunction.DigitalBidirectional | PinFunction.SpiCs, Logic3V3FiveVoltTolerant, "JTDI / SPI1_NSS remap (5V-tolerant FT pin; defaults to JTAG at reset)"),
            new(31, "PB3",         PinFunction.DigitalBidirectional | PinFunction.SpiSck, Logic3V3FiveVoltTolerant, "JTDO / SPI1_SCK remap (5V-tolerant FT pin; defaults to JTAG at reset)"),
            new(32, "PB4",         PinFunction.DigitalBidirectional | PinFunction.SpiMiso, Logic3V3FiveVoltTolerant, "NJTRST / SPI1_MISO remap (5V-tolerant FT pin; defaults to JTAG at reset)"),
            new(33, "PB5",         PinFunction.DigitalBidirectional | PinFunction.SpiMosi, Logic3V3Only, "SPI1_MOSI remap / I2C1_SMBA (NOT 5V-tolerant: 3.3V MAX!)"),
            new(34, "PB6 (SCL1)",  PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.UartTx | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "I2C1_SCL / TIM4_CH1 (5V-tolerant FT pin)"),
            new(35, "PB7 (SDA1)",  PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.UartRx | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "I2C1_SDA / TIM4_CH2 (5V-tolerant FT pin)"),
            new(36, "PB8",         PinFunction.DigitalBidirectional | PinFunction.I2cScl | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "TIM4_CH3 / I2C1_SCL remap (5V-tolerant FT pin)"),
            new(37, "PB9",         PinFunction.DigitalBidirectional | PinFunction.I2cSda | PinFunction.PwmOutput, Logic3V3FiveVoltTolerant, "TIM4_CH4 / I2C1_SDA remap (5V-tolerant FT pin)"),
            new(38, "5V",          PinFunction.PowerVcc, Logic5VStandard, "5.0V USB / External Supply Pin"),
            new(39, "GND_3",       PinFunction.Ground, null, "Ground 3"),
            new(40, "3.3V_2",      PinFunction.PowerVcc | PinFunction.RegulatedVoltageOut, Logic3V3Only, "3.3V Regulated Rail 2")
        ],
        CommonWiringTraps:
        [
            new("BLUEPILL-TRAP-01", "Applying 5V Signals to Non-FT Analog Capable Pins (PA0-PA7, PB0-PB1, PB5, PC13-PC15)", WiringTrapSeverity.CriticalDamageRisk, [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 33],
                "Only specific STM32F103 pins marked 'FT' (Five-Volt Tolerant: PA8-PA15, PB2-PB4, PB6-PB15) can tolerate 5V inputs. All ADC-capable pins (PA0-PA7, PB0-PB1), PB5, and PC13-PC15 are strictly 3.3V max!",
                "CRITICAL: Flag any 5V logic output wired to Pins 1-14 or Pin 33."),
            new("BLUEPILL-TRAP-02", "Sourcing/Sinking > 3mA on Backup Domain Pins PC13, PC14, PC15 (Pins 2-4)", WiringTrapSeverity.CriticalDamageRisk, [2, 3, 4],
                "STM32 datasheet strictly limits PC13, PC14, and PC15 to 3mA maximum current and forbids driving external LEDs or relays from them because they pass through the low-power RTC backup power switch.",
                "Flag any high-current load (e.g., external LED with < 1k resistor, transistor base, or relay) on Pins 2, 3, or 4."),
            new("BLUEPILL-TRAP-03", "Using JTAG Pins PA15, PB3, PB4 (Pins 30-32) as Normal GPIOs Without Disabling JTAG in AFIO", WiringTrapSeverity.LogicFailure, [30, 31, 32],
                "At reset, PA15 (JTDI), PB3 (JTDO), and PB4 (NJTRST) are claimed by the full JTAG debug port and ignore standard GPIO writes until __HAL_AFIO_REMAP_SWJ_NOJTAG() is called.",
                "Info warning when general-purpose I/O connections are detected on Pins 30, 31, or 32.")
        ]);
}
