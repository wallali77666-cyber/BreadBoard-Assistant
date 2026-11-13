// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/ProgrammerIdeModels.cs
// PURPOSE: Arduino IDE-style build/upload options, board/variant FQBN builder,
//          hardware programmer tools, MicroPython transfer CLI args, COM port
//          sanitization, and compiler diagnostic parsers.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

public sealed record BoardVariantPreset(
    string Id,
    string DisplayName,
    MicrocontrollerBoardType BoardType,
    string BaseFqbn,
    int DefaultUploadBaud,
    bool SupportsFlashOptions,
    bool SupportsUsbCdcOnBoot,
    bool SupportsCpuFreqSelection,
    string Notes);

public sealed record ProgrammerToolOption(
    string Id,
    string DisplayName,
    string CliId,
    string SupportedBoardsSummary);

public sealed record PhysicalComPortEntry(
    string PortName,
    string DisplayLabel,
    bool IsVirtualSimulator)
{
    public override string ToString() => DisplayLabel;
}

public sealed record ArduinoUploadOptions(
    MicrocontrollerBoardType BoardType = MicrocontrollerBoardType.ArduinoUnoR3R4,
    string BoardVariantId = "uno_r3",
    string? CustomFqbnOverride = null,
    int UploadSpeedBaud = 115200,
    string FlashMode = "qio",
    string FlashSize = "4MB",
    string PartitionScheme = "default",
    int CpuFrequencyMHz = 0,
    bool UsbCdcOnBoot = true,
    string ProgrammerToolId = "default",
    bool VerifyOnly = false,
    bool UploadUsingProgrammer = false,
    bool VerboseOutput = false,
    bool AllowVirtualFallbackOnPhysicalPort = false,
    string MicroPythonTool = "auto",
    string TargetBoardInstanceId = "MCU_1");

public static partial class ProgrammerIdeCatalog
{
    public const string VirtualSimPortDisplay = "VIRTUAL-SIM (Built-in Simulator)";

    public static IReadOnlyList<BoardVariantPreset> AllBoardVariants { get; } = new[]
    {
        new BoardVariantPreset(
            "uno_r3",
            "Arduino Uno R3 (ATmega328P · arduino:avr:uno)",
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            "arduino:avr:uno",
            115200,
            SupportsFlashOptions: false,
            SupportsUsbCdcOnBoot: false,
            SupportsCpuFreqSelection: false,
            "Classic 16 MHz 5V ATmega328P via Optiboot USB-UART."),

        new BoardVariantPreset(
            "uno_r4_minima",
            "Arduino Uno R4 Minima (RA4M1 · arduino:renesas_uno:minima)",
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            "arduino:renesas_uno:minima",
            115200,
            SupportsFlashOptions: false,
            SupportsUsbCdcOnBoot: false,
            SupportsCpuFreqSelection: false,
            "48 MHz Arm Cortex-M4 5V board with native USB-C DFU."),

        new BoardVariantPreset(
            "uno_r4_wifi",
            "Arduino Uno R4 WiFi (RA4M1 + ESP32-S3 · unor4wifi)",
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            "arduino:renesas_uno:unor4wifi",
            115200,
            SupportsFlashOptions: false,
            SupportsUsbCdcOnBoot: false,
            SupportsCpuFreqSelection: false,
            "5V Uno R4 with 12x8 LED Matrix and ESP32-S3 bridge."),

        new BoardVariantPreset(
            "mega_2560",
            "Arduino Mega 2560 (ATmega2560 · cpu=atmega2560)",
            MicrocontrollerBoardType.ArduinoMega2560,
            "arduino:avr:mega:cpu=atmega2560",
            115200,
            SupportsFlashOptions: false,
            SupportsUsbCdcOnBoot: false,
            SupportsCpuFreqSelection: false,
            "256 KB Flash, 54 digital I/O, 16 analog pins, 4 UARTs."),

        new BoardVariantPreset(
            "esp32_devkit",
            "ESP32 DevKit V1 / DOIT (esp32:esp32:esp32)",
            MicrocontrollerBoardType.Esp32DevKitV1,
            "esp32:esp32:esp32",
            921600,
            SupportsFlashOptions: true,
            SupportsUsbCdcOnBoot: false,
            SupportsCpuFreqSelection: true,
            "Dual-core Xtensa LX6 240 MHz 3.3V Wi-Fi + BLE module."),

        new BoardVariantPreset(
            "esp32_s2_mini",
            "WEMOS LOLIN S2 Mini (esp32:esp32:lolin_s2_mini)",
            MicrocontrollerBoardType.WemosEsp32S2Mini,
            "esp32:esp32:lolin_s2_mini",
            921600,
            SupportsFlashOptions: true,
            SupportsUsbCdcOnBoot: true,
            SupportsCpuFreqSelection: true,
            "ESP32-S2 native USB OTG CDC board (Hold 0 + Tap RST for ROM DFU)."),

        new BoardVariantPreset(
            "stm32_bluepill",
            "STM32F103C8T6 Blue Pill (GenF1 · pnum=BLUEPILL_F103C8)",
            MicrocontrollerBoardType.Stm32BluePillF103C8,
            "STMicroelectronics:stm32:GenF1:pnum=BLUEPILL_F103C8",
            115200,
            SupportsFlashOptions: false,
            SupportsUsbCdcOnBoot: true,
            SupportsCpuFreqSelection: false,
            "72 MHz ARM Cortex-M3 · Upload via ST-Link, Serial Boot0, or USB DFU.")
    };

