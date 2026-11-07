// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/BoxCodingModels.cs
// PURPOSE: Data contracts and models for the Scratch-style visual Box Coding
//          Studio. Represents dev board target profiles, visual jigsaw coding
//          blocks, sensor/actuator block parameters, circuit matching, and target languages.
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

/// <summary>Target programming language for the Box Code generator.</summary>
public enum TargetProgrammingLanguage
{
    ArduinoCpp,
    MicroPython
}

/// <summary>Visual category of the Scratch-style box coding block.</summary>
public enum BoxBlockCategory
{
    Events,
    Sensors,
    Displays,
    Actuators,
    Logic,
    Control,
    Output
}

/// <summary>The functional kind of block placed in the coding script.</summary>
public enum BoxBlockKind
{
    WhenStarted,
    ForeverLoop,
    ReadDhtSensor,
    ReadUltrasonic,
    ReadLdrLight,
    ReadPirMotion,
    ReadPotentiometer,
    ReadButton,
    ReadMpu6050,
    ReadGasSensor,
    ReadSoundSensor,
    ReadAnalogTemperature,
    SetLed,
    SetRgbLed,
    SetPwm,
    SetBuzzer,
    SetServo,
    SetRelay,
    DisplayOledI2c,
    LcdInitI2c,
    LcdPrintText,
    LcdPrintSensor,
    LcdClear,
    LcdBacklight,
    IfCondition,
    IfElseCondition,
    LogicalAnd,
    LogicalOr,
    CompareCondition,
    MathOperation,
    RepeatTimes,
    WhileLoop,
    SetVariable,
    Delay,
    SerialPrint
}

