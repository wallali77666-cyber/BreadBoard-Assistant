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

    #region 4. IDE Options, Semantic Analyzer Fixes & VS Code Debugger

    [Fact]
    public void ProgrammerCatalog_PhysicalCableStyles_And_StrictAdapterMismatchMode()
    {
        var typeC = ProgrammerCatalog.GetCableSpec(UsbCableKind.UsbTypeC);
        var typeB = ProgrammerCatalog.GetCableSpec(UsbCableKind.UsbB);

        typeC.ShellStyleId.Should().Be("anker_100w_braided");
        typeC.DisplayName.Should().Contain("Anker 100W");
        typeB.ShellStyleId.Should().Be("black_orange_emblem");
        typeB.DisplayName.Should().Contain("Orange");

        ProgrammerCatalog.RequiresAdapter(UsbCableKind.UsbTypeC, MicrocontrollerBoardType.ArduinoUnoR3R4).Should().BeTrue();
        ProgrammerCatalog.RequiresAdapter(UsbCableKind.UsbB, MicrocontrollerBoardType.ArduinoUnoR3R4).Should().BeFalse();

        // Adapter OFF -> plug mismatch blocked cleanly
        ProgrammerCatalog.IsCableCompatible(
            UsbCableKind.UsbTypeC,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            adapterModeEnabled: false,
            out bool isNativeOff,
            out string noteOff).Should().BeFalse();
        isNativeOff.Should().BeFalse();
        noteOff.Should().Contain("Plug mismatch");

        // Adapter ON -> non-native cable allowed with explicit OTG/adapter notice
        ProgrammerCatalog.IsCableCompatible(
            UsbCableKind.UsbTypeC,
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            adapterModeEnabled: true,
            out bool isNativeOn,
            out string noteOn).Should().BeTrue();
        isNativeOn.Should().BeFalse();
        noteOn.Should().Contain("adapter");
    }

    [Fact]
    public void ProgrammerIdeCatalog_ExtractsComPort_BuildsFqbn_And_ParsesCompilerOutput()
    {
        ProgrammerIdeCatalog.ExtractRawComPort("COM7 (USB-SERIAL CH340)").Should().Be("COM7");
        ProgrammerIdeCatalog.ExtractRawComPort("COM14").Should().Be("COM14");
        ProgrammerIdeCatalog.ExtractRawComPort("VIRTUAL-SIM (Live Breadboard Execution)").Should().BeNull();
        ProgrammerIdeCatalog.IsVirtualPort("VIRTUAL-SIM (Live Breadboard Execution)").Should().BeTrue();
        ProgrammerIdeCatalog.IsVirtualPort("COM7 (USB-UART)").Should().BeFalse();

        var s2Opts = new ArduinoUploadOptions(
            BoardType: MicrocontrollerBoardType.WemosEsp32S2Mini,
            BoardVariantId: "lolin_s2_mini",
            CustomFqbnOverride: null,
            UploadSpeedBaud: 921600,
            FlashMode: "qio",
            FlashSize: "4M",
            PartitionScheme: "huge_app",
            CpuFrequencyMHz: 240,
            UsbCdcOnBoot: true,
            ProgrammerToolId: "esptool",
            VerifyOnly: false,
            UploadUsingProgrammer: false,
            VerboseOutput: true);

        string s2Fqbn = ProgrammerIdeCatalog.BuildEffectiveFqbn(MicrocontrollerBoardType.WemosEsp32S2Mini, s2Opts);
        s2Fqbn.Should().Contain("esp32:esp32:lolin_s2_mini");
        s2Fqbn.Should().Contain("UploadSpeed=921600");
        s2Fqbn.Should().Contain("FlashMode=qio");
        s2Fqbn.Should().Contain("CDCOnBoot=cdc");

        string[] compileArgs = ProgrammerIdeCatalog.BuildCompileArguments(s2Fqbn, @"C:\tmp\sketch", s2Opts.VerboseOutput);
        compileArgs.Should().Contain("compile").And.Contain("--fqbn").And.Contain("-v");

        string? rawCom = ProgrammerIdeCatalog.ExtractRawComPort("COM7 (ESP32-S2)");
        string[] uploadArgs = ProgrammerIdeCatalog.BuildUploadArguments(s2Fqbn, @"C:\tmp\sketch", rawCom, s2Opts);
        uploadArgs.Should().Contain("-p").And.Contain("COM7").And.NotContain(a => a.Contains("ESP32-S2"));

        var mpyOpts = s2Opts with { MicroPythonTool = "mpremote" };
        var (exe, mpyArgs) = ProgrammerIdeCatalog.BuildMicroPythonTransferCommand(
            mpyOpts.MicroPythonTool,
            ProgrammerIdeCatalog.ExtractRawComPort("COM12 (MicroPython)")!,
            @"C:\tmp\main.py",
            115200);
        exe.Should().Be("mpremote");
        mpyArgs.Should().Contain("connect").And.Contain("COM12").And.Contain("cp");

        var cliDiags = ProgrammerIdeCatalog.ParseArduinoCliDiagnostics(
            @"C:\tmp\sketch\sketch.ino:14:5: error: 'myUnkVar' was not declared in this scope");
        cliDiags.Should().ContainSingle(d => d.Line == 14 && d.Column == 5 && d.Severity == BoxCodeDiagnosticSeverity.Error);

        var pyDiags = ProgrammerIdeCatalog.ParsePythonSyntaxDiagnostics(
            "  File \"main.py\", line 7\nSyntaxError: invalid syntax");
        pyDiags.Should().ContainSingle(d => d.Line == 7 && d.Severity == BoxCodeDiagnosticSeverity.Error);
    }

    [Fact]
    public void Analyzer_S2MiniGpio6To11Valid_LogicErrorsDetected_And_QuickFixesApplyCleanly()
    {
        // Wemos ESP32-S2 Mini GPIO 6-11 are valid general/USB/ADC pins (must NOT trigger BC031 SPI flash false positive)
        const string s2Code = """
            void setup() {
                Serial.begin(115200);
                pinMode(7, OUTPUT);
                pinMode(11, OUTPUT);
            }
            void loop() {
                digitalWrite(7, HIGH);
                digitalWrite(11, LOW);
                delay(200);
            }
            """;

        var s2Report = BoxCodeErrorAnalyzer.Analyze(s2Code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.WemosEsp32S2Mini);
        s2Report.Diagnostics.Should().NotContain(d => d.Code == "BC031");

        // Classic ESP32 DevKit V1 MUST still flag GPIO 6-11 as flash-bus hazard BC031
        var devKitReport = BoxCodeErrorAnalyzer.Analyze(s2Code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.Esp32DevKitV1);
        devKitReport.Diagnostics.Should().Contain(d => d.Code == "BC031");

        // Semantic & logic error catalog: assignment-in-condition, Serial w/o Serial.begin, div by zero,
        // non-PWM analogWrite, delay in ISR, unreachable code after return, empty while(true)
        const string buggyCpp = """
            void onTick() {
                delay(50);
            }
            int testFn() {
                int x = 1;
                if (x = 5) {
                    int y = 10 / 0;
                    Serial.println(y);
                }
                analogWrite(2, 128);
                return x;
                digitalWrite(13, HIGH);
            }
            void setup() {
                attachInterrupt(0, onTick, RISING);
                pinMode(2, OUTPUT);
                pinMode(13, OUTPUT);
            }
            void loop() {
                while (true) { }
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(buggyCpp, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);
        report.Diagnostics.Should().Contain(d => d.Code == "BC040" && d.HasQuickFix, "if (x = 5) assignment in condition");
        report.Diagnostics.Should().Contain(d => d.Code == "BC041", "division by zero");
        report.Diagnostics.Should().Contain(d => d.Code == "BC042", "unreachable digitalWrite after return");
        report.Diagnostics.Should().Contain(d => d.Code == "BC043", "empty while(true) lockup");
        report.Diagnostics.Should().Contain(d => d.Code == "BC045", "delay() inside ISR");
        report.Diagnostics.Should().Contain(d => d.Code == "BC046", "Serial.println without Serial.begin");
        report.Diagnostics.Should().Contain(d => d.Code == "BC035", "non-PWM pin 2 on Uno analogWrite");

        // Apply Quick Fix on BC040 (if (x = 5) -> if (x == 5))
        var bc040 = report.Diagnostics.First(d => d.Code == "BC040" && d.QuickFix is not null);
        string fixedCode = BoxCodeErrorAnalyzer.ApplyQuickFix(buggyCpp, bc040.QuickFix!);
        fixedCode.Should().Contain("if (x == 5)");
        BoxCodeErrorAnalyzer.Analyze(fixedCode, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4)
            .Diagnostics.Should().NotContain(d => d.Code == "BC040");
    }

    [Fact]
    public void FirmwareDebuggerSession_Breakpoints_StepInto_StepOver_WatchAndPinInjection_Work()
    {
        const string debugSketch = """
            int counter = 0;
            void helperStep() {
                counter = counter + 5;
            }
            void setup() {
                Serial.begin(115200);
                pinMode(13, OUTPUT);
                pinMode(2, INPUT);
            }
            void loop() {
                int btn = digitalRead(2);
                helperStep();
                digitalWrite(13, btn);
                Serial.println(counter);
                delay(100);
            }
            """;

        var session = new FirmwareDebuggerSession();
        session.LoadCode(debugSketch, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);
        session.SetSimulatedPinInput("2", isHigh: true);

        int helperLine = debugSketch.Split('\n').ToList().FindIndex(l => l.Contains("counter = counter + 5")) + 1;
        session.ToggleBreakpoint(helperLine).Should().BeTrue();

        FirmwareDebugSnapshot snap = session.Continue();
        snap.HitBreakpointLine.Should().Be(helperLine);
        snap.CallStack.Should().Contain(f => f.FunctionName.StartsWith("helperStep"));
        snap.CallStack.Should().Contain(f => f.FunctionName.StartsWith("loop"));

        // Step over inside helperStep increments counter and returns to loop
        snap = session.StepOver();
        snap = session.StepOver();
        snap.Variables.Should().Contain(v => v.Name == "counter" && v.Value == "5");

        // Step past digitalWrite(13, btn) and Serial.println(counter)
        snap = session.StepOver();
        snap = session.StepOver();
        snap.PinStates.Should().Contain(p => p.PinName == "13" && p.IsHigh);
        snap.SerialLines.Should().Contain("5");

        // Reset clears counters & restores initial frame
        FirmwareDebugSnapshot resetSnap = session.Reset();
        resetSnap.StepCount.Should().Be(0);
    }

    [Fact]
    public void SemanticAnalyzer_WhileSemicolonQuickFix_ForHeaderUndeclared_And_TypeMismatches_Detected()
    {
        const string code = """
            void setup() {
                Serial.begin("115200");
                int badNum = "text";
                String okText = "hello";
                char okChar = 'A';
                pinMode(13, OUTPUT);
            }
            void loop() {
                delay("500");
                digitalWrite(13, "HIGH");
                for (int k = 0; k < undeclaredMax; k++) {
                    delay(10);
                }
                while (true);
            }
            """;

        var report = BoxCodeErrorAnalyzer.Analyze(code, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        report.Diagnostics.Should().Contain(d => d.Code == "BC052" && d.Message.Contains("Serial.begin"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC052" && d.Message.Contains("badNum"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC052" && d.Message.Contains("delay"));
        report.Diagnostics.Should().Contain(d => d.Code == "BC052" && d.Message.Contains("digitalWrite"));
        report.Diagnostics.Should().NotContain(d => d.Code == "BC052" && d.Message.Contains("okText"));
        report.Diagnostics.Should().NotContain(d => d.Code == "BC052" && d.Message.Contains("okChar"));

        report.Diagnostics.Should().Contain(d => d.Code == "BC048" && d.Message.Contains("undeclaredMax"));

        var bc043 = report.Diagnostics.First(d => d.Code == "BC043" && d.QuickFix is not null);
        string fixedLoop = BoxCodeErrorAnalyzer.ApplyQuickFix(code, bc043.QuickFix!);
        fixedLoop.Should().Contain("while (true) { delay(10); }");
        fixedLoop.Should().NotContain("while (true);");
        BoxCodeErrorAnalyzer.Analyze(fixedLoop, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4)
            .Diagnostics.Should().NotContain(d => d.Code == "BC043");
    }

    [Fact]
    public void ProgrammerIdeCatalog_NormalizesFlashSizeMb_And_ProvidesProgrammerFallbackForIsp()
    {
        var opts = new ArduinoUploadOptions(
            BoardType: MicrocontrollerBoardType.WemosEsp32S2Mini,
            BoardVariantId: "esp32_s2_mini",
            CustomFqbnOverride: null,
            UploadSpeedBaud: 921600,
            FlashMode: "qio",
            FlashSize: "4MB",
            PartitionScheme: "default",
            CpuFrequencyMHz: 240,
            UsbCdcOnBoot: true,
            ProgrammerToolId: "default",
            VerifyOnly: false,
            UploadUsingProgrammer: true,
            VerboseOutput: false);

        string fqbn = ProgrammerIdeCatalog.BuildEffectiveFqbn(MicrocontrollerBoardType.WemosEsp32S2Mini, opts);
        fqbn.Should().Contain("FlashSize=4M").And.NotContain("FlashSize=4MB");

        string[] unoIspArgs = ProgrammerIdeCatalog.BuildUploadArguments(
            "arduino:avr:uno",
            @"C:\tmp\sketch",
            "COM4",
            opts with { BoardType = MicrocontrollerBoardType.ArduinoUnoR3R4 });
        unoIspArgs.Should().Contain("-P").And.Contain("avrispmkii");
    }

    [Fact]
    public void FirmwareDebuggerSession_EvaluatesIfElseBranches_And_ForLoops_Accurately()
    {
        const string branchSketch = """
            void setup() {
                pinMode(13, OUTPUT);
                pinMode(2, INPUT);
            }
            void loop() {
                int total = 0;
                int btn = digitalRead(2);
                if (btn == 1) {
                    digitalWrite(13, HIGH);
                } else {
                    digitalWrite(13, LOW);
                }
                for (int idx = 0; idx < 3; idx++) {
                    total = total + 10;
                }
            }
            """;

        var session = new FirmwareDebuggerSession();
        session.LoadCode(branchSketch, BoxCodingLanguage.ArduinoCpp, MicrocontrollerBoardType.ArduinoUnoR3R4);

        // Case 1: btn == 0 -> must skip HIGH branch and execute LOW branch, then run for-loop 3 times (total == 30)
        session.SetSimulatedPinInput("2", isHigh: false);
        int afterForLine = branchSketch.Split('\n').Length;
        for (int s = 0; s < 25; s++)
        {
            FirmwareDebugSnapshot snap = session.StepOver();
            snap.CurrentLine.Should().NotBe(9, "LOW input must skip the 'if (btn == 1)' body at line 9");
            if (snap.Variables.Any(v => v.Name == "total" && v.Value == "30"))
            {
                snap.PinStates.Should().Contain(p => p.PinName == "13" && !p.IsHigh);
                break;
            }
        }
        session.Reset();

        // Case 2: btn == 1 -> must take HIGH branch and skip else LOW branch
        session.SetSimulatedPinInput("2", isHigh: true);
        bool sawHigh = false;
        for (int s = 0; s < 25; s++)
        {
            FirmwareDebugSnapshot snap = session.StepOver();
            snap.CurrentLine.Should().NotBe(11, "HIGH input must skip the 'else' body at line 11");
            if (snap.PinStates.Any(p => p.PinName == "13" && p.IsHigh))
                sawHigh = true;
            if (snap.Variables.Any(v => v.Name == "total" && v.Value == "30"))
                break;
        }
        sawHigh.Should().BeTrue();
    }

    [Fact]
    public void SemanticAnalyzer_MicroPythonPlainAndCommaImports_NoFalseUndefinedOrEmptyKey()
    {
        const string code = """
            import custom_sensor
            import custom_driver, board_bus as bus_alias
            import pkg_alpha.submod

            def run_step():
                a = custom_sensor
                b = custom_driver
                c = bus_alias
                d = pkg_alpha
                e = truly_missing_var
                return (a, b, c, d, e)
            """;

        BoxCodeAnalysisReport report = BoxCodeErrorAnalyzer.Analyze(
            code,
            BoxCodingLanguage.MicroPython,
            MicrocontrollerBoardType.WemosEsp32S2Mini);

        report.Diagnostics.Should().NotContain(d => d.Code == "BC048" && d.Message.Contains("''", StringComparison.Ordinal));
        report.Diagnostics.Should().NotContain(d =>
            d.Code == "BC048" &&
            (d.Message.Contains("custom_sensor", StringComparison.Ordinal) ||
             d.Message.Contains("custom_driver", StringComparison.Ordinal) ||
             d.Message.Contains("bus_alias", StringComparison.Ordinal) ||
             d.Message.Contains("pkg_alpha", StringComparison.Ordinal)));
        report.Diagnostics.Should().Contain(d =>
            d.Code == "BC048" && d.Message.Contains("truly_missing_var", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticAnalyzer_MicroPythonMultilineFromImports_And_Comprehensions_NoFalseUndefined()
    {
        const string code = """
            from custom_pkg import (
                sym_one,
                sym_two as alias_two
            )
            import raw_mod

            def tick():
                vals = [idx * 2 for idx in range(3)]
                val = sym_one + alias_two + raw_mod.x + vals[0]
                return val

            def leak_check():
                return idx
            """;

        BoxCodeAnalysisReport report = BoxCodeErrorAnalyzer.Analyze(
            code,
            BoxCodingLanguage.MicroPython,
            MicrocontrollerBoardType.WemosEsp32S2Mini);

        report.Diagnostics.Should().NotContain(d =>
            d.Code == "BC048" &&
            (d.Message.Contains("sym_one", StringComparison.Ordinal) ||
             d.Message.Contains("sym_two", StringComparison.Ordinal) ||
             d.Message.Contains("alias_two", StringComparison.Ordinal)));

        // Comprehension variable 'idx' is valid inside the list comprehension line (L8),
        // but must still be flagged as undefined if referenced outside in leak_check() (L13).
        report.Diagnostics.Should().NotContain(d => d.Code == "BC048" && d.Line == 8);
        report.Diagnostics.Should().Contain(d =>
            d.Code == "BC048" && d.Line == 13 && d.Message.Contains("'idx'", StringComparison.Ordinal));
    }

    #endregion
}
