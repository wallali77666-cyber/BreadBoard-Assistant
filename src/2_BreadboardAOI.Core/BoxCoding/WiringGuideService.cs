// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/WiringGuideService.cs
// PURPOSE: Offline knowledge base and deterministic wiring generation engine.
//          Produces step-by-step pin connections, electrical safety warnings,
//          level shifting rules, and verification firmware for DevBoard + Sensor pairs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

/// <summary>Generates comprehensive hardware wiring guides and interface documentation.</summary>
public static class WiringGuideService
{
    private static readonly Dictionary<string, SensorInterfaceSpec> SensorCatalog = new(StringComparer.OrdinalIgnoreCase);

    static WiringGuideService()
    {
        RegisterAllSensors();
    }

    /// <summary>Returns all supported sensor and peripheral interface definitions.</summary>
    public static IReadOnlyList<SensorInterfaceSpec> GetAvailableSensors() => SensorCatalog.Values.ToList();

    /// <summary>Finds a sensor specification by ID or common alias.</summary>
    public static SensorInterfaceSpec? GetSensorById(string? sensorId)
    {
        if (string.IsNullOrWhiteSpace(sensorId))
            return SensorCatalog.Values.FirstOrDefault();

        string key = sensorId.Trim();
        if (SensorCatalog.TryGetValue(key, out var spec))
            return spec;

        // Partial match
        return SensorCatalog.Values.FirstOrDefault(s =>
            s.SensorId.Contains(key, StringComparison.OrdinalIgnoreCase) ||
            s.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Generates complete tailored wiring guide documentation for a given sensor and dev board.</summary>
    public static WiringGuideDocument GenerateGuide(string sensorId, MicrocontrollerBoardType boardType)
    {
        var sensor = GetSensorById(sensorId) ?? SensorCatalog["HC-SR04"];
        string boardName = FormatBoardName(boardType);
        bool is3V3Board = boardType is MicrocontrollerBoardType.Esp32DevKitV1
            or MicrocontrollerBoardType.WemosEsp32S2Mini
            or MicrocontrollerBoardType.Stm32BluePillF103C8;

        var steps = new List<PinConnectionStep>();
        bool levelShiftRequired = false;
        string? levelShiftAdvice = null;

        switch (sensor.SensorId)
        {
            case "HC-SR04":
                string trigPin = is3V3Board ? (boardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 5" : "GPIO 4") : "Pin D9";
                string echoPin = is3V3Board ? (boardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 18" : "GPIO 5") : "Pin D10";
                string vccRail = "5V Power Rail";

                steps.Add(new PinConnectionStep(1, "VCC", vccRail, "RED", "#E03E3E", "Connect to 5V regulated output to power the ultrasonic transducers."));
                steps.Add(new PinConnectionStep(2, "TRIG", trigPin, "YELLOW", "#E8C52B", "Digital output from MCU: sends a 10µs pulse to start sound burst."));

                if (is3V3Board)
                {
                    levelShiftRequired = true;
                    levelShiftAdvice = "CRITICAL VOLTAGE PROTECTION: HC-SR04 ECHO pin outputs 5.0V pulses, but your 3.3V board GPIOs cannot tolerate >3.6V! Insert a 1kΩ / 2kΩ voltage divider between ECHO and the GPIO pin, or use an HC-SR04P / RCWL-9610 (native 3.3V) sensor.";
                    steps.Add(new PinConnectionStep(3, "ECHO", $"{echoPin} (via 1kΩ/2kΩ divider)", "PURPLE", "#7A3E8E", "ECHO pulse width represents time of flight.", "Voltage divider required: 1kΩ series resistor to GPIO, 2kΩ from GPIO to GND!"));
                }
                else
                {
                    steps.Add(new PinConnectionStep(3, "ECHO", echoPin, "BLUE", "#2B78E4", "Measures the duration of the reflected ultrasound echo."));
                }
                steps.Add(new PinConnectionStep(4, "GND", "GND Rail", "BLACK", "#222222", "Common 0V ground reference return."));
                break;

            case "DHT11":
            case "DHT22":
                string dhtVcc = is3V3Board ? "3.3V Rail" : "5V Rail";
                string dhtData = is3V3Board ? "GPIO 4" : "Pin D2";

                steps.Add(new PinConnectionStep(1, "VCC", dhtVcc, "RED", "#E03E3E", $"Supplies {dhtVcc} to power internal thermistor, capacitive humidity polymer, and 8-bit MCU."));
                steps.Add(new PinConnectionStep(2, "DATA", dhtData, "YELLOW", "#E8C52B", "Single-wire bidirectional bus transmitting 40-bit data packets.", "Requires 4.7kΩ–10kΩ pull-up resistor between DATA and VCC!"));
                steps.Add(new PinConnectionStep(3, "NC", "Do Not Connect", "GRAY", "#9E9E9E", "Pin 3 is unconnected on discrete 4-pin DHT sensors (omit on 3-pin breakout modules)."));
                steps.Add(new PinConnectionStep(4, "GND", "GND Rail", "BLACK", "#222222", "System 0V ground return."));
                break;

            case "LCD1602_I2C":
            case "LCD1602":
                string lcdVcc = "5V Power Rail";
                string lcdSda = GetI2cSdaPin(boardType);
                string lcdScl = GetI2cSclPin(boardType);

                steps.Add(new PinConnectionStep(1, "GND", "GND Rail", "BLACK", "#222222", "Common 0V system ground return."));
                steps.Add(new PinConnectionStep(2, "VCC", lcdVcc, "RED", "#E03E3E", "5.0V regulated power supply (essential for LCD contrast potential).", "Do not power from 3.3V: liquid crystal characters will remain completely invisible."));

                if (is3V3Board)
                {
                    levelShiftRequired = true;
                    levelShiftAdvice = "3.3V LEVEL SHIFTER ADVICE: The PCF8574 backpack pulls SDA and SCL to 5.0V with 4.7kΩ pull-up resistors. Use a bidirectional 3.3V <-> 5V I2C logic level shifter between the MCU and the LCD backpack to protect the 3.3V MCU GPIOs.";
                    steps.Add(new PinConnectionStep(3, "SDA", $"{lcdSda} (via 3.3V/5V Level Shifter)", "BLUE", "#2B78E4", "I2C Serial Data Line (PCF8574 backpack).", "Level shifter recommended to protect 3.3V GPIO."));
                    steps.Add(new PinConnectionStep(4, "SCL", $"{lcdScl} (via 3.3V/5V Level Shifter)", "ORANGE", "#E0741E", "I2C Serial Clock Line (PCF8574 backpack).", "Level shifter recommended to protect 3.3V GPIO."));
                }
                else
                {
                    steps.Add(new PinConnectionStep(3, "SDA", lcdSda, "BLUE", "#2B78E4", $"I2C Serial Data Line connected to hardware SDA ({lcdSda}). Default I2C Address: 0x27 or 0x3F."));
                    steps.Add(new PinConnectionStep(4, "SCL", lcdScl, "ORANGE", "#E0741E", $"I2C Serial Clock Line connected to hardware SCL ({lcdScl})."));
                }
                break;

            case "OLED_SSD1306":
                string oledVcc = is3V3Board ? "3.3V Rail" : "5V Rail (or 3.3V)";
                string sdaPin = GetI2cSdaPin(boardType);
                string sclPin = GetI2cSclPin(boardType);

                steps.Add(new PinConnectionStep(1, "GND", "GND Rail", "BLACK", "#222222", "Ground reference for display driver and charge pump."));
                steps.Add(new PinConnectionStep(2, "VCC", oledVcc, "RED", "#E03E3E", $"Operating supply ({oledVcc}). Most breakout boards feature an on-board 3.3V LDO regulator."));
                steps.Add(new PinConnectionStep(3, "SCL", sclPin, "ORANGE", "#E0741E", $"I2C Clock Line connected to hardware clock ({sclPin})."));
                steps.Add(new PinConnectionStep(4, "SDA", sdaPin, "BLUE", "#2B78E4", $"I2C Serial Data Line connected to hardware data ({sdaPin}). Default I2C Address: 0x3C."));
                break;

            case "MPU6050":
                string mpuVcc = "3.3V Rail";
                string mpuSda = GetI2cSdaPin(boardType);
                string mpuScl = GetI2cSclPin(boardType);

                steps.Add(new PinConnectionStep(1, "VCC", mpuVcc, "RED", "#E03E3E", "Power input (+3.3V DC). Note: Most breakout boards include an on-board LDO regulator."));
                steps.Add(new PinConnectionStep(2, "GND", "GND Rail", "BLACK", "#222222", "Common ground reference."));
                steps.Add(new PinConnectionStep(3, "SCL", mpuScl, "ORANGE", "#E0741E", $"I2C Clock connected to hardware SCL ({mpuScl})."));
                steps.Add(new PinConnectionStep(4, "SDA", mpuSda, "BLUE", "#2B78E4", $"I2C Data connected to hardware SDA ({mpuSda}). Default Address: 0x68."));
                steps.Add(new PinConnectionStep(5, "AD0", "GND (for 0x68) or 3.3V (for 0x69)", "GREEN", "#2E8B46", "I2C Address Select pin. Tie to GND for 0x68; tie to 3.3V for 0x69."));
                steps.Add(new PinConnectionStep(6, "INT", is3V3Board ? "GPIO 19 (Optional)" : "Pin D2 (Optional)", "WHITE", "#E0E0E0", "Interrupt trigger pin (optional motion wake-up interrupt)."));
                break;

            case "MQ-2":
                string mqVcc = "5V Power Rail";
                string mqAo = is3V3Board ? "GPIO 34 (ADC1)" : "Pin A0";
                string mqDo = is3V3Board ? "GPIO 15" : "Pin D7";

                if (is3V3Board)
                {
                    levelShiftRequired = true;
                    levelShiftAdvice = "MQ-2 heater strictly requires 5.0V (~150mA). While the digital DO pin is safe if powered with 3.3V on dual-rail boards, 5V analog AO must be scaled with a voltage divider or read on a 5V-tolerant channel.";
                }

                steps.Add(new PinConnectionStep(1, "VCC", mqVcc, "RED", "#E03E3E", "Heater and sensing circuit power (+5V DC @ 150mA required for SnO2 tin-dioxide sensor)."));
                steps.Add(new PinConnectionStep(2, "GND", "GND Rail", "BLACK", "#222222", "Common 0V ground return for heater and amplifier."));
                steps.Add(new PinConnectionStep(3, "DO", mqDo, "PURPLE", "#7A3E8E", "Digital Output: transitions LOW/HIGH when gas concentration exceeds potentiometer threshold."));
                steps.Add(new PinConnectionStep(4, "AO", mqAo, "BLUE", "#2B78E4", "Analog Output: continuous 0-5V voltage proportional to gas concentration."));
                break;

            case "PIR":
                string pirVcc = is3V3Board ? "5V Rail (Internal 3.3V regulator)" : "5V Rail";
                string pirOut = is3V3Board ? "GPIO 13" : "Pin D4";

                steps.Add(new PinConnectionStep(1, "VCC", pirVcc, "RED", "#E03E3E", "Power supply (+5V to +12V DC). The module regulates this internally to 3.3V via on-board 7133 regulator."));
                steps.Add(new PinConnectionStep(2, "OUT", pirOut, "YELLOW", "#E8C52B", "Digital Output (native 3.3V logic HIGH when infrared motion is detected; 0V when clear). Safe for both 5V and 3.3V MCUs!"));
                steps.Add(new PinConnectionStep(3, "GND", "GND Rail", "BLACK", "#222222", "Common 0V ground return."));
                break;

            case "LDR":
                string ldrVcc = is3V3Board ? "3.3V Rail" : "5V Rail";
                string ldrAnalog = is3V3Board ? "GPIO 36 (ADC1_CH0)" : "Pin A0";

                steps.Add(new PinConnectionStep(1, "Lead 1", ldrVcc, "RED", "#E03E3E", $"Connect one lead of LDR photoresistor to {ldrVcc}."));
                steps.Add(new PinConnectionStep(2, "Lead 2 (Divider Node)", ldrAnalog, "BLUE", "#2B78E4", $"Connect second lead of LDR to {ldrAnalog}, and attach a 10kΩ pull-down resistor to GND."));
                steps.Add(new PinConnectionStep(3, "10kΩ Resistor", "GND Rail", "BLACK", "#222222", "Completes the voltage divider circuit with the LDR to convert varying resistance to voltage."));
                break;

            case "SERVO_SG90":
                string servoVcc = "5V Power Rail";
                string servoSig = is3V3Board ? "GPIO 19 (PWM)" : "Pin D9 (PWM)";

                steps.Add(new PinConnectionStep(1, "GND (Brown)", "GND Rail", "BLACK", "#222222", "Motor and internal H-bridge 0V ground reference."));
                steps.Add(new PinConnectionStep(2, "VCC (Red)", servoVcc, "RED", "#E03E3E", "Motor power supply (+4.8V to +6.0V DC). Recommend external 5V supply if driving multiple servos!"));
                steps.Add(new PinConnectionStep(3, "SIG (Orange/Yellow)", servoSig, "ORANGE", "#E0741E", $"PWM Control pulse (50Hz / 20ms period, 1ms=0°, 1.5ms=90°, 2ms=180°) connected to {servoSig}."));
                break;

            case "RELAY_5V":
                string relayVcc = "5V Power Rail";
                string relayIn = is3V3Board ? "GPIO 12" : "Pin D7";

                steps.Add(new PinConnectionStep(1, "VCC", relayVcc, "RED", "#E03E3E", "Relay coil supply (+5V DC). Optocoupler isolates MCU from inductive coil kickback."));
                steps.Add(new PinConnectionStep(2, "IN", relayIn, "YELLOW", "#E8C52B", $"Logic trigger input connected to {relayIn} (Active LOW on most standard relay breakout boards)."));
                steps.Add(new PinConnectionStep(3, "GND", "GND Rail", "BLACK", "#222222", "Common 0V ground return."));
                break;

            default:
                // Generic 3-pin sensor (VCC, GND, OUT)
                string genVcc = is3V3Board ? "3.3V Rail" : "5V Rail";
                string genOut = is3V3Board ? "GPIO 4" : "Pin A0 / D2";
                steps.Add(new PinConnectionStep(1, "VCC", genVcc, "RED", "#E03E3E", $"Power positive rail ({genVcc})."));
                steps.Add(new PinConnectionStep(2, "OUT / SIG", genOut, "BLUE", "#2B78E4", $"Sensor signal line connected to MCU ({genOut})."));
                steps.Add(new PinConnectionStep(3, "GND", "GND Rail", "BLACK", "#222222", "Common ground 0V reference."));
                break;
        }

        string markdown = BuildMarkdownDocument(sensor, boardName, is3V3Board, levelShiftRequired, levelShiftAdvice, steps);

        return new WiringGuideDocument(
            Sensor: sensor,
            BoardType: boardType,
            BoardName: boardName,
            IsLevelShiftingRequired: levelShiftRequired,
            LevelShiftingAdvice: levelShiftAdvice,
            Steps: steps,
            FormattedMarkdown: markdown);
    }

    /// <summary>Inspects breadboard draft components and returns the primary detected sensor specification.</summary>
    public static SensorInterfaceSpec? AutoDetectSensorFromParts(IEnumerable<DraftPart> parts)
    {
        var partList = parts.ToList();
        foreach (var p in partList)
        {
            if (p.Kind == ComponentKind.Sensor || p.Kind == ComponentKind.IntegratedCircuit)
            {
                string val = (p.Value ?? p.SymbolId ?? "").ToUpperInvariant();
                if (val.Contains("LCD") || val.Contains("1602") || val.Contains("HD44780"))
                    return SensorCatalog["LCD1602_I2C"];
                if (val.Contains("HC") || val.Contains("SR04") || val.Contains("ULTRA"))
                    return SensorCatalog["HC-SR04"];
                if (val.Contains("DHT22") || val.Contains("AM2302"))
                    return SensorCatalog["DHT22"];
                if (val.Contains("DHT11"))
                    return SensorCatalog["DHT11"];
                if (val.Contains("MPU") || val.Contains("6050") || val.Contains("GYRO"))
                    return SensorCatalog["MPU6050"];
                if (val.Contains("OLED") || val.Contains("1306"))
                    return SensorCatalog["OLED_SSD1306"];
                if (val.Contains("MQ2") || val.Contains("GAS") || val.Contains("SMOKE"))
                    return SensorCatalog["MQ-2"];
                if (val.Contains("PIR") || val.Contains("SR501") || val.Contains("MOTION"))
                    return SensorCatalog["PIR"];
                if (val.Contains("TMP") || val.Contains("LM35"))
                    return SensorCatalog["TMP36"];
            }
            if (p.Kind == ComponentKind.Potentiometer)
                return SensorCatalog["POTENTIOMETER"];
            if (p.Kind == ComponentKind.Buzzer)
                return SensorCatalog["SERVO_SG90"];
        }

        return SensorCatalog["HC-SR04"];
    }

    private static string BuildMarkdownDocument(
        SensorInterfaceSpec sensor,
        string boardName,
        bool is3V3Board,
        bool levelShiftRequired,
        string? levelShiftAdvice,
        List<PinConnectionStep> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {sensor.DisplayName} + {boardName}");
        sb.AppendLine($"**Category**: `{sensor.Category}`  |  **Operating Supply**: `{sensor.OperatingVoltage}`  |  **Protocol**: `{sensor.Protocol}`");
        sb.AppendLine();

        sb.AppendLine("## 1. What It Does & How It Works");
        sb.AppendLine(sensor.Summary);
        sb.AppendLine();
        sb.AppendLine($"**Working Principle**:");
        sb.AppendLine(sensor.WorkingPrinciple);
        sb.AppendLine();
        sb.AppendLine($"- **Typical Current Draw**: ~{sensor.TypicalCurrentMa} mA");
        sb.AppendLine($"- **Pinout Structure**: {sensor.PinoutSummary}");
        sb.AppendLine();

        if (levelShiftRequired && !string.IsNullOrWhiteSpace(levelShiftAdvice))
        {
            sb.AppendLine("> [!WARNING]");
            sb.AppendLine($"> **LEVEL SHIFTING & SAFETY ALERT**:");
            sb.AppendLine($"> {levelShiftAdvice}");
            sb.AppendLine();
        }

        sb.AppendLine("## 2. Step-by-Step Connection & Jumper Wire Map");
        sb.AppendLine("| Pin # | Sensor Pin | Direction | Target Dev Board Pin | Jumper Color | Connection Purpose & Notes |");
        sb.AppendLine("|:-----:|:----------:|:---------:|:--------------------:|:------------:|:---------------------------|");

        foreach (var s in steps)
        {
            string warn = s.Warning != null ? $" **⚠️ {s.Warning}**" : "";
            sb.AppendLine($"| {s.SensorPinNumber} | **{s.SensorPinName}** | \u2192 | `{s.TargetBoardPin}` | `{s.WireColor}` | {s.Explanation}{warn} |");
        }
        sb.AppendLine();

        if (sensor.RequiredExternalParts.Count > 0)
        {
            sb.AppendLine("## 3. Required External Components");
            foreach (var part in sensor.RequiredExternalParts)
                sb.AppendLine($"- {part}");
            sb.AppendLine();
        }

        if (sensor.SafetyRules.Count > 0)
        {
            sb.AppendLine("## 4. Hardware Safety & Electrical Rules");
            foreach (var rule in sensor.SafetyRules)
                sb.AppendLine($"- {rule}");
            sb.AppendLine();
        }

        sb.AppendLine("## 5. Verification Test Firmware");
        sb.AppendLine("### Arduino C++ (.ino)");
        sb.AppendLine("```cpp");
        sb.AppendLine(sensor.MinimalTestCodeIno);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### MicroPython (.py)");
        sb.AppendLine("```python");
        sb.AppendLine(sensor.MinimalTestCodePy);
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static string GetI2cSdaPin(MicrocontrollerBoardType boardType) => boardType switch
    {
        MicrocontrollerBoardType.ArduinoMega2560 => "Pin D20 (SDA)",
        MicrocontrollerBoardType.Esp32DevKitV1 => "GPIO 21 (SDA)",
        MicrocontrollerBoardType.WemosEsp32S2Mini => "GPIO 8 (SDA)",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => "PB7 (I2C1_SDA)",
        _ => "Pin A4 (SDA)"
    };

    private static string GetI2cSclPin(MicrocontrollerBoardType boardType) => boardType switch
    {
        MicrocontrollerBoardType.ArduinoMega2560 => "Pin D21 (SCL)",
        MicrocontrollerBoardType.Esp32DevKitV1 => "GPIO 22 (SCL)",
        MicrocontrollerBoardType.WemosEsp32S2Mini => "GPIO 9 (SCL)",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => "PB6 (I2C1_SCL)",
        _ => "Pin A5 (SCL)"
    };

    private static string FormatBoardName(MicrocontrollerBoardType boardType) => boardType switch
    {
        MicrocontrollerBoardType.ArduinoMega2560 => "Arduino Mega 2560",
        MicrocontrollerBoardType.Esp32DevKitV1 => "ESP32 DevKit v1",
        MicrocontrollerBoardType.WemosEsp32S2Mini => "Wemos ESP32-S2 Mini",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => "STM32 Blue Pill",
        MicrocontrollerBoardType.None => "Generic Breadboard",
        _ => "Arduino Uno R3 / R4"
    };

    private static void RegisterAllSensors()
    {
        // 1. HC-SR04 Ultrasonic Distance Sensor
        SensorCatalog["HC-SR04"] = new SensorInterfaceSpec(
            SensorId: "HC-SR04",
            DisplayName: "HC-SR04 Ultrasonic Distance Sensor",
            Category: "Proximity & Motion",
            Summary: "Measures non-contact distance to obstacles from 2 cm to 400 cm (0.8\" to 13 feet) with millimeter accuracy.",
            WorkingPrinciple: "Emits eight 40 kHz ultrasonic acoustic bursts when a 10µs trigger pulse is received on the TRIG pin. The onboard receiver detects the reflected echo and outputs a logic HIGH pulse on the ECHO pin whose duration is directly proportional to round-trip travel time. Distance in centimeters is calculated as: Distance (cm) = (Echo Time in µs × 0.0343) / 2.",
            OperatingVoltage: "5.0V DC (HC-SR04) / 3.3V–5.0V (HC-SR04P)",
            TypicalCurrentMa: 15.0,
            Protocol: "Digital Trigger & Echo Pulse (pulseIn)",
            PinoutSummary: "4 pins: VCC (+5V), TRIG (Input), ECHO (Output), GND (0V)",
            RequiredExternalParts: new[] { "1kΩ and 2kΩ resistors for voltage divider when used with 3.3V microcontrollers (ESP32 / STM32)." },
            SafetyRules: new[]
            {
                "ECHO pin outputs 5V TTL logic. When interfacing with ESP32, Raspberry Pi, or STM32 (3.3V max input), you MUST use a voltage divider or logic level converter to prevent destroying the MCU's GPIO input buffer.",
                "Maintain a minimum 60ms measurement cycle delay between successive trigger pulses to prevent false echo reflections."
            },
            MinimalTestCodeIno:
@"const int trigPin = 9;
const int echoPin = 10;

void setup() {
    Serial.begin(115200);
    pinMode(trigPin, OUTPUT);
    pinMode(echoPin, INPUT);
    Serial.println(F(""HC-SR04 Test Initialized""));
}

void loop() {
    digitalWrite(trigPin, LOW);
    delayMicroseconds(2);
    digitalWrite(trigPin, HIGH);
    delayMicroseconds(10);
    digitalWrite(trigPin, LOW);

    long duration = pulseIn(echoPin, HIGH, 30000);
    float distanceCm = (duration * 0.0343) / 2.0;

    Serial.print(F(""Distance: ""));
    Serial.print(distanceCm);
    Serial.println(F("" cm""));
    delay(250);
}",
            MinimalTestCodePy:
@"from machine import Pin, time_pulse_us
import time

trig = Pin(5, Pin.OUT)
echo = Pin(18, Pin.IN)

while True:
    trig.value(0)
    time.sleep_us(2)
    trig.value(1)
    time.sleep_us(10)
    trig.value(0)
    duration = time_pulse_us(echo, 1, 30000)
    distance_cm = (duration * 0.0343) / 2.0
    print(f""Distance: {distance_cm:.1f} cm"")
    time.sleep_ms(250)"
        );

        // 2. DHT11 Temperature & Humidity Sensor
        SensorCatalog["DHT11"] = new SensorInterfaceSpec(
            SensorId: "DHT11",
            DisplayName: "DHT11 Digital Temperature & Humidity Sensor",
            Category: "Environmental",
            Summary: "Low-cost composite sensor providing calibrated digital output for ambient temperature (0°C to 50°C ±2°C) and relative humidity (20% to 90% RH ±5%).",
            WorkingPrinciple: "Contains a resistive humidity sensing component and an NTC temperature thermistor connected to an onboard 8-bit OTP microcontroller. The sensor outputs a single-wire bi-directional synchronous 40-bit data stream (16-bit humidity, 16-bit temperature, 8-bit checksum) when pulled low by the host MCU for 18ms.",
            OperatingVoltage: "3.3V to 5.5V DC",
            TypicalCurrentMa: 1.5,
            Protocol: "Proprietary 1-Wire Synchronous Bit-Stream",
            PinoutSummary: "4 pins (or 3 on breakout module): 1=VCC, 2=DATA, 3=NC, 4=GND",
            RequiredExternalParts: new[] { "4.7kΩ to 10kΩ pull-up resistor between VCC and DATA line." },
            SafetyRules: new[]
            {
                "Do not sample the DHT11 faster than once every 1.5 to 2.0 seconds; high polling frequencies cause sensor internal self-heating and inaccurate readings.",
                "Ensure reliable pull-up resistor (4.7kΩ–10kΩ) is present between DATA and VCC; missing pull-up will lead to constant timeout or checksum errors."
            },
            MinimalTestCodeIno:
@"#include <DHT.h>
#define DHTPIN 2
#define DHTTYPE DHT11

DHT dht(DHTPIN, DHTTYPE);

void setup() {
    Serial.begin(115200);
    dht.begin();
    Serial.println(F(""DHT11 Diagnostic Test Running""));
}

void loop() {
    delay(2000);
    float h = dht.readHumidity();
    float t = dht.readTemperature();
    if (isnan(h) || isnan(t)) {
        Serial.println(F(""Failed to read from DHT11 sensor!""));
        return;
    }
    Serial.print(F(""Humidity: "")); Serial.print(h); Serial.print(F(""%  Temperature: "")); Serial.print(t); Serial.println(F("" C""));
}",
            MinimalTestCodePy:
@"import dht
from machine import Pin
import time

sensor = dht.DHT11(Pin(4))

while True:
    time.sleep(2)
    try:
        sensor.measure()
        print(f""Temp: {sensor.temperature()}C, Humidity: {sensor.humidity()}%"")
    except Exception as e:
        print(""DHT Read error:"", e)"
        );

        // 3. DHT22 / AM2302 High-Precision Sensor
        SensorCatalog["DHT22"] = new SensorInterfaceSpec(
            SensorId: "DHT22",
            DisplayName: "DHT22 / AM2302 High-Precision Temp & Humidity",
            Category: "Environmental",
            Summary: "High-accuracy environmental sensor measuring -40°C to +80°C (±0.5°C) and 0% to 100% relative humidity (±2%).",
            WorkingPrinciple: "Employs an upgraded capacitive relative humidity sensor element and high-precision NTC thermistor. Communicates via a 40-bit single-wire digital packet with 0.1°C temperature and 0.1% RH resolution.",
            OperatingVoltage: "3.3V to 6.0V DC",
            TypicalCurrentMa: 2.1,
            Protocol: "Single-Wire Bit-Stream (40-bit with checksum)",
            PinoutSummary: "4 pins (or 3-pin module): 1=VCC, 2=DATA, 3=NC, 4=GND",
            RequiredExternalParts: new[] { "4.7kΩ to 10kΩ pull-up resistor on the DATA line." },
            SafetyRules: new[]
            {
                "Operates cleanly on both 3.3V and 5.0V; on 3.3V systems (ESP32/Pico), connect VCC to 3.3V so output pulses remain native 3.3V."
            },
            MinimalTestCodeIno:
@"#include <DHT.h>
#define DHTPIN 2
#define DHTTYPE DHT22
DHT dht(DHTPIN, DHTTYPE);

void setup() {
    Serial.begin(115200);
    dht.begin();
}
void loop() {
    delay(2000);
    Serial.print(F(""Temp: "")); Serial.print(dht.readTemperature()); Serial.print(F("" C, Hum: "")); Serial.println(dht.readHumidity());
}",
            MinimalTestCodePy:
@"import dht
from machine import Pin
import time

sensor = dht.DHT22(Pin(4))
while True:
    time.sleep(2)
    sensor.measure()
    print(f""DHT22: {sensor.temperature():.1f}C, {sensor.humidity():.1f}%"")"
        );

        // 4. SSD1306 0.96" I2C OLED Display
        SensorCatalog["OLED_SSD1306"] = new SensorInterfaceSpec(
            SensorId: "OLED_SSD1306",
            DisplayName: "0.96\" I2C OLED Display (SSD1306 128x64)",
            Category: "Actuators & Displays",
            Summary: "Monochrome 128x64 pixel self-illuminating graphical OLED display with high contrast and wide viewing angles.",
            WorkingPrinciple: "Contains a grid of 8,192 organic light-emitting diodes driven by an internal SSD1306 controller with charge-pump DC-DC boost converter. Communicates over I2C standard-mode (100kHz) or fast-mode (400kHz) addressing page memory (GDDRAM).",
            OperatingVoltage: "3.3V to 5.0V DC (with on-board LDO)",
            TypicalCurrentMa: 20.0,
            Protocol: "I2C Serial Bus (Default Address: 0x3C or 0x3D)",
            PinoutSummary: "4 pins: GND, VCC, SCL (Clock), SDA (Data)",
            RequiredExternalParts: new[] { "Breakout boards usually contain 4.7kΩ I2C pull-ups." },
            SafetyRules: new[]
            {
                "Always check pin labelling carefully: some clones order pins GND-VCC-SCL-SDA while others order VCC-GND-SCL-SDA! Reversing VCC and GND will instantly burn out the controller."
            },
            MinimalTestCodeIno:
@"#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>

#define SCREEN_WIDTH 128
#define SCREEN_HEIGHT 64
Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, -1);

void setup() {
    Serial.begin(115200);
    if (!display.begin(SSD1306_SWITCHCAPVCC, 0x3C)) {
        Serial.println(F(""SSD1306 allocation failed""));
        for (;;);
    }
    display.clearDisplay();
    display.setTextSize(1);
    display.setTextColor(SSD1306_WHITE);
    display.setCursor(10, 20);
    display.println(F(""Breadboard AOI""));
    display.setCursor(10, 36);
    display.println(F(""OLED Online!""));
    display.display();
}

void loop() {} ",
            MinimalTestCodePy:
@"from machine import Pin, I2C
import ssd1306

i2c = I2C(0, scl=Pin(22), sda=Pin(21))
oled = ssd1306.SSD1306_I2C(128, 64, i2c)
oled.fill(0)
oled.text('Breadboard AOI', 10, 20)
oled.text('MicroPython OK', 10, 36)
oled.show()"
        );

        // 5. MPU-6050 6-Axis Motion Tracking Sensor
        SensorCatalog["MPU6050"] = new SensorInterfaceSpec(
            SensorId: "MPU6050",
            DisplayName: "MPU-6050 6-Axis IMU (Gyroscope + Accelerometer)",
            Category: "Proximity & Motion",
            Summary: "6-degree-of-freedom inertial measurement unit combining a 3-axis MEMS accelerometer and 3-axis MEMS gyroscope with an internal Digital Motion Processor (DMP).",
            WorkingPrinciple: "Micro-machined silicon proof masses deflect under acceleration or Coriolis effect from angular rotation, modulating capacitive sensors. On-chip 16-bit ADCs digitize each channel simultaneously over a ±2g to ±16g range and ±250°/s to ±2000°/s gyro range.",
            OperatingVoltage: "3.0V to 3.6V DC (Breakout GY-521 accepts 3.3V–5V via 662K LDO)",
            TypicalCurrentMa: 3.8,
            Protocol: "I2C Bus (Default 0x68 when AD0=GND, 0x69 when AD0=VCC)",
            PinoutSummary: "8 pins: VCC, GND, SCL, SDA, XDA, XCL, AD0, INT",
            RequiredExternalParts: new[] { "4.7kΩ pull-up resistors on SDA and SCL if not present on breakout module." },
            SafetyRules: new[]
            {
                "On bare MPU-6050 chips, input voltage exceeding 3.6V will permanently damage the silicon. GY-521 modules include a 3.3V regulator but their I2C lines communicate at logic level."
            },
            MinimalTestCodeIno:
@"#include <Wire.h>
const int MPU_ADDR = 0x68;
int16_t AcX, AcY, AcZ, Tmp, GyX, GyY, GyZ;

void setup() {
    Wire.begin();
    Wire.beginTransmission(MPU_ADDR);
    Wire.write(0x6B); // PWR_MGMT_1 register
    Wire.write(0);    // Wake up MPU-6050
    Wire.endTransmission(true);
    Serial.begin(115200);
}

void loop() {
    Wire.beginTransmission(MPU_ADDR);
    Wire.write(0x3B); // Starting with register 0x3B (ACCEL_XOUT_H)
    Wire.endTransmission(false);
    Wire.requestFrom(MPU_ADDR, 14, true);

    AcX = Wire.read()<<8 | Wire.read();
    AcY = Wire.read()<<8 | Wire.read();
    AcZ = Wire.read()<<8 | Wire.read();
    Tmp = Wire.read()<<8 | Wire.read();
    GyX = Wire.read()<<8 | Wire.read();
    GyY = Wire.read()<<8 | Wire.read();
    GyZ = Wire.read()<<8 | Wire.read();

    Serial.print(F(""Accel: "")); Serial.print(AcX); Serial.print(F("", "")); Serial.print(AcY); Serial.print(F("", "")); Serial.println(AcZ);
    delay(200);
}",
            MinimalTestCodePy:
@"from machine import Pin, I2C
import time

i2c = I2C(0, scl=Pin(22), sda=Pin(21))
i2c.writeto_mem(0x68, 0x6B, b'\x00') # Wake up MPU

while True:
    data = i2c.readfrom_mem(0x68, 0x3B, 6)
    ax = int.from_bytes(data[0:2], 'big', True)
    ay = int.from_bytes(data[2:4], 'big', True)
    az = int.from_bytes(data[4:6], 'big', True)
    print(f""Accel: X={ax}, Y={ay}, Z={az}"")
    time.sleep_ms(200)"
        );

        // 6. MQ-2 Gas & Smoke Sensor
        SensorCatalog["MQ-2"] = new SensorInterfaceSpec(
            SensorId: "MQ-2",
            DisplayName: "MQ-2 Flammable Gas, LPG & Smoke Sensor",
            Category: "Audio & Gas",
            Summary: "Detects combustible gases including Methane, Propane, Butane, LPG, Hydrogen, and airborne smoke particles.",
            WorkingPrinciple: "Utilizes a heated tin-dioxide (SnO2) ceramic semiconductor element. In clean air, conductivity is low. When combustible gases are absorbed, the surface barrier potential drops and electrical conductivity increases proportionally to gas concentration.",
            OperatingVoltage: "5.0V DC ±0.1V (Strictly required for internal heating element)",
            TypicalCurrentMa: 150.0,
            Protocol: "Analog Voltage (AO) + Digital Threshold (DO)",
            PinoutSummary: "4 pins: VCC (+5V), GND (0V), DO (Digital Output), AO (Analog Output)",
            RequiredExternalParts: new[] { "Requires a stable 5V power supply capable of supplying at least 200mA heating current." },
            SafetyRules: new[]
            {
                "The MQ-2 gets noticeably warm during normal operation due to its 5V internal heater; this is normal physical operation.",
                "Ensure pre-heating warm-up time of 24–48 hours for brand new sensors to achieve rated ppm accuracy, or 3 minutes for general bench diagnostics."
            },
            MinimalTestCodeIno:
@"const int gasAoPin = A0;
const int gasDoPin = 7;

void setup() {
    Serial.begin(115200);
    pinMode(gasDoPin, INPUT);
    Serial.println(F(""MQ-2 Gas Sensor Initialized""));
}

void loop() {
    int analogVal = analogRead(gasAoPin);
    bool alarm = digitalRead(gasDoPin) == LOW;

    Serial.print(F(""Gas Concentration Analog: ""));
    Serial.print(analogVal);
    Serial.print(F("" | Alarm: ""));
    Serial.println(alarm ? F(""GAS DETECTED!"") : F(""Normal""));
    delay(500);
}",
            MinimalTestCodePy:
@"from machine import Pin, ADC
import time

gas_ao = ADC(Pin(34))
gas_do = Pin(15, Pin.IN)

while True:
    val = gas_ao.read_u16()
    alarm = gas_do.value() == 0
    print(f""Gas Level: {val} | Alarm: {'ALERT!' if alarm else 'Clear'}"")
    time.sleep_ms(500)"
        );

        // 7. PIR Motion Sensor HC-SR501
        SensorCatalog["PIR"] = new SensorInterfaceSpec(
            SensorId: "PIR",
            DisplayName: "HC-SR501 PIR Passive Infrared Motion Sensor",
            Category: "Proximity & Motion",
            Summary: "Detects human, animal, or thermal infrared radiation moving across optical Fresnel lens zones up to 7 meters away.",
            WorkingPrinciple: "Contains a pyroelectric lithium tantalate sensor element split into two halves. When an infrared radiating body walks past, one half experiences a differential temperature rise before the second, generating a microvolt fluctuation amplified by a BISS0001 controller chip.",
            OperatingVoltage: "4.5V to 20V DC (on-board 3.3V LDO regulator)",
            TypicalCurrentMa: 0.05,
            Protocol: "Digital Logic (3.3V HIGH on motion, 0V LOW when clear)",
            PinoutSummary: "3 pins: VCC (+5V), OUT (3.3V Digital Signal), GND (0V)",
            RequiredExternalParts: new[] { "None; completely self-contained with onboard sensitivity and delay potentiometers." },
            SafetyRules: new[]
            {
                "The OUT pin outputs native 3.3V logic regardless of whether VCC is 5V, 9V, or 12V, making it universally safe for both 3.3V (ESP32) and 5V (Arduino) microcontrollers.",
                "Allow a 30 to 60-second stabilization settling period after powering up before treating readings as valid motion events."
            },
            MinimalTestCodeIno:
@"const int pirPin = 4;

void setup() {
    Serial.begin(115200);
    pinMode(pirPin, INPUT);
    Serial.println(F(""PIR Warming Up (Please wait 30s)...""));
}

void loop() {
    int motion = digitalRead(pirPin);
    if (motion == HIGH) {
        Serial.println(F("">>> MOTION DETECTED! <<<""));
    } else {
        Serial.println(F(""Room Clear""));
    }
    delay(500);
}",
            MinimalTestCodePy:
@"from machine import Pin
import time

pir = Pin(13, Pin.IN)
while True:
    if pir.value() == 1:
        print("">>> MOTION DETECTED! <<<"")
    else:
        print(""Area Clear"")
    time.sleep_ms(500)"
        );

        // 8. LDR Light Sensor (Photoresistor)
        SensorCatalog["LDR"] = new SensorInterfaceSpec(
            SensorId: "LDR",
            DisplayName: "LDR Photoresistor (Light Dependent Resistor)",
            Category: "Optical",
            Summary: "Passive semiconductor light sensor that drops in electrical resistance as incident ambient lux increases.",
            WorkingPrinciple: "Cadmium sulfide (CdS) photo-conductive cells contain valence electrons. Incoming photons with energy exceeding the bandgap free electrons into the conduction band, decreasing resistance from >1 MΩ in total darkness to <1 kΩ in direct bright sunlight.",
            OperatingVoltage: "0V to 30V DC (determined by circuit voltage divider)",
            TypicalCurrentMa: 0.5,
            Protocol: "Analog Voltage Divider",
            PinoutSummary: "2 non-polarized leads",
            RequiredExternalParts: new[] { "10kΩ pull-down resistor to create a voltage divider." },
            SafetyRules: new[]
            {
                "Never connect an LDR directly across power and ground without a fixed series resistor; in high light intensity, the resistance drops near zero, causing a destructive short circuit."
            },
            MinimalTestCodeIno:
@"const int ldrPin = A0;

void setup() {
    Serial.begin(115200);
}

void loop() {
    int lightVal = analogRead(ldrPin);
    Serial.print(F(""Ambient Light Value: ""));
    Serial.print(lightVal);
    Serial.println(lightVal < 300 ? F("" [DARK]"") : (lightVal > 800 ? F("" [BRIGHT]"") : F("" [NORMAL]"")));
    delay(250);
}",
            MinimalTestCodePy:
@"from machine import Pin, ADC
import time

ldr = ADC(Pin(36))
while True:
    val = ldr.read_u16()
    print(f""Light Level: {val}"")
    time.sleep_ms(250)"
        );

        // 9. TMP36 Precision Linear Temperature Sensor
        SensorCatalog["TMP36"] = new SensorInterfaceSpec(
            SensorId: "TMP36",
            DisplayName: "TMP36 / LM35 Precision Linear Temperature Sensor",
            Category: "Environmental",
            Summary: "Low-voltage, precision centigrade temperature sensor providing an analog output linearly proportional to Celsius temperature.",
            WorkingPrinciple: "Exploits the temperature-dependent forward voltage characteristics of matched transistor base-emitter junctions (ΔVbe bandgap reference). Generates exactly 10 mV/°C output voltage with a 500 mV offset at 0°C to allow reading negative temperatures down to -40°C.",
            OperatingVoltage: "2.7V to 5.5V DC",
            TypicalCurrentMa: 0.05,
            Protocol: "Linear Analog Voltage (10mV/°C)",
            PinoutSummary: "3 pins (TO-92 flat face forward): 1=VCC (+Vs), 2=VOUT (Signal), 3=GND (0V)",
            RequiredExternalParts: new[] { "Optional 100nF ceramic decoupling capacitor across VCC and GND." },
            SafetyRules: new[]
            {
                "Verify TO-92 pinout orientation! Placing the TMP36 backwards (VCC into GND) will turn it into a short-circuit heating element that will burn your fingers within seconds."
            },
            MinimalTestCodeIno:
@"const int tempPin = A0;

void setup() {
    Serial.begin(115200);
}

void loop() {
    int raw = analogRead(tempPin);
    float voltage = raw * (5.0 / 1024.0);
    float tempC = (voltage - 0.5) * 100.0;

    Serial.print(F(""Voltage: "")); Serial.print(voltage); Serial.print(F(""V, Temp: ""));
    Serial.print(tempC); Serial.println(F("" C""));
    delay(1000);
}",
            MinimalTestCodePy:
@"from machine import Pin, ADC
import time

adc = ADC(Pin(34))
while True:
    raw = adc.read_u16()
    voltage = (raw / 65535.0) * 3.3
    temp_c = (voltage - 0.5) * 100.0
    print(f""Temp: {temp_c:.1f} C"")
    time.sleep(1)"
        );

        // 10. SG90 Micro Servo Motor
        SensorCatalog["SERVO_SG90"] = new SensorInterfaceSpec(
            SensorId: "SERVO_SG90",
            DisplayName: "SG90 9g Micro Servo Motor (0° to 180°)",
            Category: "Actuators & Displays",
            Summary: "High-torque miniature rotary actuator capable of precise angular position control between 0 and 180 degrees.",
            WorkingPrinciple: "Contains a DC coreless motor, reduction gearbox, output splined shaft, potentiometer for position feedback, and an analog error amplifier. Accepts a 50Hz PWM control signal where a 1.0ms pulse commands 0°, 1.5ms commands 90° center, and 2.0ms commands 180°.",
            OperatingVoltage: "4.8V to 6.0V DC",
            TypicalCurrentMa: 250.0,
            Protocol: "PWM Position Control (50Hz / 20ms period)",
            PinoutSummary: "3 wires: Brown=GND, Red=VCC (+5V), Orange/Yellow=PWM Signal",
            RequiredExternalParts: new[] { "Recommend 470µF electrolytic capacitor across 5V and GND rails to absorb motor stall current spikes." },
            SafetyRules: new[]
            {
                "Do NOT power more than one servo directly from an Arduino or ESP32 board's 5V regulator pin; stall currents (up to 650mA per servo) can cause brown-outs or reset loops. Use an external 5V power supply with a shared ground reference."
            },
            MinimalTestCodeIno:
@"#include <Servo.h>
Servo myServo;

void setup() {
    myServo.attach(9);
}

void loop() {
    myServo.write(0);
    delay(1000);
    myServo.write(90);
    delay(1000);
    myServo.write(180);
    delay(1000);
}",
            MinimalTestCodePy:
@"from machine import Pin, PWM
import time

servo = PWM(Pin(19), freq=50)

def set_angle(angle):
    duty = int(1638 + (angle / 180.0) * 6554)
    servo.duty_u16(duty)

while True:
    set_angle(0)
    time.sleep(1)
    set_angle(90)
    time.sleep(1)
    set_angle(180)
    time.sleep(1)"
        );

        // 11. 5V Relay Module
        SensorCatalog["RELAY_5V"] = new SensorInterfaceSpec(
            SensorId: "RELAY_5V",
            DisplayName: "5V Single-Channel Optocoupler Relay Module",
            Category: "Actuators & Displays",
            Summary: "Electromagnetic power switch allowing a low-power microcontroller to safely switch mains appliances or DC motors up to 10A @ 250V AC / 30V DC.",
            WorkingPrinciple: "An optocoupler (e.g. EL817) provides galvanic optical isolation between the MCU and the relay coil. When the IN pin is driven LOW (active low), an internal transistor energizes the coil, pulling the mechanical armature from Normally Open (NO) to Normally Closed (NC). Includes flyback diode to quench reverse inductive emf.",
            OperatingVoltage: "5.0V DC (Coil)",
            TypicalCurrentMa: 70.0,
            Protocol: "Digital Logic (Active LOW / Active HIGH)",
            PinoutSummary: "Input side: VCC, GND, IN. Output terminals: COM (Common), NO (Normally Open), NC (Normally Closed)",
            RequiredExternalParts: new[] { "None; on-board transistor driver, optocoupler, and flyback diode are built-in." },
            SafetyRules: new[]
            {
                "When switching AC mains voltages (120V/240V), maintain physical barrier insulation. Never touch breadboard or screw terminals while connected to wall outlet."
            },
            MinimalTestCodeIno:
@"const int relayPin = 7;

void setup() {
    pinMode(relayPin, OUTPUT);
    digitalWrite(relayPin, HIGH); // Relay OFF (Active LOW)
}

void loop() {
    digitalWrite(relayPin, LOW);  // Turn Relay ON
    delay(2000);
    digitalWrite(relayPin, HIGH); // Turn Relay OFF
    delay(2000);
}",
            MinimalTestCodePy:
@"from machine import Pin
import time

relay = Pin(12, Pin.OUT)
while True:
    relay.value(0) # ON
    time.sleep(2)
    relay.value(1) # OFF
    time.sleep(2)"
        );

        // 12. Potentiometer
        SensorCatalog["POTENTIOMETER"] = new SensorInterfaceSpec(
            SensorId: "POTENTIOMETER",
            DisplayName: "10kΩ Rotary Potentiometer Dial",
            Category: "Analog Input",
            Summary: "Three-terminal manual rotary resistor providing variable analog voltage input from 0V to VCC.",
            WorkingPrinciple: "A wiper contact moves along a resistive carbon track, dividing the applied voltage linearly according to angular position: Vout = Vcc × (θ / θmax).",
            OperatingVoltage: "0V to 30V DC",
            TypicalCurrentMa: 0.5,
            Protocol: "Analog Voltage Input",
            PinoutSummary: "3 pins: Pin 1=VCC, Pin 2=Wiper Output (SIG), Pin 3=GND",
            RequiredExternalParts: new[] { "None" },
            SafetyRules: new[] { "Connect the two outer pins to VCC and GND, and the middle pin to the analog input. Connecting the middle pin to VCC and outer pin to GND without resistance can create a short when rotated to end limits." },
            MinimalTestCodeIno:
@"void setup() { Serial.begin(115200); }
void loop() {
    Serial.println(analogRead(A0));
    delay(100);
}",
            MinimalTestCodePy:
@"from machine import Pin, ADC
import time
pot = ADC(Pin(34))
while True:
    print(pot.read_u16())
    time.sleep_ms(100)"
        );

        // 13. LCD1602 Character LCD with I2C Backpack
        SensorCatalog["LCD1602_I2C"] = new SensorInterfaceSpec(
            SensorId: "LCD1602_I2C",
            DisplayName: "LCD1602 16x2 Character LCD (PCF8574 I2C Backpack)",
            Category: "Display Module",
            Summary: "Standard HD44780 dot-matrix character liquid crystal display with a PCF8574 I2C 8-bit I/O expander backpack. Displays 2 rows of 16 characters with blue LED backlight and adjustable contrast trimmer.",
            WorkingPrinciple: "The PCF8574 backpack converts 2-wire serial I2C bus packets into 4-bit parallel data commands (D4-D7), register select (RS), and enable pulse (EN) to command the HD44780 LCD controller. Built-in blue backlight LED illuminates nematic liquid crystal cells that rotate polarized light under electric field to form crisp 5x8 dot-matrix characters.",
            OperatingVoltage: "4.5V to 5.5V DC (Strictly 5.0V recommended; at 3.3V, liquid crystal contrast potential is insufficient and characters remain invisible)",
            TypicalCurrentMa: 25.0,
            Protocol: "I2C Bus (Default 0x27 or 0x3F, configurable via A0/A1/A2 solder pads)",
            PinoutSummary: "4 pins: GND (0V), VCC (+5V), SDA (I2C Data), SCL (I2C Clock)",
            RequiredExternalParts: new[] { "PCF8574 backpack includes on-board 4.7kΩ I2C pull-up resistors and contrast trimmer potentiometer." },
            SafetyRules: new[]
            {
                "CRITICAL CONTRAST / VOLTAGE TRAP: Do NOT power standard 5V LCD1602 modules from a 3.3V rail. Although the backlight lights up and the I2C chip responds, the liquid crystal fluid requires ~4.5V potential; characters will be completely invisible even with the contrast potentiometer turned fully to GND.",
                "3.3V MCU PROTECTION: When connecting the 5V LCD backpack to 3.3V-only microcontrollers (ESP32 / STM32 / Raspberry Pi Pico), the backpack's 4.7kΩ pull-up resistors pull SDA and SCL to 5.0V! Use a bidirectional 3.3V <-> 5V I2C logic level shifter to protect your MCU."
            },
            MinimalTestCodeIno:
@"#include <Wire.h>
#include <LiquidCrystal_I2C.h>

LiquidCrystal_I2C lcd(0x27, 16, 2);

void setup() {
    Serial.begin(115200);
    lcd.init();
    lcd.backlight();
    lcd.setCursor(0, 0);
    lcd.print(""Breadboard AOI"");
    lcd.setCursor(0, 1);
    lcd.print(""LCD 1602 Online!"");
}

void loop() {}",
            MinimalTestCodePy:
@"from machine import Pin, I2C
from i2c_lcd import I2cLcd
import time

i2c = I2C(0, scl=Pin(22), sda=Pin(21), freq=400000)
lcd = I2cLcd(i2c, 0x27, 2, 16)
lcd.putstr(""Breadboard AOI\nLCD 1602 Online!"")

while True:
    time.sleep(1)"
        );
        SensorCatalog["LCD1602"] = SensorCatalog["LCD1602_I2C"];
    }
}