/// <summary>A single Scratch-style visual block with pin assignments and execution parameters.</summary>
public sealed class BoxBlockItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }

    private string _id = Guid.NewGuid().ToString("N")[..8];
    public string Id { get => _id; set => SetField(ref _id, value); }

    private BoxBlockKind _kind;
    public BoxBlockKind Kind { get => _kind; set => SetField(ref _kind, value); }

    private BoxBlockCategory _category;
    public BoxBlockCategory Category { get => _category; set => SetField(ref _category, value); }

    private string _title = string.Empty;
    public string Title { get => _title; set => SetField(ref _title, value); }

    private string _modelName = string.Empty;
    /// <summary>Hardware model designation (e.g. LCD1602 I2C, DHT11, HC-SR04, PCF8574, MPU-6050).</summary>
    public string ModelName { get => _modelName; set => SetField(ref _modelName, value); }

    private string _icon = "⚡";
    public string Icon { get => _icon; set => SetField(ref _icon, value); }

    private string _accentHexColor = "#3B82F6";
    public string AccentHexColor { get => _accentHexColor; set => SetField(ref _accentHexColor, value); }

    private string _backgroundHexColor = "#1E293B";
    public string BackgroundHexColor { get => _backgroundHexColor; set => SetField(ref _backgroundHexColor, value); }

    private bool _isCircuitMatched;
    /// <summary>Whether this block matches a physical component currently placed in the circuit draft.</summary>
    public bool IsCircuitMatched { get => _isCircuitMatched; set => SetField(ref _isCircuitMatched, value); }

    private string _pin1 = "D2";
    /// <summary>Primary pin designation (e.g. D2, GPIO4, A0, or Trig pin).</summary>
    public string Pin1 { get => _pin1; set => SetField(ref _pin1, value); }

    private string _pin2 = "D3";
    /// <summary>Secondary pin designation if required (e.g. Echo pin for ultrasonic).</summary>
    public string Pin2 { get => _pin2; set => SetField(ref _pin2, value); }

    private string _parameter1 = string.Empty;
    /// <summary>Primary parameter value (e.g. HIGH/LOW, angle 0-180, tone frequency 1000Hz, text message).</summary>
    public string Parameter1 { get => _parameter1; set => SetField(ref _parameter1, value); }

    private string _parameter2 = string.Empty;
    /// <summary>Secondary parameter value (e.g. duration ms, speed, threshold).</summary>
    public string Parameter2 { get => _parameter2; set => SetField(ref _parameter2, value); }

    private string _conditionOperator = ">";
    /// <summary>Comparison operator for logic blocks (&lt;, &gt;, ==, !=, &gt;=, &lt;=).</summary>
    public string ConditionOperator { get => _conditionOperator; set => SetField(ref _conditionOperator, value); }

    private double _conditionThreshold = 50.0;
    /// <summary>Threshold value for conditional triggers.</summary>
    public double ConditionThreshold { get => _conditionThreshold; set => SetField(ref _conditionThreshold, value); }

    private string _secondaryVariableName = string.Empty;
    /// <summary>Second variable name used for compound logic comparisons (AND / OR).</summary>
    public string SecondaryVariableName { get => _secondaryVariableName; set => SetField(ref _secondaryVariableName, value); }

    private string _secondaryConditionOperator = "<";
    /// <summary>Comparison operator for secondary condition.</summary>
    public string SecondaryConditionOperator { get => _secondaryConditionOperator; set => SetField(ref _secondaryConditionOperator, value); }

    private double _secondaryConditionThreshold = 80.0;
    /// <summary>Threshold value for secondary condition.</summary>
    public double SecondaryConditionThreshold { get => _secondaryConditionThreshold; set => SetField(ref _secondaryConditionThreshold, value); }

    private string _mathTargetVariable = "calcResult";
    /// <summary>Destination variable name for math operations.</summary>
    public string MathTargetVariable { get => _mathTargetVariable; set => SetField(ref _mathTargetVariable, value); }

    private string _mathOperator = "+";
    /// <summary>Mathematical operator (+, -, *, /, %).</summary>
    public string MathOperator { get => _mathOperator; set => SetField(ref _mathOperator, value); }

    private string _operand1 = "currentTemperature";
    /// <summary>First operand for math operations.</summary>
    public string Operand1 { get => _operand1; set => SetField(ref _operand1, value); }

    private string _operand2 = "1.0";
    /// <summary>Second operand for math operations.</summary>
    public string Operand2 { get => _operand2; set => SetField(ref _operand2, value); }

    private int _repeatCount = 5;
    /// <summary>Number of loop repetitions for RepeatTimes block.</summary>
    public int RepeatCount { get => _repeatCount; set => SetField(ref _repeatCount, value); }

    private string _customMessage = string.Empty;
    /// <summary>Custom telemetry or serial monitor message.</summary>
    public string CustomMessage { get => _customMessage; set => SetField(ref _customMessage, value); }

    private string _description = string.Empty;
    /// <summary>Human-readable explanation of what this block does in hardware.</summary>
    public string Description { get => _description; set => SetField(ref _description, value); }

    private int _lcdRow = 0;
    /// <summary>Target LCD row (0 or 1 for 16x2 LCD).</summary>
    public int LcdRow { get => _lcdRow; set => SetField(ref _lcdRow, value); }

    private int _lcdCol = 0;
    /// <summary>Target LCD column (0..15 for 16x2 LCD).</summary>
    public int LcdCol { get => _lcdCol; set => SetField(ref _lcdCol, value); }

    private string _lcdI2cAddress = "0x27";
    /// <summary>I2C address for LCD module (standard 0x27 or 0x3F).</summary>
    public string LcdI2cAddress { get => _lcdI2cAddress; set => SetField(ref _lcdI2cAddress, value); }

    private string _variableName = string.Empty;
    /// <summary>Variable name used in logic or sensor caching.</summary>
    public string VariableName { get => _variableName; set => SetField(ref _variableName, value); }

    private int _indentationLevel = 0;
    /// <summary>Visual indentation level for Scratch-style container blocks.</summary>
    public int IndentationLevel
    {
        get => _indentationLevel;
        set
        {
            if (SetField(ref _indentationLevel, Math.Clamp(value, 0, 6)))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndentationLeft)));
            }
        }
    }

    /// <summary>Visual horizontal indent offset (pixels) for visual hierarchy.</summary>
    public double IndentationLeft => IndentationLevel * 18.0;

    private bool _isEnabled = true;
    /// <summary>Whether this block is included in code generation.</summary>
    public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }

    public BoxBlockItem Clone()
    {
        return new BoxBlockItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Kind = Kind,
            Category = Category,
            Title = Title,
            ModelName = ModelName,
            Icon = Icon,
            AccentHexColor = AccentHexColor,
            BackgroundHexColor = BackgroundHexColor,
            IsCircuitMatched = IsCircuitMatched,
            Pin1 = Pin1,
            Pin2 = Pin2,
            Parameter1 = Parameter1,
            Parameter2 = Parameter2,
            ConditionOperator = ConditionOperator,
            ConditionThreshold = ConditionThreshold,
            SecondaryVariableName = SecondaryVariableName,
            SecondaryConditionOperator = SecondaryConditionOperator,
            SecondaryConditionThreshold = SecondaryConditionThreshold,
            MathTargetVariable = MathTargetVariable,
            MathOperator = MathOperator,
            Operand1 = Operand1,
            Operand2 = Operand2,
            RepeatCount = RepeatCount,
            CustomMessage = CustomMessage,
            Description = Description,
            LcdRow = LcdRow,
            LcdCol = LcdCol,
            LcdI2cAddress = LcdI2cAddress,
            VariableName = VariableName,
            IndentationLevel = IndentationLevel,
            IsEnabled = IsEnabled
        };
    }
}

