using System.Linq;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public class BoxCodingAndProgrammerTests
{
    private static readonly DevBoardAndModuleRuleEngine BoardRules = new();
    private static readonly ElectricalRulesChecker ErcChecker = new();

    #region 1. BoxCodingModels, Palette Templates & Hierarchical Nesting

    [Fact]
    public void BoxBlockKind_NewKindsExistAndPreserveExistingOrdinals()
    {
        ((int)BoxBlockKind.WhenStarted).Should().Be(0);
        ((int)BoxBlockKind.ForeverLoop).Should().Be(1);
        ((int)BoxBlockKind.IfElseCondition).Should().Be(25);

        BoxBlockKind.MapRange.Should().NotBe(BoxBlockKind.MathOperation);
        BoxBlockKind.PlayToneNote.Should().NotBe(BoxBlockKind.SetBuzzer);
        BoxBlockKind.RgbLedColor.Should().NotBe(BoxBlockKind.SetRgbLed);
        BoxBlockKind.ReadUltrasonicCm.Should().NotBe(BoxBlockKind.ReadUltrasonic);
    }

    [Fact]
    public void BoxBlockItem_IsContainerBlock_IdentifiesContainersAccurately()
    {
        new BoxBlockItem { Kind = BoxBlockKind.IfCondition }.IsContainerBlock.Should().BeTrue();
        new BoxBlockItem { Kind = BoxBlockKind.IfElseCondition }.IsContainerBlock.Should().BeTrue();
        new BoxBlockItem { Kind = BoxBlockKind.RepeatTimes }.IsContainerBlock.Should().BeTrue();
        new BoxBlockItem { Kind = BoxBlockKind.WhileLoop }.IsContainerBlock.Should().BeTrue();

        new BoxBlockItem { Kind = BoxBlockKind.WhenStarted }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.ForeverLoop }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.SetLed }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.Delay }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.MapRange }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.PlayToneNote }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.RgbLedColor }.IsContainerBlock.Should().BeFalse();
        new BoxBlockItem { Kind = BoxBlockKind.ReadUltrasonicCm }.IsContainerBlock.Should().BeFalse();
    }

    [Fact]
    public void BoxCodeGenerator_GetPaletteTemplates_IncludesAllRequiredNewKinds()
    {
        var templates = BoxCodeGenerator.GetPaletteTemplates();

        templates.Should().NotBeNullOrEmpty();
        templates.Select(t => t.Kind).Should().Contain(new[]
        {
            BoxBlockKind.WhenStarted,
            BoxBlockKind.ForeverLoop,
            BoxBlockKind.IfCondition,
            BoxBlockKind.IfElseCondition,
            BoxBlockKind.MapRange,
            BoxBlockKind.PlayToneNote,
            BoxBlockKind.RgbLedColor,
            BoxBlockKind.ReadUltrasonicCm
        });

        templates.Should().OnlyContain(t =>
            !string.IsNullOrWhiteSpace(t.Title) &&
            !string.IsNullOrWhiteSpace(t.AccentHexColor));
    }

    [Theory]
    [InlineData(BoxBlockKind.IfElseCondition, MicrocontrollerBoardType.ArduinoUnoR3R4)]
    [InlineData(BoxBlockKind.MapRange, MicrocontrollerBoardType.ArduinoUnoR3R4)]
    [InlineData(BoxBlockKind.PlayToneNote, MicrocontrollerBoardType.Esp32DevKitV1)]
    [InlineData(BoxBlockKind.RgbLedColor, MicrocontrollerBoardType.Esp32DevKitV1)]
    [InlineData(BoxBlockKind.ReadUltrasonicCm, MicrocontrollerBoardType.ArduinoUnoR3R4)]
    public void BoxCodeGenerator_CreateConfiguredBlock_ConfiguresAllNewKindsForBoard(
        BoxBlockKind kind,
        MicrocontrollerBoardType board)
    {
        var block = BoxCodeGenerator.CreateConfiguredBlock(kind, board);

        block.Should().NotBeNull();
        block.Kind.Should().Be(kind);
        block.Title.Should().NotBeNullOrWhiteSpace();
        block.Clone().Kind.Should().Be(kind);
    }

    [Fact]
    public void GenerateCode_ArduinoCpp_NestedContainersEmitChildrenInsideBracesAndPassAnalyzer()
    {
        var blocks = new List<BoxBlockItem>
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, MicrocontrollerBoardType.ArduinoUnoR3R4),
            new BoxBlockItem
            {
                Kind = BoxBlockKind.ReadUltrasonicCm,
                Category = BoxBlockCategory.Sensors,
                Title = "STORE ULTRASONIC CM INTO VAR",
                Pin1 = "9",
                Pin2 = "10",
                VariableName = "distCm",
                IndentationLevel = 0
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.IfCondition,
                Category = BoxBlockCategory.Control,
                Title = "IF CONDITION",
                VariableName = "distCm",
                ConditionOperator = "<",
                ConditionThreshold = 20,
                IndentationLevel = 0
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.RepeatTimes,
                Category = BoxBlockCategory.Control,
                Title = "REPEAT TIMES",
                RepeatCount = 3,
                IndentationLevel = 1
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.SetLed,
                Category = BoxBlockCategory.Actuators,
                Title = "SET LED",
                Pin1 = "13",
                Parameter1 = "HIGH",
                IndentationLevel = 2
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.Delay,
                Category = BoxBlockCategory.Control,
                Title = "DELAY",
                Parameter1 = "150",
                IndentationLevel = 2
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.PlayToneNote,
                Category = BoxBlockCategory.Actuators,
                Title = "PLAY MUSICAL NOTE",
                Pin1 = "8",
                Parameter1 = "C5",
                Parameter2 = "120",
                IndentationLevel = 0
            }
        };

        string cpp = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, BoxCodingLanguage.ArduinoCpp);

        cpp.Should().Contain("if (distCm < 20.0) {");
        cpp.Should().Contain("        for (int loopIndex = 0; loopIndex < 3; loopIndex++) {");
        cpp.Should().Contain("            digitalWrite(");
        cpp.Should().Contain("            delay(150);");
        cpp.Should().Contain("\n    tone("); // Sibling after nested if returns to loop() top indent

        int openBraces = cpp.Count(c => c == '{');
        int closeBraces = cpp.Count(c => c == '}');
        openBraces.Should().Be(closeBraces);

        var report = BoxCodeErrorAnalyzer.Analyze(cpp, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);
        report.IsValid.Should().BeTrue(report.Diagnostics.FirstOrDefault()?.DisplaySummary ?? "should have no errors");
        report.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void GenerateCode_MicroPython_NestedContainersEmitProperIndentationAndPassAnalyzer()
    {
        var blocks = new List<BoxBlockItem>
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, MicrocontrollerBoardType.Esp32DevKitV1),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, MicrocontrollerBoardType.Esp32DevKitV1),
            new BoxBlockItem
            {
                Kind = BoxBlockKind.IfElseCondition,
                Category = BoxBlockCategory.Control,
                Title = "IF / ELSE CONDITION",
                VariableName = "sensorLevel",
                ConditionOperator = ">",
                ConditionThreshold = 500,
                IndentationLevel = 0
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.SetLed,
                Category = BoxBlockCategory.Actuators,
                Title = "SET LED",
                Pin1 = "2",
                Parameter1 = "HIGH",
                IndentationLevel = 1
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.Delay,
                Category = BoxBlockCategory.Control,
                Title = "DELAY",
                Parameter1 = "200",
                IndentationLevel = 0
            }
        };

        string py = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.Esp32DevKitV1, BoxCodingLanguage.MicroPython);

        py.Should().Contain("    if sensorLevel > 500.0:");
        py.Should().Contain("        led.value(1)");
        py.Should().Contain("    else:");
        py.Should().Contain("    time.sleep_ms(200)");

        var report = BoxCodeErrorAnalyzer.Analyze(py, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.Esp32DevKitV1);
        report.IsValid.Should().BeTrue(report.Diagnostics.FirstOrDefault()?.DisplaySummary ?? "should have no errors");
        report.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void GenerateCode_EmptyContainers_EmitSafeFallbackBodiesInCppAndPython()
    {
        var blocks = new List<BoxBlockItem>
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, MicrocontrollerBoardType.ArduinoUnoR3R4),
            new BoxBlockItem
            {
                Kind = BoxBlockKind.IfCondition,
                Category = BoxBlockCategory.Control,
                Title = "IF CONDITION",
                VariableName = "measuredDistanceCm",
                ConditionOperator = "<",
                ConditionThreshold = 15,
                IndentationLevel = 0
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.RepeatTimes,
                Category = BoxBlockCategory.Control,
                Title = "REPEAT TIMES",
                RepeatCount = 4,
                IndentationLevel = 0
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.WhileLoop,
                Category = BoxBlockCategory.Control,
                Title = "WHILE LOOP",
                VariableName = "buttonPressed",
                ConditionOperator = "==",
                ConditionThreshold = 1,
                IndentationLevel = 0
            }
        };

        string cpp = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, BoxCodingLanguage.ArduinoCpp);
        string py = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, BoxCodingLanguage.MicroPython);

        cpp.Should().Contain("// Add indented child blocks inside this block");
        py.Should().Contain("        pass");

        BoxCodeErrorAnalyzer.Analyze(cpp, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4)
            .ErrorCount.Should().Be(0);
        BoxCodeErrorAnalyzer.Analyze(py, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.ArduinoUnoR3R4)
            .ErrorCount.Should().Be(0);
    }

    [Fact]
    public void GenerateCode_AllFiveNewBlockKinds_ProduceValidAnalyzableCppAndMicroPython()
    {
        var blocks = new List<BoxBlockItem>
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadUltrasonicCm, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.MapRange, MicrocontrollerBoardType.ArduinoUnoR3R4),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.IfElseCondition, MicrocontrollerBoardType.ArduinoUnoR3R4),
            new BoxBlockItem
            {
                Kind = BoxBlockKind.RgbLedColor,
                Category = BoxBlockCategory.Actuators,
                Title = "SET RGB LED (R,G,B)",
                Pin1 = "9",
                Pin2 = "10",
                SecondaryVariableName = "11",
                Parameter1 = "255,128,64",
                IndentationLevel = 1
            },
            new BoxBlockItem
            {
                Kind = BoxBlockKind.PlayToneNote,
                Category = BoxBlockCategory.Actuators,
                Title = "PLAY MUSICAL NOTE",
                Pin1 = "8",
                Parameter1 = "A4",
                Parameter2 = "200",
                IndentationLevel = 0
            }
        };

        string cpp = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.ArduinoCpp);
        string py = BoxCodeGenerator.GenerateCode(blocks, MicrocontrollerBoardType.ArduinoUnoR3R4, TargetProgrammingLanguage.MicroPython);

        cpp.Should().Contain("map(");
        cpp.Should().Contain("tone(");
        cpp.Should().Contain("else {");

        var cppReport = BoxCodeErrorAnalyzer.Analyze(cpp, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);
        var pyReport = BoxCodeErrorAnalyzer.Analyze(py, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.ArduinoUnoR3R4);

        cppReport.ErrorCount.Should().Be(0, string.Join(" | ", cppReport.Diagnostics.Select(d => d.DisplaySummary)));
        pyReport.ErrorCount.Should().Be(0, string.Join(" | ", pyReport.Diagnostics.Select(d => d.DisplaySummary)));
    }

    #endregion

    #region 2. BoxCodeErrorAnalyzer Static Syntax & Hardware Pin Checks

    [Fact]
    public void Analyze_EmptyCode_ReturnsBC001()
    {
        var report = BoxCodeErrorAnalyzer.Analyze("   \r\n ", BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.ErrorCount.Should().BeGreaterThan(0);
        report.Diagnostics.Should().Contain(d => d.Code == "BC001" && d.Severity == BoxCodeDiagnosticSeverity.Error);
    }

    [Fact]
    public void Analyze_UnbalancedDelimiters_ReturnsBC002WithLineAndColumn()
    {
        const string code = """
            void setup() {
                pinMode(13, OUTPUT);
            }
            void loop() {
                digitalWrite(13, HIGH);
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Should().Contain(d => d.Code == "BC002" && d.Line > 0 && d.Column > 0);
    }

    [Fact]
    public void Analyze_UnclosedStringLiteral_ReturnsBC003()
    {
        const string code = """
            void setup() {
                Serial.begin(9600);
            }
            void loop() {
                Serial.println("Hello World);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Should().Contain(d => d.Code == "BC003");
    }

    [Fact]
    public void Analyze_CppMissingSetupOrLoop_ReturnsBC010()
    {
        const string code = """
            void setup() {
                pinMode(13, OUTPUT);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Should().Contain(d => d.Code == "BC010");
    }

    [Fact]
    public void Analyze_CppMissingSemicolon_ReturnsBC011WithoutFlaggingCommentsOrHeaders()
    {
        const string code = """
            #define LED_PIN 13
            // This is a comment without semicolon
            void setup() {
                pinMode(LED_PIN, OUTPUT);
            }
            void loop() {
                if (true) {
                    digitalWrite(LED_PIN, HIGH)
                }
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Should().ContainSingle(d => d.Code == "BC011");
        report.Diagnostics.First(d => d.Code == "BC011").Line.Should().Be(8);
    }

    [Fact]
    public void Analyze_CppMisspelledArduinoApi_ReturnsBC012()
    {
        const string code = """
            void setup() {
                pinmode(13, OUTPUT);
                Seral.begin(9600);
            }
            void loop() {
                digtalWrite(13, HIGH);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Count(d => d.Code == "BC012").Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void Analyze_MicroPythonSyntaxErrors_DetectsBC020_BC021_BC022()
    {
        const string missingColon = """
            while True
                pass
            """;
        var r20 = BoxCodeErrorAnalyzer.Analyze(missingColon, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.Esp32DevKitV1);
        r20.Diagnostics.Should().Contain(d => d.Code == "BC020");

        const string badIndent = """
            while True:
            print("Not indented")
            """;
        var r21 = BoxCodeErrorAnalyzer.Analyze(badIndent, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.Esp32DevKitV1);
        r21.Diagnostics.Should().Contain(d => d.Code == "BC021");

        const string cppInPython = """
            void setup() {
                digitalWrite(13, HIGH);
            }
            """;
        var r22 = BoxCodeErrorAnalyzer.Analyze(cppInPython, BoxCodingLanguage.MicroPython, MicrocontrollerBoardType.Esp32DevKitV1);
        r22.Diagnostics.Should().Contain(d => d.Code == "BC022");
    }

    [Fact]
    public void Analyze_Esp32HardwarePinViolations_DetectsInputOnlySpiFlashAndRange()
    {
        const string badPins = """
            void setup() {
                pinMode(34, OUTPUT);
                pinMode(6, OUTPUT);
                pinMode(45, OUTPUT);
            }
            void loop() {
                digitalWrite(34, HIGH);
                digitalWrite(6, LOW);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(badPins, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.Esp32DevKitV1);

        report.IsValid.Should().BeFalse();
        report.Diagnostics.Should().Contain(d => d.Code == "BC030" && d.Message.Contains("34"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC031" && d.Message.Contains("6"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC032" && d.Message.Contains("45"));
    }

    [Fact]
    public void Analyze_UnoPinOutOfRange_MissingPinMode_AndUnwiredPinWarnings()
    {
        const string code = """
            void setup() {
                pinMode(12, OUTPUT);
            }
            void loop() {
                digitalWrite(13, HIGH);
                digitalWrite(12, LOW);
                digitalWrite(24, HIGH);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(
            code,
            BoxCodingLanguage.ArduinoCpp,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            wiredMcuPins: new[] { "13" });

        report.Diagnostics.Should().Contain(d => d.Code == "BC032" && d.Severity == BoxCodeDiagnosticSeverity.Error);
        report.Diagnostics.Should().Contain(d => d.Code == "BC033" && d.Severity == BoxCodeDiagnosticSeverity.Warning && d.Message.Contains("13"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC034" && d.Severity == BoxCodeDiagnosticSeverity.Warning && d.Message.Contains("12"));
    }

    [Fact]
    public void Analyze_ValidCode_ExtractsSimulatedPinActions()
    {
        const string code = """
            void setup() {
                Serial.begin(115200);
                pinMode(13, OUTPUT);
                pinMode(9, OUTPUT);
            }
            void loop() {
                digitalWrite(13, HIGH);
                analogWrite(9, 180);
                tone(8, 880);
                lcd.print("Voltage OK");
                Serial.println("Heartbeat");
                delay(250);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.ErrorCount.Should().Be(0);
        report.ExtractedActions.Should().Contain(a => a.PinName == "13" && a.IsOutput && a.IsDigitalHigh);
        report.ExtractedActions.Should().Contain(a => a.PinName == "9" && a.IsPwm && a.PwmDuty == 180);
        report.ExtractedActions.Should().Contain(a => a.BuzzerHz == 880);
        report.ExtractedActions.Should().Contain(a => a.LcdLine1 == "Voltage OK");
        report.ExtractedActions.Should().Contain(a => a.SerialText == "Heartbeat");
        report.ExtractedActions.Should().Contain(a => a.DelayMs == 250);
    }

    #endregion

    #region 3. ProgrammerCatalog & VirtualFirmwareSimulator

    [Fact]
    public void ProgrammerCatalog_CoversAllCablesRecommendedBoardsAndCompatibilityNotes()
    {
        ProgrammerCatalog.AllCables.Should().HaveCount(3);

        ProgrammerCatalog.GetRecommendedCable(MicrocontrollerBoardType.ArduinoUnoR3R4).Should().Be(UsbCableKind.UsbB);
        ProgrammerCatalog.GetRecommendedCable(MicrocontrollerBoardType.ArduinoMega2560).Should().Be(UsbCableKind.UsbB);
        ProgrammerCatalog.GetRecommendedCable(MicrocontrollerBoardType.Esp32DevKitV1).Should().Be(UsbCableKind.MicroUsb);
        ProgrammerCatalog.GetRecommendedCable(MicrocontrollerBoardType.Stm32BluePillF103C8).Should().Be(UsbCableKind.MicroUsb);
        ProgrammerCatalog.GetRecommendedCable(MicrocontrollerBoardType.WemosEsp32S2Mini).Should().Be(UsbCableKind.UsbTypeC);

        ProgrammerCatalog.IsCableCompatible(UsbCableKind.None, MicrocontrollerBoardType.ArduinoUnoR3R4, out _, out _)
            .Should().BeFalse();

        ProgrammerCatalog.IsCableCompatible(UsbCableKind.UsbB, MicrocontrollerBoardType.ArduinoUnoR3R4, out bool nativeUno, out _)
            .Should().BeTrue();
        nativeUno.Should().BeTrue();

        ProgrammerCatalog.IsCableCompatible(UsbCableKind.UsbTypeC, MicrocontrollerBoardType.ArduinoUnoR3R4, out bool nativeAdapter, out string note)
            .Should().BeTrue();
        nativeAdapter.Should().BeFalse();
        note.Should().Contain("adapter");

        string portDesc = ProgrammerCatalog.GetBoardPortDescription(MicrocontrollerBoardType.Esp32DevKitV1, UsbCableKind.MicroUsb, "COM7");
        portDesc.Should().Contain("COM7");
    }

    [Fact]
    public void VirtualFirmwareSimulator_BlinkPwmLcdBuzzerSerial_ExecutesAcrossTicksAndBuilds64Samples()
    {
        const string blinkCode = """
            void setup() {
                Serial.begin(115200);
                pinMode(13, OUTPUT);
                pinMode(9, OUTPUT);
            }
            void loop() {
                digitalWrite(13, HIGH);
                delay(500);
                digitalWrite(13, LOW);
                delay(500);
                analogWrite(9, 128);
                tone(8, 1000);
                lcd.print("Temp: 24.5C");
                Serial.println("Tick running");
            }
            """;

        var stateEven = VirtualFirmwareSimulator.SimulateFromCode(
            blinkCode,
            BoxCodingLanguage.ArduinoCpp,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            UsbCableKind.UsbB,
            tickIndex: 0,
            userSerialInputs: new[] { "STATUS?" });

        var stateOdd = VirtualFirmwareSimulator.SimulateFromCode(
            blinkCode,
            BoxCodingLanguage.ArduinoCpp,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            UsbCableKind.UsbB,
            tickIndex: 1,
            userSerialInputs: new[] { "STATUS?" });

        stateEven.IsRunning.Should().BeTrue();
        stateEven.DigitalPinStates.Should().ContainKey("13");
        stateOdd.DigitalPinStates.Should().ContainKey("13");
        stateEven.DigitalPinStates["13"].Should().NotBe(stateOdd.DigitalPinStates["13"]);

        stateEven.PwmPinValues.Should().ContainKey("9").WhoseValue.Should().Be(128);
        stateEven.IsBuzzerActive.Should().BeTrue();
        stateEven.BuzzerFrequencyHz.Should().Be(1000);
        stateEven.LcdLine1.Should().Contain("Temp: 24.5C");
        stateEven.SerialMonitorLines.Should().Contain(l => l.Contains("Tick running"));
        stateEven.SerialMonitorLines.Should().Contain(l => l.Contains("STATUS?"));
        stateEven.WaveformSamples.Should().HaveCount(64);
    }

    [Fact]
    public void VirtualFirmwareSimulator_InvalidCode_HaltsSafely()
    {
        var state = VirtualFirmwareSimulator.SimulateFromCode(
            "void loop() { bad syntax }",
            BoxCodingLanguage.ArduinoCpp,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            UsbCableKind.UsbB,
            tickIndex: 0);

        state.IsRunning.Should().BeFalse();
        state.SerialMonitorLines.Should().NotBeEmpty();
    }

    #endregion

    #region 4. ExperimentalLabEngine: AutoFix, Prompt Builder & Thermal Hotspots

    [Fact]
    public void ExperimentalLabEngine_TryAutoFixViolations_RemovesDirectVccGndShort()
    {
        var draft = new CircuitDraft();
        draft.TryAdd(ComponentKind.JumperWire, "TOP_VCC_1", "TOP_GND_1", "Jumper", out _, out _).Should().BeTrue();
        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());

        bool fixedAny = ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out string summary);

        fixedAny.Should().BeTrue();
        fixedCount.Should().BeGreaterOrEqualTo(1);
        summary.Should().NotBeNullOrWhiteSpace();
        draft.Parts.Should().BeEmpty();

        var after = ErcChecker.EvaluateBoard(2, 0, draft.BuildDetections());
        after.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcVccGndDirectShort);
    }

    [Fact]
    public void ExperimentalLabEngine_TryAutoFixViolations_Inserts220RResistorForUnprotectedLed()
    {
        var draft = new CircuitDraft();
        draft.TryAdd(ComponentKind.Led, "TOP_VCC_5", "TOP_GND_5", "RED 2.0V", out _, out _).Should().BeTrue();
        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());

        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcResistorCurrentLimitLow);

        bool fixedAny = ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out _);

        fixedAny.Should().BeTrue();
        fixedCount.Should().BeGreaterOrEqualTo(1);
        draft.Parts.Should().Contain(p => p.Kind == ComponentKind.Resistor && p.Value == "220R");

        var after = ErcChecker.EvaluateBoard(2, 0, draft.BuildDetections());
        after.Violations.Should().NotContain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitLow ||
            v.Code == ViolationCode.ErcVccGndDirectShort);
    }

    [Fact]
    public void ExperimentalLabEngine_TryAutoFixViolations_FlipsReversedPolarityLed()
    {
        var draft = new CircuitDraft();
        // Safe resistor on col 10
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_10", "A10", "220R", out _, out _).Should().BeTrue();
        // Reversed LED: Pins[0]=anode on GND, Pins[1]=cathode on B10 (connected to VCC resistor)
        draft.TryAdd(ComponentKind.Led, "TOP_GND_10", "B10", "RED 2.0V", out var led, out _).Should().BeTrue();

        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());
        bool fixedAny = ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out _);

        fixedAny.Should().BeTrue();
        fixedCount.Should().BeGreaterOrEqualTo(1);

        var updatedLed = draft.Parts.Single(p => p.TrackId == led.TrackId);
        updatedLed.Pins[0].Should().Be("B10");
        updatedLed.Pins[1].Should().Be("TOP_GND_10");

        var after = ErcChecker.EvaluateBoard(2, 0, draft.BuildDetections());
        after.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcReversedPolarity);
    }

    [Fact]
    public void ExperimentalLabEngine_TryAutoFixViolations_FixesReversedPolaritySensorPower()
    {
        var draft = new CircuitDraft();
        draft.TryAddFootprint(ComponentKind.Sensor, new[] { "E10", "E11", "E12", "E13" }, "HC-SR04", "HC-SR04", out _, out _).Should().BeTrue();
        // Reversed power: VCC pin 1 (E10) -> GND, GND pin 4 (E13) -> VCC
        draft.TryAdd(ComponentKind.JumperWire, "A10", "TOP_GND_10", "BadVCC", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.JumperWire, "A13", "TOP_VCC_13", "BadGND", out _, out _).Should().BeTrue();

        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());
        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcReversedPolarity);

        bool fixedAny = ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out _);

        fixedAny.Should().BeTrue();
        fixedCount.Should().BeGreaterOrEqualTo(1);

        var after = ErcChecker.EvaluateBoard(2, 0, draft.BuildDetections());
        after.Violations.Should().NotContain(v => v.Code == ViolationCode.ErcReversedPolarity);
    }

    [Fact]
    public void ExperimentalLabEngine_TryAutoFixViolations_CleanCircuitReturnsFalse()
    {
        var draft = new CircuitDraft();
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_5", "A5", "220R", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Led, "B5", "TOP_GND_5", "RED 2.0V", out _, out _).Should().BeTrue();

        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());
        bool fixedAny = ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out string summary);

        fixedAny.Should().BeFalse();
        fixedCount.Should().Be(0);
        summary.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Build an ultrasonic HC-SR04 distance radar with buzzer and LED", ComponentKind.Sensor)]
    [InlineData("Create a DHT11 temperature and humidity weather monitor with LCD", ComponentKind.Sensor)]
    [InlineData("Make a traffic light with red yellow and green LEDs", ComponentKind.Led)]
    [InlineData("Build a 555 timer LED astable blinker circuit", ComponentKind.IntegratedCircuit)]
    [InlineData("Doorbell alarm with push button and buzzer", ComponentKind.PushButton)]
    [InlineData("Starter desk indicator gadget", ComponentKind.Led)]
    public void ExperimentalLabEngine_BuildCircuitFromPrompt_BuildsCleanCircuitsAndValidPrograms(
        string prompt,
        ComponentKind expectedPartKind)
    {
        var draft = new CircuitDraft();

        bool ok = ExperimentalLabEngine.BuildCircuitFromPrompt(
            prompt,
            draft,
            out MicrocontrollerBoardType board,
            out List<BoxBlockItem> blocks,
            out string summary);

        ok.Should().BeTrue();
        summary.Should().NotBeNullOrWhiteSpace();
        board.Should().NotBe(MicrocontrollerBoardType.None);
        draft.Parts.Should().Contain(p => p.Kind == expectedPartKind);
        blocks.Should().NotBeEmpty();

        string cpp = BoxCodeGenerator.GenerateCode(blocks, board, BoxCodingLanguage.ArduinoCpp);
        string py = BoxCodeGenerator.GenerateCode(blocks, board, BoxCodingLanguage.MicroPython);

        BoxCodeErrorAnalyzer.Analyze(cpp, BoxCodingLanguage.ArduinoCpp, board).ErrorCount.Should().Be(0);
        BoxCodeErrorAnalyzer.Analyze(py, BoxCodingLanguage.MicroPython, board).ErrorCount.Should().Be(0);

        var erc = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());
        erc.Violations.Should().NotContain(v => v.Severity == DrcSeverity.Critical,
            $"prompt '{prompt}' generated critical DRC violations: " +
            string.Join(" | ", erc.Violations.Select(v => v.TechnicalMessage)));
    }

    [Fact]
    public void ExperimentalLabEngine_BuildCircuitFromPrompt_RejectsBlankPrompt()
    {
        var draft = new CircuitDraft();

        bool ok = ExperimentalLabEngine.BuildCircuitFromPrompt(
            "   ",
            draft,
            out _,
            out var blocks,
            out _);

        ok.Should().BeFalse();
        blocks.Should().BeEmpty();
        draft.Parts.Should().BeEmpty();
    }

    [Fact]
    public void ExperimentalLabEngine_ComputeThermalHotspots_ScoresOverloadedNormalAndPassiveParts()
    {
        var draft = new CircuitDraft();
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_2", "A2", "10R", out var lowResistor, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_8", "A8", "220R", out var safeResistor, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.JumperWire, "A15", "B16", "Jumper", out var passiveWire, out _).Should().BeTrue();

        var packet = ErcChecker.EvaluateBoard(1, 0, draft.BuildDetections());
        var hotspots = ExperimentalLabEngine.ComputeThermalHotspots(draft.Parts, packet, draft.Matrix);

        hotspots.Should().HaveCount(3);

        var hot = hotspots.Single(h => h.TrackId == lowResistor.TrackId);
        hot.IsOverloaded.Should().BeTrue();
        hot.NormalizedHeat01.Should().BeGreaterOrEqualTo(0.85f);
        hot.EstimatedCurrentMa.Should().BeGreaterThan(100);

        var normal = hotspots.Single(h => h.TrackId == safeResistor.TrackId);
        normal.IsOverloaded.Should().BeFalse();
        normal.NormalizedHeat01.Should().BeInRange(0.20f, 0.60f);

        var wire = hotspots.Single(h => h.TrackId == passiveWire.TrackId);
        wire.IsOverloaded.Should().BeFalse();
        wire.NormalizedHeat01.Should().BeApproximately(0.10f, 0.05f);
    }

    #endregion
}
