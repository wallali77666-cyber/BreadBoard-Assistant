// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/BoxCodingTests.cs
// PURPOSE: Unit tests for Scratch-style visual Box Coding generator:
//          - Arduino C++ (.ino) generation for dev boards & sensors
//          - MicroPython (.py) generation for ESP32 & sensors
//          - Auto-detection of blocks from circuit draft components
//          - 16x2 I2C Character LCD simulation and code generation
//          - Logic and variable manipulation blocks
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class BoxCodingTests
{
    [Fact]
    public void GenerateCode_ArduinoCpp_produces_compilable_skeleton_with_dht_and_servo()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.WhenStarted, Category = BoxBlockCategory.Events, Title = "Start" },
            new() { Kind = BoxBlockKind.ReadDhtSensor, Category = BoxBlockCategory.Sensors, Pin1 = "D2" },
            new() { Kind = BoxBlockKind.SetServo, Category = BoxBlockCategory.Actuators, Pin1 = "D9", Parameter1 = "120" },
            new() { Kind = BoxBlockKind.Delay, Category = BoxBlockCategory.Control, Parameter1 = "1000" }
        };

        string code = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);

        code.Should().Contain("#include <DHT.h>");
        code.Should().Contain("#include <Servo.h>");
        code.Should().Contain("#define PIN_DHT 2");
        code.Should().Contain("#define PIN_SERVO 9");
        code.Should().Contain("void setup()");
        code.Should().Contain("Serial.begin(115200);");
        code.Should().Contain("dht.begin();");
        code.Should().Contain("servoMotor.attach(PIN_SERVO);");
        code.Should().Contain("void loop()");
        code.Should().Contain("dht.readTemperature()");
        code.Should().Contain("servoMotor.write(120);");
        code.Should().Contain("delay(1000);");
    }

    [Fact]
    public void GenerateCode_MicroPython_produces_valid_syntax_for_esp32_with_ultrasonic_and_led()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.WhenStarted, Category = BoxBlockCategory.Events, Title = "Start" },
            new() { Kind = BoxBlockKind.ReadUltrasonic, Category = BoxBlockCategory.Sensors, Pin1 = "GPIO5", Pin2 = "GPIO18" },
            new() { Kind = BoxBlockKind.SetLed, Category = BoxBlockCategory.Actuators, Pin1 = "GPIO2", Parameter1 = "HIGH" },
            new() { Kind = BoxBlockKind.IfCondition, Category = BoxBlockCategory.Control, ConditionOperator = "<", ConditionThreshold = 15.0 },
            new() { Kind = BoxBlockKind.Delay, Category = BoxBlockCategory.Control, Parameter1 = "250" }
        };

        string code = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, TargetProgrammingLanguage.MicroPython);

        code.Should().Contain("from machine import Pin, ADC, PWM");
        code.Should().Contain("import time");
        code.Should().Contain("trig = Pin(5, Pin.OUT)");
        code.Should().Contain("echo = Pin(18, Pin.IN)");
        code.Should().Contain("led = Pin(2, Pin.OUT)");
        code.Should().Contain("while True:");
        code.Should().Contain("time_pulse_us(echo, 1, 30000)");
        code.Should().Contain("distance_cm < 15.0:");
        code.Should().Contain("time.sleep_ms(250)");
    }

    [Fact]
    public void AutoDetectFromCircuit_generates_blocks_matching_placed_components()
    {
        var draftParts = new List<DraftPart>
        {
            new(TrackId: 1, Kind: ComponentKind.Led, Pins: new[] { "A10", "A11" }, Value: "Red LED"),
            new(TrackId: 2, Kind: ComponentKind.Sensor, Pins: new[] { "B5", "B6", "B7", "B8" }, Value: "HC-SR04"),
            new(TrackId: 3, Kind: ComponentKind.Buzzer, Pins: new[] { "C20", "C21" }, Value: "Active Buzzer"),
            new(TrackId: 4, Kind: ComponentKind.Potentiometer, Pins: new[] { "E1", "E2", "E3" }, Value: "10k POT")
        };

        var blocks = BoxCodeGenerator.AutoDetectFromCircuit(draftParts, MicrocontrollerBoardType.ArduinoUnoR3R4);

        blocks.Should().Contain(b => b.Kind == BoxBlockKind.SetLed);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.ReadUltrasonic);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.SetBuzzer);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.ReadPotentiometer);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.SerialPrint);
    }

    [Fact]
    public void Disabled_blocks_are_excluded_from_code_generation()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.SetLed, Pin1 = "D13", Parameter1 = "HIGH", IsEnabled = true },
            new() { Kind = BoxBlockKind.SetRelay, Pin1 = "D7", Parameter1 = "HIGH", IsEnabled = false }
        };

        string code = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);

        code.Should().Contain("PIN_LED");
        code.Should().NotContain("PIN_RELAY");
    }

    [Fact]
    public void GenerateCode_OledDisplay_and_Mpu6050_generates_wire_and_display_code()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.WhenStarted, Category = BoxBlockCategory.Events },
            new() { Kind = BoxBlockKind.ReadMpu6050, Category = BoxBlockCategory.Sensors },
            new() { Kind = BoxBlockKind.DisplayOledI2c, Category = BoxBlockCategory.Displays, Parameter1 = "Distance 12cm" }
        };

        string inoCode = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);
        inoCode.Should().Contain("#include <Wire.h>");
        inoCode.Should().Contain("#include <Adafruit_SSD1306.h>");
        inoCode.Should().Contain("oledDisplay.begin(SSD1306_SWITCHCAPVCC, 0x3C)");
        inoCode.Should().Contain("oledDisplay.println(F(\"Distance 12cm\"))");
        inoCode.Should().Contain("mpuAccelX");

        string pyCode = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, TargetProgrammingLanguage.MicroPython);
        pyCode.Should().Contain("from machine import I2C");
        pyCode.Should().Contain("import ssd1306");
        pyCode.Should().Contain("oled.show()");
    }

    [Fact]
    public void BoardPinProvider_returns_authentic_pins_for_esp32_and_uno()
    {
        var espPins = BoardPinProvider.GetDigitalPins(MicrocontrollerBoardType.Esp32DevKitV1);
        espPins.Should().Contain("GPIO 2");
        espPins.Should().Contain("GPIO 21");

        var unoPins = BoardPinProvider.GetDigitalPins(MicrocontrollerBoardType.ArduinoUnoR3R4);
        unoPins.Should().Contain("D2");
        unoPins.Should().Contain("D13");

        var unoAnalog = BoardPinProvider.GetAnalogPins(MicrocontrollerBoardType.ArduinoUnoR3R4);
        unoAnalog.Should().Contain("A0");
        unoAnalog.Should().Contain("A5");
    }

    [Fact]
    public void AutoDetectFromCircuit_With_Dht11_And_Lcd1602_Produces_Complete_Interactive_Loop()
    {
        var draftParts = new List<DraftPart>
        {
            new(TrackId: 1, Kind: ComponentKind.Sensor, Pins: new[] { "A1", "A2", "A3", "A4" }, Value: "DHT11"),
            new(TrackId: 2, Kind: ComponentKind.Sensor, Pins: new[] { "B1", "B2", "B3", "B4" }, Value: "LCD1602-I2C")
        };

        var blocks = BoxCodeGenerator.AutoDetectFromCircuit(draftParts, MicrocontrollerBoardType.ArduinoUnoR3R4);

        blocks.Should().Contain(b => b.Kind == BoxBlockKind.WhenStarted);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.LcdInitI2c);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.ForeverLoop);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.ReadDhtSensor);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.SerialPrint);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.LcdPrintSensor);
        blocks.Should().Contain(b => b.Kind == BoxBlockKind.Delay);

        string cppCode = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);
        cppCode.Should().Contain("#include <Wire.h>");
        cppCode.Should().Contain("#include <LiquidCrystal_I2C.h>");
        cppCode.Should().Contain("#include <DHT.h>");
        cppCode.Should().Contain("LiquidCrystal_I2C lcd(0x27, 16, 2);");
        cppCode.Should().Contain("lcd.init();");
        cppCode.Should().Contain("lcd.backlight();");
        cppCode.Should().Contain("dht.readTemperature();");
        cppCode.Should().Contain("lcd.setCursor(0, 0);");

        string pyCode = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, TargetProgrammingLanguage.MicroPython);
        pyCode.Should().Contain("I2cLcd");
        pyCode.Should().Contain("lcd.move_to(0, 0)");
        pyCode.Should().Contain("lcd.putstr(f\"Temp: {temp}C\")");
    }

    [Fact]
    public void BoxCoding_LcdPrintText_Generates_SetCursor_And_Print()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.WhenStarted },
            new()
            {
                Kind = BoxBlockKind.LcdPrintText,
                LcdRow = 1,
                LcdCol = 4,
                Parameter1 = "Hello World!"
            }
        };

        string cpp = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);
        cpp.Should().Contain("lcd.setCursor(4, 1);");
        cpp.Should().Contain("lcd.print(F(\"Hello World!\"));");

        string py = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, TargetProgrammingLanguage.MicroPython);
        py.Should().Contain("lcd.move_to(4, 1)");
        py.Should().Contain("lcd.putstr(\"Hello World!\")");
    }

    [Fact]
    public void BoxCoding_LogicBlocks_CompareCondition_And_SetVariable_Generate_Valid_Code()
    {
        var blocks = new List<BoxBlockItem>
        {
            new() { Kind = BoxBlockKind.WhenStarted },
            new()
            {
                Kind = BoxBlockKind.CompareCondition,
                VariableName = "currentTemperature",
                ConditionOperator = ">=",
                ConditionThreshold = 28.0
            },
            new()
            {
                Kind = BoxBlockKind.SetVariable,
                VariableName = "fanActive",
                Parameter1 = "1"
            }
        };

        string cpp = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);
        cpp.Should().Contain("if (currentTemperature >= 28.0)");
        cpp.Should().Contain("fanActive = 1;");

        string py = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, TargetProgrammingLanguage.MicroPython);
        py.Should().Contain("if currentTemperature >= 28.0:");
        py.Should().Contain("fanActive = 1");
    }

    [Fact]
    public void ComponentCatalog_StandardSensors_Contains_Lcd1602I2c()
    {
        ComponentCatalog.StandardSensors.Should().Contain(p => p.Value == "LCD1602-I2C" && p.PinCount == 4);
    }

    [Fact]
    public void OverlayLayerSettings_ShowLcdSimulation_Default_And_Live_Text()
    {
        var settings = new OverlayLayerSettings();
        settings.ShowLcdSimulation.Should().BeTrue();
        settings.LcdSimulatedRow0.Should().NotBeNullOrWhiteSpace();
        settings.LcdSimulatedRow1.Should().NotBeNullOrWhiteSpace();
    }
}