    public static IReadOnlyList<ProgrammerToolOption> AllProgrammerTools { get; } = new[]
    {
        new ProgrammerToolOption("default", "Serial Bootloader / Native USB (Default)", string.Empty, "Standard IDE USB-UART / OTG upload"),
        new ProgrammerToolOption("avrispmkii", "AVRISP mkII", "avrispmkii", "ATmega328P / ATmega2560 ICSP"),
        new ProgrammerToolOption("arduinoasisp", "Arduino as ISP", "arduinoasisp", "AVR target flashed through another Arduino"),
        new ProgrammerToolOption("usbasp", "USBasp", "usbasp", "AVR 6-pin / 10-pin USBasp programmer"),
        new ProgrammerToolOption("esptool", "esptool (ESP ROM UART)", "esptool", "ESP32 / ESP32-S2 serial bootloader"),
        new ProgrammerToolOption("stlink", "ST-Link V2 (SWD)", "stlink", "STM32F103 Blue Pill SWDIO / SWCLK")
    };

    public static IReadOnlyList<int> AvailableUploadSpeeds { get; } = new[]
    {
        9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600
    };

    public static IReadOnlyList<string> AvailableFlashModes { get; } = new[] { "qio", "dio", "qout", "dout" };

    public static IReadOnlyList<string> AvailableFlashSizes { get; } = new[] { "2MB", "4MB", "8MB", "16MB" };

    public static IReadOnlyList<string> AvailablePartitionSchemes { get; } = new[]
    {
        "default", "huge_app", "min_spiffs", "no_ota", "ffat"
    };

    public static IReadOnlyList<int> AvailableCpuFrequenciesMHz { get; } = new[] { 0, 80, 160, 240 };

    public static IReadOnlyList<string> AvailableMicroPythonTools { get; } = new[]
    {
        "auto", "mpremote", "ampy", "rshell", "esptool"
    };

    public static IReadOnlyList<BoardVariantPreset> GetVariantsForBoard(MicrocontrollerBoardType boardType)
    {
        var matches = AllBoardVariants.Where(v => v.BoardType == boardType).ToList();
        return matches.Count > 0 ? matches : AllBoardVariants;
    }

    public static BoardVariantPreset GetDefaultVariant(MicrocontrollerBoardType boardType) =>
        GetVariantsForBoard(boardType)[0];