/// <summary>Provides valid GPIO and Analog pin suggestions tailored to specific development boards.</summary>
public static class BoardPinProvider
{
    public static IReadOnlyList<string> GetDigitalPins(MicrocontrollerBoardType boardType) => boardType switch
    {
        MicrocontrollerBoardType.ArduinoMega2560 => new[]
        {
            "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12", "D13",
            "D14", "D15", "D16", "D17", "D18", "D19", "D20", "D21", "D22", "D23", "D24"
        },
        MicrocontrollerBoardType.Esp32DevKitV1 => new[]
        {
            "GPIO 2", "GPIO 4", "GPIO 5", "GPIO 12", "GPIO 13", "GPIO 14", "GPIO 15",
            "GPIO 18", "GPIO 19", "GPIO 21", "GPIO 22", "GPIO 23", "GPIO 25", "GPIO 26", "GPIO 27", "GPIO 32", "GPIO 33"
        },
        MicrocontrollerBoardType.WemosEsp32S2Mini => new[]
        {
            "GPIO 1", "GPIO 2", "GPIO 3", "GPIO 4", "GPIO 5", "GPIO 6", "GPIO 7", "GPIO 8", "GPIO 9", "GPIO 10", "GPIO 11", "GPIO 12"
        },
        MicrocontrollerBoardType.Stm32BluePillF103C8 => new[]
        {
            "PA0", "PA1", "PA2", "PA3", "PA4", "PA5", "PA6", "PA7", "PB0", "PB1", "PB10", "PB11", "PC13"
        },
        _ => new[]
        {
            "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12", "D13"
        }
    };

    public static IReadOnlyList<string> GetAnalogPins(MicrocontrollerBoardType boardType) => boardType switch
    {
        MicrocontrollerBoardType.ArduinoMega2560 => new[]
        {
            "A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "A11", "A12", "A13", "A14", "A15"
        },
        MicrocontrollerBoardType.Esp32DevKitV1 => new[]
        {
            "GPIO 32 (ADC1_CH4)", "GPIO 33 (ADC1_CH5)", "GPIO 34 (ADC1_CH6)", "GPIO 35 (ADC1_CH7)", "GPIO 36 (ADC1_CH0)", "GPIO 39 (ADC1_CH3)"
        },
        MicrocontrollerBoardType.WemosEsp32S2Mini => new[]
        {
            "GPIO 1 (ADC1)", "GPIO 2 (ADC1)", "GPIO 3 (ADC1)", "GPIO 4 (ADC1)", "GPIO 5 (ADC1)"
        },
        MicrocontrollerBoardType.Stm32BluePillF103C8 => new[]
        {
            "PA0 (ADC1_IN0)", "PA1 (ADC1_IN1)", "PA2 (ADC1_IN2)", "PA3 (ADC1_IN3)", "PA4 (ADC1_IN4)", "PA5 (ADC1_IN5)"
        },
        _ => new[]
        {
            "A0", "A1", "A2", "A3", "A4", "A5"
        }
    };
}
