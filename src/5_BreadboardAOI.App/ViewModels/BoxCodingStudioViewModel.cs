// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/BoxCodingStudioViewModel.cs
// PURPOSE: ViewModel for the Scratch-Style Visual Box Coding Studio.
//          Binds block scripts, palette library, target dev board, language,
//          live code generation, circuit-matched filtering, pin suggestions,
//          and wiring guide navigation.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using Microsoft.Win32;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class BoxCodingStudioViewModel : ObservableObject
{
    private readonly IReadOnlyList<DraftPart> _initialDraftParts;

    [ObservableProperty]
    private MicrocontrollerBoardType _selectedBoardType = MicrocontrollerBoardType.ArduinoUnoR3R4;

    [ObservableProperty]
    private TargetProgrammingLanguage _selectedLanguage = TargetProgrammingLanguage.ArduinoCpp;

    [ObservableProperty]
    private string _generatedCode = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready - Construct your box program and press CODE";

    [ObservableProperty]
    private BoxBlockItem? _selectedBlock;

    [ObservableProperty]
    private bool _filterCircuitOnly;

    [ObservableProperty]
    private string _selectedCategoryFilter = "ALL";

    [ObservableProperty]
    private bool _isLcdStimulated;

    [ObservableProperty]
    private string _lcdModelLabel = "LCD1602 I2C (PCF8574 Backpack)";

    [ObservableProperty]
    private string _lcdSimulatedRow0 = "Breadboard AOI";

    [ObservableProperty]
    private string _lcdSimulatedRow1 = "LCD 1602 Active";

    public event Action<string, string>? LcdSimulatedTextUpdated;

    public event Action<bool, string, string>? LcdSimulatedStateUpdated;

    public ObservableCollection<BoxBlockItem> Blocks { get; } = new();

    public ObservableCollection<BoxBlockItem> PaletteBlocks { get; } = new();

    public ObservableCollection<BoxBlockItem> DisplayPaletteBlocks { get; } = new();

    public ObservableCollection<string> BoardSuggestedDigitalPins { get; } = new();

    public ObservableCollection<string> BoardSuggestedAnalogPins { get; } = new();

    public IReadOnlyList<MicrocontrollerBoardType> AvailableBoards { get; } = new[]
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4,
        MicrocontrollerBoardType.ArduinoMega2560,
        MicrocontrollerBoardType.Esp32DevKitV1,
        MicrocontrollerBoardType.WemosEsp32S2Mini,
        MicrocontrollerBoardType.Stm32BluePillF103C8
    };

    public BoxCodingStudioViewModel(
        IReadOnlyList<DraftPart>? draftParts = null,
        MicrocontrollerBoardType initialBoard = MicrocontrollerBoardType.ArduinoUnoR3R4)
    {
        _initialDraftParts = draftParts ?? Array.Empty<DraftPart>();
        if (initialBoard != MicrocontrollerBoardType.None)
            SelectedBoardType = initialBoard;

        RefreshBoardPinSuggestions();
        InitializePalette();

        if (_initialDraftParts.Count > 0)
        {
            var detected = BoxCodeGenerator.AutoDetectFromCircuit(_initialDraftParts, SelectedBoardType);
            foreach (var b in detected)
                Blocks.Add(b);
            StatusMessage = $"Auto-detected {Blocks.Count} blocks from breadboard circuit draft!";
        }
        else
        {
            var defaultBlocks = BoxCodeGenerator.GetDefaultTemplate(SelectedBoardType);
            foreach (var b in defaultBlocks)
                Blocks.Add(b);
        }

        Blocks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (BoxBlockItem item in e.NewItems)
                    item.PropertyChanged += OnBlockPropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (BoxBlockItem item in e.OldItems)
                    item.PropertyChanged -= OnBlockPropertyChanged;
            }
        };

        foreach (var b in Blocks)
            b.PropertyChanged += OnBlockPropertyChanged;

        RegenerateCode();
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RegenerateCode();
    }

    private void RefreshBoardPinSuggestions()
    {
        BoardSuggestedDigitalPins.Clear();
        foreach (var p in BoardPinProvider.GetDigitalPins(SelectedBoardType))
            BoardSuggestedDigitalPins.Add(p);

        BoardSuggestedAnalogPins.Clear();
        foreach (var p in BoardPinProvider.GetAnalogPins(SelectedBoardType))
            BoardSuggestedAnalogPins.Add(p);
    }

    private void InitializePalette()
    {
        PaletteBlocks.Clear();

        // Identify which components are physically in the circuit
        bool circuitHasDht = _initialDraftParts.Any(p => p.Kind == ComponentKind.Sensor && (p.Value ?? "").Contains("DHT", StringComparison.OrdinalIgnoreCase));
        bool circuitHasUltra = _initialDraftParts.Any(p => p.Kind == ComponentKind.Sensor && ((p.Value ?? "").Contains("HC", StringComparison.OrdinalIgnoreCase) || (p.Value ?? "").Contains("SR04", StringComparison.OrdinalIgnoreCase)));
        bool circuitHasMpu = _initialDraftParts.Any(p => p.Kind == ComponentKind.Sensor && ((p.Value ?? "").Contains("MPU", StringComparison.OrdinalIgnoreCase) || (p.Value ?? "").Contains("6050", StringComparison.OrdinalIgnoreCase)));
        bool circuitHasGas = _initialDraftParts.Any(p => p.Kind == ComponentKind.Sensor && ((p.Value ?? "").Contains("MQ", StringComparison.OrdinalIgnoreCase) || (p.Value ?? "").Contains("GAS", StringComparison.OrdinalIgnoreCase)));
        bool circuitHasPot = _initialDraftParts.Any(p => p.Kind == ComponentKind.Potentiometer);
        bool circuitHasLed = _initialDraftParts.Any(p => p.Kind == ComponentKind.Led);
        bool circuitHasBuzzer = _initialDraftParts.Any(p => p.Kind == ComponentKind.Buzzer);
        bool circuitHasButton = _initialDraftParts.Any(p => p.Kind == ComponentKind.PushButton);
        bool circuitHasLcd = _initialDraftParts.Any(p => (p.Value ?? p.SymbolId ?? "").Contains("LCD", StringComparison.OrdinalIgnoreCase) ||
                                                         (p.Value ?? p.SymbolId ?? "").Contains("1602", StringComparison.OrdinalIgnoreCase));

        string defaultDigPin = BoardSuggestedDigitalPins.FirstOrDefault() ?? "D2";
        string defaultPwmPin = SelectedBoardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 2" : "D9";
        string defaultAnalogPin = BoardSuggestedAnalogPins.FirstOrDefault() ?? "A0";

        // 1. Events
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.WhenStarted,
            Category = BoxBlockCategory.Events,
            Title = "When Board Starts",
            Icon = "🚀",
            AccentHexColor = "#E67E22",
            BackgroundHexColor = "#3E2723",
            IsCircuitMatched = true,
            Description = "Runs once at startup: setups pins & serial monitor."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ForeverLoop,
            Category = BoxBlockCategory.Events,
            Title = "Forever Loop",
            Icon = "🔁",
            AccentHexColor = "#F39C12",
            BackgroundHexColor = "#4E342E",
            IsCircuitMatched = true,
            Description = "Repeats enclosed actions continuously."
        });

        // 2. Sensors
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadUltrasonic,
            Category = BoxBlockCategory.Sensors,
            Title = "Read HC-SR04 Ultrasonic",
            ModelName = "HC-SR04",
            Icon = "📡",
            AccentHexColor = "#0288D1",
            BackgroundHexColor = "#0D47A1",
            IsCircuitMatched = circuitHasUltra,
            Pin1 = SelectedBoardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 5" : "D9",
            Pin2 = SelectedBoardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 18" : "D10",
            Description = "Measures distance (cm) via Trig & Echo pulses."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadDhtSensor,
            Category = BoxBlockCategory.Sensors,
            Title = "Read DHT11/22 Sensor",
            ModelName = "DHT11",
            Icon = "🌡️",
            AccentHexColor = "#00ACC1",
            BackgroundHexColor = "#006064",
            IsCircuitMatched = circuitHasDht,
            Pin1 = SelectedBoardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 4" : "D2",
            Description = "Reads temperature (°C) and humidity (%)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadMpu6050,
            Category = BoxBlockCategory.Sensors,
            Title = "Read MPU-6050 IMU",
            ModelName = "MPU-6050",
            Icon = "🔄",
            AccentHexColor = "#3949AB",
            BackgroundHexColor = "#1A237E",
            IsCircuitMatched = circuitHasMpu,
            Description = "Reads 6-axis accelerometer & gyroscope over I2C."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadGasSensor,
            Category = BoxBlockCategory.Sensors,
            Title = "Read MQ-2 Gas/Smoke",
            ModelName = "MQ-2",
            Icon = "💨",
            AccentHexColor = "#FB8C00",
            BackgroundHexColor = "#E65100",
            IsCircuitMatched = circuitHasGas,
            Pin1 = defaultAnalogPin,
            Description = "Detects combustible gas, LPG, and smoke concentration."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadLdrLight,
            Category = BoxBlockCategory.Sensors,
            Title = "Read LDR Light Sensor",
            ModelName = "GL5528 LDR",
            Icon = "☀️",
            AccentHexColor = "#FBC02D",
            BackgroundHexColor = "#F57F17",
            Pin1 = defaultAnalogPin,
            Description = "Measures ambient light level via analog input."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadPotentiometer,
            Category = BoxBlockCategory.Sensors,
            Title = "Read Potentiometer Dial",
            ModelName = "B10K POT",
            Icon = "🎛️",
            AccentHexColor = "#00897B",
            BackgroundHexColor = "#004D40",
            IsCircuitMatched = circuitHasPot,
            Pin1 = defaultAnalogPin,
            Description = "Reads rotary potentiometer wiper voltage."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadPirMotion,
            Category = BoxBlockCategory.Sensors,
            Title = "Read PIR Motion",
            ModelName = "HC-SR501",
            Icon = "👁️",
            AccentHexColor = "#7CB342",
            BackgroundHexColor = "#33691E",
            Pin1 = defaultDigPin,
            Description = "Detects human / infrared motion."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadButton,
            Category = BoxBlockCategory.Sensors,
            Title = "Read Push Button",
            ModelName = "TACT-6MM",
            Icon = "🔘",
            AccentHexColor = "#5E35B1",
            BackgroundHexColor = "#311B92",
            IsCircuitMatched = circuitHasButton,
            Pin1 = defaultDigPin,
            Description = "Reads tactile switch state with pullup resistor."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.ReadAnalogTemperature,
            Category = BoxBlockCategory.Sensors,
            Title = "Read TMP36/LM35 Temp",
            ModelName = "TMP36 / LM35",
            Icon = "🌡️",
            AccentHexColor = "#0288D1",
            BackgroundHexColor = "#01579B",
            Pin1 = defaultAnalogPin,
            Description = "Reads precision analog temperature (10mV/°C)."
        });

        // 3. Actuators & Displays
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetLed,
            Category = BoxBlockCategory.Actuators,
            Title = "Set LED Output",
            Icon = "💡",
            AccentHexColor = "#E91E63",
            BackgroundHexColor = "#880E4F",
            IsCircuitMatched = circuitHasLed,
            Pin1 = SelectedBoardType == MicrocontrollerBoardType.Esp32DevKitV1 ? "GPIO 2" : "D13",
            Parameter1 = "HIGH",
            Description = "Drives pin HIGH (ON) or LOW (OFF) for LED indicator."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetPwm,
            Category = BoxBlockCategory.Actuators,
            Title = "Set PWM Value",
            Icon = "📊",
            AccentHexColor = "#9C27B0",
            BackgroundHexColor = "#4A148C",
            Pin1 = defaultPwmPin,
            Parameter1 = "128",
            Description = "Outputs analog PWM waveform (duty cycle 0..255)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetBuzzer,
            Category = BoxBlockCategory.Actuators,
            Title = "Sound Buzzer Tone",
            Icon = "🔊",
            AccentHexColor = "#D81B60",
            BackgroundHexColor = "#880E4F",
            IsCircuitMatched = circuitHasBuzzer,
            Pin1 = defaultDigPin,
            Parameter1 = "1000",
            Parameter2 = "250",
            Description = "Plays frequency tone (Hz) for given duration (ms)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetServo,
            Category = BoxBlockCategory.Actuators,
            Title = "Set Servo Angle",
            Icon = "🦾",
            AccentHexColor = "#3F51B5",
            BackgroundHexColor = "#1A237E",
            Pin1 = defaultPwmPin,
            Parameter1 = "90",
            Description = "Rotates servo motor arm to 0..180 degrees."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetRelay,
            Category = BoxBlockCategory.Actuators,
            Title = "Switch Relay Module",
            Icon = "🔌",
            AccentHexColor = "#FF5722",
            BackgroundHexColor = "#BF360C",
            Pin1 = defaultDigPin,
            Parameter1 = "HIGH",
            Description = "Toggles high-current electromagnetic relay switch."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.DisplayOledI2c,
            Category = BoxBlockCategory.Displays,
            Title = "Display Text on OLED",
            ModelName = "SSD1306 OLED",
            Icon = "🖥️",
            AccentHexColor = "#00BCD4",
            BackgroundHexColor = "#006064",
            Parameter1 = "Breadboard AOI",
            Description = "Renders text message on 0.96\" I2C OLED display (128x64)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LcdInitI2c,
            Category = BoxBlockCategory.Displays,
            Title = "Init LCD 1602 I2C (0x27)",
            ModelName = "LCD1602 I2C",
            Icon = "🖥️",
            AccentHexColor = "#06B6D4",
            BackgroundHexColor = "#083344",
            LcdI2cAddress = "0x27",
            IsCircuitMatched = circuitHasLcd,
            Description = "Initializes 16x2 HD44780 Character LCD via PCF8574 I2C Backpack (Addr: 0x27 / 0x3F)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LcdPrintText,
            Category = BoxBlockCategory.Displays,
            Title = "Print Text to LCD 1602",
            ModelName = "LCD1602 I2C",
            Icon = "📝",
            AccentHexColor = "#06B6D4",
            BackgroundHexColor = "#083344",
            LcdRow = 0,
            LcdCol = 0,
            Parameter1 = "Hello World!",
            IsCircuitMatched = circuitHasLcd,
            Description = "Prints alphanumeric text string to 16x2 LCD at row (0/1) and col (0..15)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LcdPrintSensor,
            Category = BoxBlockCategory.Displays,
            Title = "Display Sensor on LCD",
            ModelName = "LCD1602 I2C",
            Icon = "📟",
            AccentHexColor = "#06B6D4",
            BackgroundHexColor = "#083344",
            LcdRow = 0,
            LcdCol = 0,
            CustomMessage = "Temp: \"temp\"C",
            IsCircuitMatched = circuitHasLcd,
            Description = "Formats and writes live sensor telemetry (Temp: \"temp\"C, Hum: {hum}%, Dist: {dist}cm) to LCD screen."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LcdClear,
            Category = BoxBlockCategory.Displays,
            Title = "Clear LCD Screen",
            ModelName = "LCD1602 I2C",
            Icon = "🧹",
            AccentHexColor = "#06B6D4",
            BackgroundHexColor = "#083344",
            IsCircuitMatched = circuitHasLcd,
            Description = "Clears all characters on 16x2 screen and returns cursor to (0,0)."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LcdBacklight,
            Category = BoxBlockCategory.Displays,
            Title = "Set LCD Backlight",
            ModelName = "LCD1602 I2C",
            Icon = "💡",
            AccentHexColor = "#06B6D4",
            BackgroundHexColor = "#083344",
            Parameter1 = "ON",
            IsCircuitMatched = circuitHasLcd,
            Description = "Turns the blue LED backlight ON or OFF."
        });

        // 4. Logic & Comparisons
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.CompareCondition,
            Category = BoxBlockCategory.Logic,
            Title = "Compare Variable (<, >, ==)",
            Icon = "⚖️",
            AccentHexColor = "#8B5CF6",
            BackgroundHexColor = "#3B0764",
            ConditionOperator = ">",
            ConditionThreshold = 30.0,
            VariableName = "currentTemperature",
            Description = "Evaluates whether a numeric sensor variable meets threshold."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.IfElseCondition,
            Category = BoxBlockCategory.Logic,
            Title = "If-Else Branch Condition",
            Icon = "🔀",
            AccentHexColor = "#8B5CF6",
            BackgroundHexColor = "#3B0764",
            ConditionOperator = ">",
            ConditionThreshold = 30.0,
            VariableName = "currentTemperature",
            Description = "Executes True branch if condition holds, otherwise executes Else branch."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LogicalAnd,
            Category = BoxBlockCategory.Logic,
            Title = "Logical AND (&&)",
            Icon = "🔗",
            AccentHexColor = "#8B5CF6",
            BackgroundHexColor = "#3B0764",
            VariableName = "currentTemperature",
            ConditionOperator = ">",
            ConditionThreshold = 25.0,
            SecondaryVariableName = "currentHumidity",
            SecondaryConditionOperator = "<",
            SecondaryConditionThreshold = 70.0,
            Description = "Evaluates whether two conditions are BOTH satisfied simultaneously."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.LogicalOr,
            Category = BoxBlockCategory.Logic,
            Title = "Logical OR (||)",
            Icon = "🔀",
            AccentHexColor = "#8B5CF6",
            BackgroundHexColor = "#3B0764",
            VariableName = "currentTemperature",
            ConditionOperator = ">",
            ConditionThreshold = 35.0,
            SecondaryVariableName = "gasLevelAnalog",
            SecondaryConditionOperator = ">",
            SecondaryConditionThreshold = 300.0,
            Description = "Evaluates whether EITHER condition is satisfied."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.MathOperation,
            Category = BoxBlockCategory.Logic,
            Title = "Math Calculation (+ - * /)",
            Icon = "➗",
            AccentHexColor = "#10B981",
            BackgroundHexColor = "#064E3B",
            MathTargetVariable = "calcResult",
            Operand1 = "currentTemperature",
            MathOperator = "*",
            Operand2 = "1.8",
            Description = "Computes an arithmetic calculation and stores the result into a variable."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SetVariable,
            Category = BoxBlockCategory.Logic,
            Title = "Set Variable Value",
            Icon = "📦",
            AccentHexColor = "#8B5CF6",
            BackgroundHexColor = "#3B0764",
            VariableName = "systemState",
            Parameter1 = "1",
            Description = "Stores a numeric value or flag into a program variable."
        });

        // 5. Control Flow
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.IfCondition,
            Category = BoxBlockCategory.Control,
            Title = "If Proximity Condition",
            Icon = "🔀",
            AccentHexColor = "#6366F1",
            BackgroundHexColor = "#312E81",
            ConditionOperator = "<",
            ConditionThreshold = 20.0,
            Description = "Branches logic if condition evaluates to true."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.RepeatTimes,
            Category = BoxBlockCategory.Control,
            Title = "Repeat N Times Loop",
            Icon = "🔁",
            AccentHexColor = "#6366F1",
            BackgroundHexColor = "#312E81",
            RepeatCount = 5,
            Description = "Executes a nested block sequence a set number of times."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.WhileLoop,
            Category = BoxBlockCategory.Control,
            Title = "While Loop Condition",
            Icon = "🔄",
            AccentHexColor = "#6366F1",
            BackgroundHexColor = "#312E81",
            VariableName = "buttonPressed",
            ConditionOperator = "==",
            ConditionThreshold = 1.0,
            Description = "Repeats loop body while condition remains true."
        });
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.Delay,
            Category = BoxBlockCategory.Control,
            Title = "Wait / Delay",
            Icon = "⏱️",
            AccentHexColor = "#607D8B",
            BackgroundHexColor = "#263238",
            Parameter1 = "500",
            Description = "Pauses loop execution for specified milliseconds."
        });

        // 6. Output
        PaletteBlocks.Add(new BoxBlockItem
        {
            Kind = BoxBlockKind.SerialPrint,
            Category = BoxBlockCategory.Output,
            Title = "Print to Serial",
            Icon = "📟",
            AccentHexColor = "#2E7D32",
            BackgroundHexColor = "#1B5E20",
            CustomMessage = "Breadboard Telemetry OK",
            Description = "Transmits text message to USB serial monitor."
        });

        UpdateDisplayPalette();
    }

    [RelayCommand]
    public void SelectCategoryFilter(string category)
    {
        SelectedCategoryFilter = category;
        UpdateDisplayPalette();
    }

    private void UpdateDisplayPalette()
    {
        DisplayPaletteBlocks.Clear();
        var query = PaletteBlocks.AsEnumerable();

        if (FilterCircuitOnly)
            query = query.Where(b => b.IsCircuitMatched);

        if (!string.Equals(SelectedCategoryFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(SelectedCategoryFilter, "Sensors", StringComparison.OrdinalIgnoreCase))
            {
                // Under Sensors category: include sensors and LCD modules with their models
                query = query.Where(b => b.Category == BoxBlockCategory.Sensors ||
                                         b.Category == BoxBlockCategory.Displays ||
                                         b.ModelName.Contains("LCD", StringComparison.OrdinalIgnoreCase));
            }
            else if (string.Equals(SelectedCategoryFilter, "Displays", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(SelectedCategoryFilter, "LCD", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(SelectedCategoryFilter, "LCDs", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(b => b.Category == BoxBlockCategory.Displays || b.ModelName.Contains("LCD", StringComparison.OrdinalIgnoreCase));
            }
            else if (Enum.TryParse<BoxBlockCategory>(SelectedCategoryFilter, true, out var cat))
            {
                query = query.Where(b => b.Category == cat);
            }
        }

        var items = query.OrderByDescending(b => b.IsCircuitMatched).ToList();
        foreach (var item in items)
            DisplayPaletteBlocks.Add(item);
    }

    partial void OnFilterCircuitOnlyChanged(bool value) => UpdateDisplayPalette();

    partial void OnSelectedBoardTypeChanged(MicrocontrollerBoardType value)
    {
        RefreshBoardPinSuggestions();
        InitializePalette();
        RegenerateCode();
    }

    partial void OnSelectedLanguageChanged(TargetProgrammingLanguage value) => RegenerateCode();

    [RelayCommand]
    public void RegenerateCode()
    {
        GeneratedCode = BoxCodeGenerator.GenerateCode(Blocks, SelectedBoardType, SelectedLanguage);
        StatusMessage = $"Code generated ({SelectedLanguage}) · {Blocks.Count} visual block(s)";
        UpdateLcdSimulatedText();
    }

    private void UpdateLcdSimulatedText()
    {
        bool hasDht = Blocks.Any(b => b.IsEnabled && b.Kind == BoxBlockKind.ReadDhtSensor);
        bool hasUltra = Blocks.Any(b => b.IsEnabled && b.Kind == BoxBlockKind.ReadUltrasonic);

        bool hasLcdCoded = Blocks.Any(b => b.IsEnabled && (
            b.Kind == BoxBlockKind.LcdInitI2c ||
            b.Kind == BoxBlockKind.LcdPrintText ||
            b.Kind == BoxBlockKind.LcdPrintSensor ||
            b.Kind == BoxBlockKind.LcdClear ||
            b.Kind == BoxBlockKind.LcdBacklight));

        IsLcdStimulated = hasLcdCoded;

        string row0 = "Breadboard AOI";
        string row1 = "System Online";

        var printBlocks = Blocks.Where(b => b.IsEnabled && (b.Kind == BoxBlockKind.LcdPrintText || b.Kind == BoxBlockKind.LcdPrintSensor)).ToList();

        if (printBlocks.Count > 0)
        {
            foreach (var pb in printBlocks)
            {
                string rawMsg = !string.IsNullOrWhiteSpace(pb.CustomMessage) ? pb.CustomMessage : pb.Parameter1;
                string text;
                if (pb.Kind == BoxBlockKind.LcdPrintText)
                {
                    text = string.IsNullOrWhiteSpace(rawMsg) ? "Hello World!" : rawMsg;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(rawMsg))
                    {
                        text = hasDht ? "Temp: 24.5C" : (hasUltra ? "Dist: 18.5 cm" : "Sensor Active");
                    }
                    else
                    {
                        // Clean user-coded templating: handles "{temp}", "\"temp\"", "<temp>", "{hum}", "\"hum\"", "{dist}", "\"dist\""
                        string formatted = rawMsg;
                        formatted = formatted.Replace("{temp}", "24.5", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("\"temp\"", "24.5", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("<temp>", "24.5", StringComparison.OrdinalIgnoreCase);

                        formatted = formatted.Replace("{hum}", "52", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("\"hum\"", "52", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("<hum>", "52", StringComparison.OrdinalIgnoreCase);

                        formatted = formatted.Replace("{dist}", "18.5", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("\"dist\"", "18.5", StringComparison.OrdinalIgnoreCase);
                        formatted = formatted.Replace("<dist>", "18.5", StringComparison.OrdinalIgnoreCase);

                        // Clean any residual double quotes around substituted numbers
                        formatted = formatted.Replace("\"24.5\"", "24.5").Replace("\"52\"", "52").Replace("\"18.5\"", "18.5");
                        text = formatted.Trim();
                    }
                }

                if (pb.LcdRow == 0)
                    row0 = text;
                else
                    row1 = text;
            }
        }
        else if (hasDht)
        {
            row0 = "Temp: 24.5C";
            row1 = "Humidity: 52%";
        }
        else if (hasUltra)
        {
            row0 = "Distance: 18.5cm";
            row1 = "Target: TRACKED";
        }

        LcdSimulatedRow0 = row0;
        LcdSimulatedRow1 = row1;
        LcdSimulatedTextUpdated?.Invoke(row0, row1);
        LcdSimulatedStateUpdated?.Invoke(IsLcdStimulated, row0, row1);
    }

    [RelayCommand]
    public void ToggleLcdStimulation()
    {
        IsLcdStimulated = !IsLcdStimulated;
        LcdSimulatedStateUpdated?.Invoke(IsLcdStimulated, LcdSimulatedRow0, LcdSimulatedRow1);
        StatusMessage = $"LCD 1602 Simulation {(IsLcdStimulated ? "STIMULATED (Backlight ON)" : "TURNED OFF (Screen Dark)")}";
    }

    [RelayCommand]
    public void IndentBlock(BoxBlockItem? block)
    {
        if (block != null)
        {
            block.IndentationLevel = Math.Min(block.IndentationLevel + 1, 6);
            RegenerateCode();
        }
    }

    [RelayCommand]
    public void OutdentBlock(BoxBlockItem? block)
    {
        if (block != null)
        {
            block.IndentationLevel = Math.Max(block.IndentationLevel - 1, 0);
            RegenerateCode();
        }
    }

    [RelayCommand]
    public void AddBlock(BoxBlockItem? paletteItem)
    {
        if (paletteItem == null)
            return;

        var newBlock = paletteItem.Clone();
        Blocks.Add(newBlock);
        SelectedBlock = newBlock;
        RegenerateCode();
        StatusMessage = $"Added block: {newBlock.Title}";
    }

    [RelayCommand]
    public void RemoveBlock(BoxBlockItem? block)
    {
        if (block == null)
            return;

        int idx = Blocks.IndexOf(block);
        if (idx >= 0)
        {
            Blocks.RemoveAt(idx);
            SelectedBlock = Blocks.Count > 0 ? Blocks[Math.Clamp(idx, 0, Blocks.Count - 1)] : null;
            RegenerateCode();
            StatusMessage = $"Removed block: {block.Title}";
        }
    }

    [RelayCommand]
    public void MoveBlockUp(BoxBlockItem? block)
    {
        if (block == null)
            return;

        int idx = Blocks.IndexOf(block);
        if (idx > 0)
        {
            Blocks.Move(idx, idx - 1);
            SelectedBlock = block;
            RegenerateCode();
        }
    }

    [RelayCommand]
    public void MoveBlockDown(BoxBlockItem? block)
    {
        if (block == null)
            return;

        int idx = Blocks.IndexOf(block);
        if (idx >= 0 && idx < Blocks.Count - 1)
        {
            Blocks.Move(idx, idx + 1);
            SelectedBlock = block;
            RegenerateCode();
        }
    }

    [RelayCommand]
    public void CopyCode()
    {
        if (string.IsNullOrWhiteSpace(GeneratedCode))
            return;

        try
        {
            Clipboard.SetText(GeneratedCode);
            StatusMessage = "⚡ CODE COPIED TO CLIPBOARD! Ready to paste into IDE / Thonny / Arduino.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy to clipboard: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ExportCode()
    {
        if (string.IsNullOrWhiteSpace(GeneratedCode))
            return;

        var sfd = new SaveFileDialog
        {
            Title = "Export Firmware Source Code",
            Filter = SelectedLanguage == TargetProgrammingLanguage.MicroPython
                ? "MicroPython Script (*.py)|*.py|All Files (*.*)|*.*"
                : "Arduino Sketch (*.ino)|*.ino|C++ Source (*.cpp)|*.cpp|All Files (*.*)|*.*",
            FileName = SelectedLanguage == TargetProgrammingLanguage.MicroPython ? "main.py" : "BreadboardFirmware.ino"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(sfd.FileName, GeneratedCode);
                StatusMessage = $"Exported successfully to: {Path.GetFileName(sfd.FileName)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public void AutoDetectFromCircuit()
    {
        if (_initialDraftParts.Count == 0)
        {
            StatusMessage = "No physical components placed in breadboard draft to detect.";
            return;
        }

        Blocks.Clear();
        var detected = BoxCodeGenerator.AutoDetectFromCircuit(_initialDraftParts, SelectedBoardType);
        foreach (var b in detected)
            Blocks.Add(b);

        RegenerateCode();
        StatusMessage = $"Auto-detected {Blocks.Count} block(s) directly from breadboard components!";
    }

    [RelayCommand]
    public void ResetDefault()
    {
        Blocks.Clear();
        var defaults = BoxCodeGenerator.GetDefaultTemplate(SelectedBoardType);
        foreach (var b in defaults)
            Blocks.Add(b);

        RegenerateCode();
        StatusMessage = "Reset block script to default starter template.";
    }

    [RelayCommand]
    public void OpenWiringGuide()
    {
        try
        {
            var parts = _initialDraftParts.ToList();
            var targetBoard = SelectedBoardType == MicrocontrollerBoardType.None
                ? MicrocontrollerBoardType.ArduinoUnoR3R4
                : SelectedBoardType;

            var window = new DevBoardSensorWiringGuideWindow(parts, targetBoard)
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open Wiring Guide: {ex.Message}";
        }
    }
}