    public static BoardVariantPreset? FindVariantById(string? variantId)
    {
        if (string.IsNullOrWhiteSpace(variantId))
            return null;

        return AllBoardVariants.FirstOrDefault(v =>
            string.Equals(v.Id, variantId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static ProgrammerToolOption GetProgrammerTool(string? programmerId)
    {
        if (string.IsNullOrWhiteSpace(programmerId))
            return AllProgrammerTools[0];

        return AllProgrammerTools.FirstOrDefault(p =>
            string.Equals(p.Id, programmerId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? AllProgrammerTools[0];
    }

    public static bool IsVirtualPort(string? portDisplay)
    {
        if (string.IsNullOrWhiteSpace(portDisplay))
            return true;

        string trimmed = portDisplay.Trim();
        return trimmed.StartsWith("VIRTUAL-SIM", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("COM-SIM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips human-readable suffixes such as "COM7 (Hardware USB-UART)" or "COM12 - CH340"
    /// down to the raw OS device identifier ("COM7" or "/dev/ttyUSB0") required by CLI uploaders.
    /// </summary>
    public static string? ExtractRawComPort(string? portDisplay)
    {
        if (IsVirtualPort(portDisplay))
            return null;

        string trimmed = portDisplay!.Trim();
        Match comMatch = WindowsComPortRegex().Match(trimmed);
        if (comMatch.Success)
            return comMatch.Groups[1].Value.ToUpperInvariant();

        Match unixMatch = UnixTtyPortRegex().Match(trimmed);
        if (unixMatch.Success)
            return unixMatch.Groups[1].Value;

        int spaceIdx = trimmed.IndexOf(' ');
        string head = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }

    public static string BuildEffectiveFqbn(MicrocontrollerBoardType boardType, ArduinoUploadOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.CustomFqbnOverride))
            return options.CustomFqbnOverride.Trim();

        BoardVariantPreset variant = FindVariantById(options?.BoardVariantId) is { } v && v.BoardType == boardType
            ? v
            : GetDefaultVariant(boardType);

        if (options is null)
            return variant.BaseFqbn;

        var props = new List<string>();
        if (variant.SupportsFlashOptions)
        {
            if (!string.IsNullOrWhiteSpace(options.FlashMode))
                props.Add($"FlashMode={options.FlashMode.Trim().ToLowerInvariant()}");
            if (!string.IsNullOrWhiteSpace(options.FlashSize))
            {
                string normSize = options.FlashSize.Trim().ToUpperInvariant();
                if (normSize.EndsWith("MB", StringComparison.Ordinal))
                    normSize = normSize[..^1];
                props.Add($"FlashSize={normSize}");
            }
            if (!string.IsNullOrWhiteSpace(options.PartitionScheme))
                props.Add($"PartitionScheme={options.PartitionScheme.Trim()}");
        }

        if (variant.SupportsCpuFreqSelection && options.CpuFrequencyMHz > 0)
            props.Add($"CPUFreq={options.CpuFrequencyMHz.ToString(CultureInfo.InvariantCulture)}");

        if (variant.SupportsUsbCdcOnBoot)
        {
            if (boardType == MicrocontrollerBoardType.WemosEsp32S2Mini)
                props.Add(options.UsbCdcOnBoot ? "CDCOnBoot=cdc" : "CDCOnBoot=default");
            else if (boardType == MicrocontrollerBoardType.Stm32BluePillF103C8 && options.UsbCdcOnBoot)
                props.Add("usb=CDCgen");
        }

        if (boardType is MicrocontrollerBoardType.Esp32DevKitV1 or MicrocontrollerBoardType.WemosEsp32S2Mini &&
            options.UploadSpeedBaud > 0)
        {
            props.Add($"UploadSpeed={options.UploadSpeedBaud.ToString(CultureInfo.InvariantCulture)}");
        }

        if (props.Count == 0)
            return variant.BaseFqbn;

        char joiner = variant.BaseFqbn.Count(c => c == ':') >= 3 ? ',' : ':';
        return variant.BaseFqbn + joiner + string.Join(",", props);
    }

    public static string[] BuildCompileArguments(string fqbn, string sketchDir, bool verboseOutput)
    {
        var args = new List<string> { "compile", "--fqbn", fqbn };
        if (verboseOutput)
            args.Add("-v");
        args.Add(sketchDir);
        return args.ToArray();
    }

    public static string[] BuildUploadArguments(
        string fqbn,
        string sketchDir,
        string? rawPort,
        ArduinoUploadOptions options)
    {
        var args = new List<string> { "upload", "--fqbn", fqbn };
        if (!string.IsNullOrWhiteSpace(rawPort))
        {
            args.Add("-p");
            args.Add(rawPort);
        }

        ProgrammerToolOption programmer = GetProgrammerTool(options.ProgrammerToolId);
        string? effectiveProgrammerCliId = !string.IsNullOrWhiteSpace(programmer.CliId)
            ? programmer.CliId
            : (options.UploadUsingProgrammer
                ? options.BoardType switch
                {
                    MicrocontrollerBoardType.Stm32BluePillF103C8 => "stlink",
                    MicrocontrollerBoardType.Esp32DevKitV1 or MicrocontrollerBoardType.WemosEsp32S2Mini => "esptool",
                    _ => "avrispmkii"
                }
                : null);

        if (!string.IsNullOrWhiteSpace(effectiveProgrammerCliId) &&
            (options.UploadUsingProgrammer || programmer.Id != "default"))
        {
            args.Add("-P");
            args.Add(effectiveProgrammerCliId);
        }

        if (options.UploadSpeedBaud > 0 &&
            options.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560)
        {
            args.Add("--upload-property");
            args.Add($"upload.speed={options.UploadSpeedBaud.ToString(CultureInfo.InvariantCulture)}");
        }

        if (options.VerboseOutput)
            args.Add("-v");

        args.Add(sketchDir);
        return args.ToArray();
    }

    public static (string ExecutableBaseName, string[] Arguments) BuildMicroPythonTransferCommand(
        string preferredTool,
        string rawPort,
        string scriptFilePath,
        int baudRate)
    {
        string tool = string.IsNullOrWhiteSpace(preferredTool) ? "auto" : preferredTool.Trim().ToLowerInvariant();
        return tool switch
        {
            "ampy" => ("ampy", new[] { "--port", rawPort, "--baud", baudRate.ToString(CultureInfo.InvariantCulture), "put", scriptFilePath, "main.py" }),
            "rshell" => ("rshell", new[] { "-p", rawPort, "-b", baudRate.ToString(CultureInfo.InvariantCulture), "cp", scriptFilePath, "/pyboard/main.py" }),
            "esptool" => ("esptool", new[] { "--port", rawPort, "--baud", baudRate.ToString(CultureInfo.InvariantCulture), "chip_id" }),
            _ => ("mpremote", new[] { "connect", rawPort, "fs", "cp", scriptFilePath, ":main.py", "+", "reset" })
        };
    }

    public static IReadOnlyList<BoxCodeDiagnostic> ParseArduinoCliDiagnostics(string? output)
    {
        var diagnostics = new List<BoxCodeDiagnostic>();
        if (string.IsNullOrWhiteSpace(output))
            return diagnostics;

        string[] lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines)
        {
            Match match = GccDiagnosticRegex().Match(line);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineNum))
                lineNum = 1;
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int colNum))
                colNum = 1;

            string level = match.Groups[3].Value.ToLowerInvariant();
            string message = match.Groups[4].Value.Trim();
            BoxCodeDiagnosticSeverity severity = level switch
            {
                "error" or "fatal error" => BoxCodeDiagnosticSeverity.Error,
                "warning" => BoxCodeDiagnosticSeverity.Warning,
                _ => BoxCodeDiagnosticSeverity.Info
            };

            diagnostics.Add(new BoxCodeDiagnostic(
                Math.Max(1, lineNum),
                Math.Max(1, colNum),
                severity,
                "CLI-GCC",
                message,
                "Fix the compiler error reported by arduino-cli / GCC."));
        }

        return diagnostics;
    }

    public static IReadOnlyList<BoxCodeDiagnostic> ParsePythonSyntaxDiagnostics(string? stderrOutput)
    {
        var diagnostics = new List<BoxCodeDiagnostic>();
        if (string.IsNullOrWhiteSpace(stderrOutput))
            return diagnostics;

        string normalized = stderrOutput.Replace("\r\n", "\n");
        Match fileLine = PythonTracebackLineRegex().Match(normalized);
        int lineNum = fileLine.Success && int.TryParse(fileLine.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Max(1, parsed)
            : 1;

        Match errMatch = PythonSyntaxErrRegex().Match(normalized);
        if (errMatch.Success)
        {
            diagnostics.Add(new BoxCodeDiagnostic(
                lineNum,
                1,
                BoxCodeDiagnosticSeverity.Error,
                "PY-AST",
                errMatch.Groups[1].Value.Trim(),
                "Fix the Python syntax or indentation error flagged by Python AST parser."));
        }

        return diagnostics;
    }

    [GeneratedRegex(@"\b(COM\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsComPortRegex();

    [GeneratedRegex(@"^(/dev/tty\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnixTtyPortRegex();

    [GeneratedRegex(@"[^:\n]+:(\d+):(\d+):\s*(fatal error|error|warning|note):\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GccDiagnosticRegex();

    [GeneratedRegex(@"line\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonTracebackLineRegex();

    [GeneratedRegex(@"((?:SyntaxError|IndentationError|TabError|NameError)\s*:\s*.+)$", RegexOptions.Multiline)]
    private static partial Regex PythonSyntaxErrRegex();
}
