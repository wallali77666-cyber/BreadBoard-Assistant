// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Contracts/ComponentCatalog.cs
// PURPOSE: Standard classified catalogs for breadboard components (Jumpers,
//          Resistors, Capacitors, ICs, Microcontrollers, LEDs, Buttons) with
//          authentic specs, pin counts, and KiCad symbol identifiers.
// ============================================================================

using System.Collections.Generic;

namespace BreadboardAOI.Core.Contracts;

/// <summary>Breadboard jumper wire color preset.</summary>
public readonly record struct JumperColorOption(string Name, string Code, string HexColor, string Usage);

/// <summary>Quick value / IC-type preset for breadboard parts.</summary>
public readonly record struct PartPreset(
    string Label,
    string Value,
    string? SymbolId = null,
    int? PinCount = null)
{
    public override string ToString() => Label;
}

public static class ComponentCatalog
{
    public static readonly IReadOnlyList<JumperColorOption> StandardJumperColors =
    [
        new("RED", "RED", "#E03E3E", "VCC / Power +5V / +12V"),
        new("BLACK", "BLACK", "#222222", "GND / Ground 0V"),
        new("BLUE", "BLUE", "#2B78E4", "GND / Signal"),
        new("YELLOW", "YELLOW", "#E8C52B", "Clock / Signal"),
        new("GREEN", "GREEN", "#2E8B46", "Signal / Audio"),
        new("ORANGE", "ORANGE", "#E0741E", "Signal / +3.3V"),
        new("PURPLE", "PURPLE", "#7A3E8E", "Logic / Trigger"),
        new("WHITE", "WHITE", "#E0E0E0", "Signal / Bus")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardResistors =
    [
        new("220 Ω  (RED-RED-BROWN)", "220R", "Device/R"),
        new("330 Ω  (LED CURRENT LIMITER)", "330R", "Device/R"),
        new("470 Ω  (ORANGE-ORANGE-BROWN)", "470R", "Device/R"),
        new("1 kΩ   (BROWN-BLACK-RED)", "1K", "Device/R"),
        new("2.2 kΩ (RED-RED-RED)", "2K2", "Device/R"),
        new("4.7 kΩ (YELLOW-VIOLET-RED)", "4K7", "Device/R"),
        new("10 kΩ  (STANDARD PULL-UP)", "10K", "Device/R"),
        new("47 kΩ  (YELLOW-VIOLET-ORANGE)", "47K", "Device/R"),
        new("100 kΩ (BROWN-BLACK-YELLOW)", "100K", "Device/R"),
        new("1 MΩ   (BROWN-BLACK-GREEN)", "1M", "Device/R")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardCapacitors =
    [
        // Ceramic Disc (pF / nF)
        new("100 pF  CERAMIC (101)", "100pF", "Device/C"),
        new("1 nF    CERAMIC (102)", "1nF", "Device/C"),
        new("10 nF   CERAMIC (103)", "10nF", "Device/C"),
        new("100 nF  CERAMIC (104 / 0.1 µF)", "100nF", "Device/C"),

        // Radial Electrolytic (µF)
        new("1 µF    ELECTROLYTIC 50V", "1uF", "Device/C_Polarized"),
        new("10 µF   ELECTROLYTIC 25V", "10uF", "Device/C_Polarized"),
        new("47 µF   ELECTROLYTIC 25V", "47uF", "Device/C_Polarized"),
        new("100 µF  ELECTROLYTIC 16V", "100uF", "Device/C_Polarized"),
        new("470 µF  ELECTROLYTIC 16V", "470uF", "Device/C_Polarized"),
        new("1000 µF ELECTROLYTIC 16V", "1000uF", "Device/C_Polarized")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardLeds =
    [
        new("5MM RED    (2.0 V / 20 mA)", "RED 2.0V", "Device/LED"),
        new("5MM GREEN  (2.1 V / 20 mA)", "GREEN 2.1V", "Device/LED"),
        new("5MM BLUE   (3.2 V / 20 mA)", "BLUE 3.2V", "Device/LED"),
        new("5MM YELLOW (2.1 V / 20 mA)", "YELLOW 2.1V", "Device/LED"),
        new("5MM WHITE  (3.0 V / 20 mA)", "WHITE 3.0V", "Device/LED"),
        new("5MM ORANGE (2.0 V / 20 mA)", "ORANGE 2.0V", "Device/LED")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardIcs =
    [
        // Timers & Clocks
        new("NE555P  PRECISION TIMER (DIP-8)", "NE555P", "Timer/NE555P", 8),
        new("NE556   DUAL TIMER (DIP-14)", "NE556", "Timer/NE556", 14),

        // Op-Amps & Comparators
        new("LM358   DUAL OP-AMP (DIP-8)", "LM358", "Amplifier_Operational/LM358", 8),
        new("LM741   SINGLE OP-AMP (DIP-8)", "LM741", "Amplifier_Operational/LM741", 8),
        new("LM393   DUAL COMPARATOR (DIP-8)", "LM393", "Comparator/LM393", 8),
        new("LM324   QUAD OP-AMP (DIP-14)", "LM324", "Amplifier_Operational/LM324", 14),

        // 74xx Logic Gates (DIP-14)
        new("74LS00  QUAD 2-IN NAND (DIP-14)", "74LS00", "74xx/74LS00", 14),
        new("74LS02  QUAD 2-IN NOR (DIP-14)", "74LS02", "74xx/74LS02", 14),
        new("74LS04  HEX INVERTER / NOT (DIP-14)", "74LS04", "74xx/74LS04", 14),
        new("74LS08  QUAD 2-IN AND (DIP-14)", "74LS08", "74xx/74LS08", 14),
        new("74LS32  QUAD 2-IN OR (DIP-14)", "74LS32", "74xx/74LS32", 14),
        new("74LS86  QUAD 2-IN XOR (DIP-14)", "74LS86", "74xx/74LS86", 14),

        // Counters & Shift Registers
        new("CD4017  DECADE COUNTER (DIP-16)", "CD4017BE", "4xxx/4017", 16),
        new("74HC595 8-BIT SHIFT REG (DIP-16)", "74HC595", "74xx/74HC595", 16),

        // Motor Drivers & Power
        new("L293D   DUAL H-BRIDGE MOTOR (DIP-16)", "L293D", "Driver_Motor/L293D", 16),
        new("ULN2003 DARLINGTON ARRAY (DIP-16)", "ULN2003A", "Transistor_Array/ULN2003A", 16),

        // Microcontrollers & Development Boards
        new("ESP32-DEVKIT-V1 30-PIN MCU", "ESP32", "MCU_Espressif/ESP32-WROOM-32", 30),
        new("ESP32-NODEMCU-32S 38-PIN MCU", "ESP32_38P", "MCU_Espressif/ESP32-WROOM-32", 38),
        new("ARDUINO NANO 30-PIN MCU", "ARDUINO_NANO", "MCU_Module/Arduino_Nano_v3.x", 30),
        new("RASPBERRY PI PICO 40-PIN MCU", "PICO", "MCU_RaspberryPi/RaspberryPi_Pico", 40),
        new("ATmega328P 8-BIT AVR MCU (DIP-28)", "ATmega328P", "MCU_Microchip_ATmega/ATmega328P-P", 28),
        new("ATTiny85   8-BIT AVR MCU (DIP-8)", "ATTINY85-20PU", "MCU_Microchip_ATtiny/ATtiny85-20P", 8),
        new("PIC16F877A 8-BIT FLASH MCU (DIP-40)", "PIC16F877A", "MCU_Microchip_PIC16/PIC16F877A-I_P", 40),
        new("MSP430G2553 16-BIT MCU (DIP-20)", "MSP430G2553", "MCU_Texas_MSP430/MSP430G2553IN20", 20),
        new("MCP23017 16-BIT I2C EXPANDER (DIP-28)", "MCP23017", "Interface_Expansion/MCP23017_SP", 28),
        new("MAX7219 SERIALLY INTERFACED LED DRIVER (DIP-24)", "MAX7219", "Driver_LED/MAX7219CNG", 24),
        new("ADS1115 16-BIT 4-CH I2C ADC BREAKOUT (10-PIN)", "ADS1115", "Analog_ADC/ADS1115IDGS", 10),

        // Generic Footprints
        new("GENERIC DIP-8  IC", "DIP-8", "Package_DIP/DIP-8_W7.62mm", 8),
        new("GENERIC DIP-14 IC", "DIP-14", "Package_DIP/DIP-14_W7.62mm", 14),
        new("GENERIC DIP-16 IC", "DIP-16", "Package_DIP/DIP-16_W7.62mm", 16),
        new("GENERIC DIP-28 IC", "DIP-28", "Package_DIP/DIP-28_W7.62mm", 28),
        new("GENERIC DIP-30 MODULE", "DIP-30", "Package_DIP/DIP-30_W15.24mm", 30),
        new("GENERIC DIP-40 MODULE", "DIP-40", "Package_DIP/DIP-40_W15.24mm", 40)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardButtons =
    [
        new("6MM TACT SWITCH", "TACT 6MM", "Switch/SW_Push"),
        new("SPST SLIDE SWITCH", "SPST", "Switch/SW_SPST")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardDiodes =
    [
        new("1N4148  FAST SWITCHING (100V / 200mA)", "1N4148", "Device/D"),
        new("1N4007  POWER RECTIFIER (1000V / 1A)", "1N4007", "Device/D"),
        new("1N5819  SCHOTTKY BARRIER (40V / 1A)", "1N5819", "Device/D_Schottky"),
        new("BZX55C3V3 3.3V ZENER (500mW)", "3.3V ZENER", "Device/D_Zener"),
        new("BZX55C5V1 5.1V ZENER (500mW)", "5.1V ZENER", "Device/D_Zener"),
        new("BZX55C12  12V ZENER (500mW)", "12V ZENER", "Device/D_Zener")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardTransistors =
    [
        new("2N3904  NPN BJT (40V / 200mA) [E-B-C]", "2N3904", "Transistor_BJT/2N3904", 3),
        new("2N2222  NPN BJT (40V / 800mA) [E-B-C]", "2N2222", "Transistor_BJT/2N2222", 3),
        new("BC547   NPN BJT (45V / 100mA) [C-B-E]", "BC547", "Transistor_BJT/BC547", 3),
        new("2N3906  PNP BJT (40V / 200mA) [E-B-C]", "2N3906", "Transistor_BJT/2N3906", 3),
        new("BC557   PNP BJT (45V / 100mA) [C-B-E]", "BC557", "Transistor_BJT/BC557", 3),
        new("2N7000  N-CH MOSFET (60V / 200mA) [S-G-D]", "2N7000", "Transistor_FET/2N7000", 3),
        new("BS170   N-CH MOSFET (60V / 500mA) [D-G-S]", "BS170", "Transistor_FET/BS170", 3)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardPotentiometers =
    [
        new("10 kΩ  TRIMPOT (BOURNS 3362)", "10K_TRIM", "Device/R_Potentiometer_Trim", 3),
        new("1 kΩ   TRIMPOT (BOURNS 3362)", "1K_TRIM", "Device/R_Potentiometer_Trim", 3),
        new("5 kΩ   TRIMPOT (BOURNS 3362)", "5K_TRIM", "Device/R_Potentiometer_Trim", 3),
        new("50 kΩ  TRIMPOT (BOURNS 3362)", "50K_TRIM", "Device/R_Potentiometer_Trim", 3),
        new("100 kΩ TRIMPOT (BOURNS 3362)", "100K_TRIM", "Device/R_Potentiometer_Trim", 3),
        new("10 kΩ  ROTARY POTENTIOMETER", "10K_POT", "Device/R_Potentiometer", 3)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardRegulators =
    [
        new("L7805   +5.0V 1.5A LINEAR REGULATOR (TO-220)", "L7805", "Regulator_Linear/L7805", 3),
        new("LD1117V33 +3.3V 800mA LDO REGULATOR (TO-220)", "LD1117V33", "Regulator_Linear/LM1117-3.3", 3),
        new("LM317T  ADJUSTABLE 1.25V-37V 1.5A (TO-220)", "LM317T", "Regulator_Linear/LM317_TO220", 3),
        new("L7812   +12.0V 1.5A LINEAR REGULATOR (TO-220)", "L7812", "Regulator_Linear/L7812", 3)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardBuzzers =
    [
        new("5V ACTIVE PIEZO BUZZER", "BUZZER 5V", "Device/Buzzer"),
        new("PASSIVE PIEZO TRANSDUCER", "PIEZO", "Device/Buzzer")
    ];

    public static readonly IReadOnlyList<PartPreset> StandardSensors =
    [
        // Optical / Light
        new("LDR GL5528 PHOTORESISTOR (10K-20K)", "LDR GL5528", "Sensor_Optical/LDR_GL5528", 2),
        new("BPW34 PIN PHOTODIODE (940nm IR)", "BPW34", "Sensor_Optical/BPW34", 2),
        new("PT334-6B PHOTOTRANSISTOR (NPN IR)", "PT334-6B", "Sensor_Optical/L14F1", 2),

        // Temperature & Humidity
        new("NTC 10K THERMISTOR (B=3950)", "NTC 10K", "Device/R_Thermistor", 2),
        new("TMP36 ANALOG TEMP SENSOR (TO-92)", "TMP36", "Sensor_Temperature/TMP36", 3),
        new("DS18B20 1-WIRE DIGITAL TEMP (TO-92)", "DS18B20", "Sensor_Temperature/DS18B20", 3),
        new("DHT11 DIGITAL TEMP & HUMIDITY (4-PIN)", "DHT11", "Sensor/DHT11", 4),
        new("DHT22 PRECISION TEMP & HUMIDITY (4-PIN)", "DHT22", "Sensor/DHT22", 4),

        // Motion, Distance & Magnetism
        new("HC-SR04 ULTRASONIC DISTANCE (4-PIN)", "HC-SR04", "Sensor/HC-SR04", 4),
        new("HC-SR501 PIR MOTION SENSOR (3-PIN)", "HC-SR501", "Sensor_Motion/HC-SR501", 3),
        new("A3144 HALL EFFECT MAGNETIC SENSOR (3-PIN)", "A3144", "Sensor_Magnetic/A3144", 3),
        new("SW-520D TILT & VIBRATION SENSOR (2-PIN)", "SW-520D", "Switch/SW_Tilt", 2),

        // Gas & Audio
        new("MQ-2 GAS & SMOKE SENSOR MODULE (4-PIN)", "MQ-2", "Sensor_Gas/MQ-2", 4),
        new("ELECTRET MICROPHONE CAPSULE (2-PIN)", "MIC", "Device/Microphone", 2),
        new("KY-038 SOUND DETECTOR MODULE (4-PIN)", "KY-038", "Sensor_Audio/KY-038", 4),

        // Environmental, Inertial & Specialized Breakouts
        new("BME680 TEMP/HUMIDITY/GAS SENSOR (6-PIN)", "BME680", "Sensor/BME680", 6),
        new("MPU6050 6-AXIS GYRO & ACCEL (8-PIN)", "MPU6050", "Sensor_Motion/MPU-6050", 8),
        new("VL53L0X TIME-OF-FLIGHT LASER DISTANCE (6-PIN)", "VL53L0X", "Sensor_Optical/VL53L0X", 6),
        new("INA219 I2C CURRENT & POWER MONITOR (6-PIN)", "INA219", "Sensor_Current/INA219", 6),
        new("LCD1602 16x2 CHARACTER LCD I2C MODULE (4-PIN)", "LCD1602-I2C", "Display/LCD1602_I2C", 4),
        new("MFRC522 13.56MHZ RFID SPI READER (8-PIN)", "RC522", "Module/RC522", 8)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardDisplays =
    [
        new("LCD1602 16x2 CHARACTER LCD I2C MODULE (4-PIN)", "LCD1602-I2C", "Display/LCD1602_I2C", 4),
        new("0.96\" I2C 128x64 OLED DISPLAY MODULE (4-PIN)", "SSD1306-OLED", "Display_Graphic/SSD1306_I2C", 4),
        new("LCD2004 20x4 CHARACTER LCD I2C MODULE (4-PIN)", "LCD2004-I2C", "Display/LCD2004_I2C", 4)
    ];

    public static readonly IReadOnlyList<PartPreset> StandardBatteries =
    [
        new("9V ALKALINE BATTERY (6F22)", "9V", "Device/Battery", 2),
        new("5V / 3.3V MB102 BREADBOARD MODULE", "MB102", "Module/PowerSupply", 4),
        new("4x AA BATTERY PACK (6.0V)", "6V (4xAA)", "Device/Battery", 2),
        new("3V CR2032 LITHIUM COIN CELL", "CR2032 3V", "Device/Battery_Cell", 2),
        new("3.7V 18650 LI-ION CELL", "18650 3.7V", "Device/Battery_Cell", 2)
    ];
}
