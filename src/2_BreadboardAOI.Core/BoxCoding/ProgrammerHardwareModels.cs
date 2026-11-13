using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

public enum UsbCableKind
{
    None = 0,
    UsbTypeC = 1,
    UsbB = 2,
    MicroUsb = 3
}

public sealed record UsbCableSpec(
    UsbCableKind Kind,
    string DisplayName,
    string ShortTag,
    string ConnectorCode,
    string AccentHex,
    string JacketHex,
    string Description,
    IReadOnlyList<MicrocontrollerBoardType> NativeBoards,
    string ShellStyleId = "standard");

public sealed record WaveformSample(
    double TimeMs,
    string ChannelName,
    double Voltage,
    bool IsDigitalHigh,
    int PwmDuty);

public sealed record VirtualFirmwareRuntimeState(
    bool IsRunning,
    MicrocontrollerBoardType BoardType,
    string ActiveBoardName,
    UsbCableKind CableKind,
    string LcdLine1,
    string LcdLine2,
    IReadOnlyDictionary<string, bool> DigitalPinStates,
    IReadOnlyDictionary<string, int> PwmPinValues,
    bool IsBuzzerActive,
    int BuzzerFrequencyHz,
    IReadOnlyList<string> SerialMonitorLines,
    IReadOnlyList<WaveformSample> WaveformSamples,
    int TickCount);

public sealed record ProgrammerUploadResult(
    bool Success,
    string BoardDisplayName,
    UsbCableKind CableKind,
    string TargetPort,
    bool UsedHardwareCli,
    string LogOutput,
    BoxCodeAnalysisReport? CodeAnalysis,
    VirtualFirmwareRuntimeState? InitialRuntimeState);

/// <summary>Catalog of supported USB programmer cables, connector specs, and board port mappings.</summary>
public static class ProgrammerCatalog
{
    private static readonly UsbCableSpec DisconnectedSpec = new(
        UsbCableKind.None,
        "No USB Cable Attached",
        "NONE",
        "Disconnected",
        "#64748B",
        "#0F172A",
        "Attach a USB Type-B, Anker 100W USB-C, or Micro-USB cable on the 2.5D rack to begin.",
        Array.Empty<MicrocontrollerBoardType>(),
        "none");

    public static IReadOnlyList<UsbCableSpec> AllCables { get; } = new[]
    {
        new UsbCableSpec(
            UsbCableKind.UsbTypeC,
            "Anker 100W USB-C (Diamond Braided)",
            "USB-C 100W",
            "Anker 100W Braided USB-C 24-Pin",
            "#00F0FF",
            "#1E222A",
            "Anker 100W black-and-silver diamond-braided nylon USB-C to USB-C cable with metallic charcoal ANKER barrels & stepped 100W boots (native ESP32-S2 Mini).",
            new[] { MicrocontrollerBoardType.WemosEsp32S2Mini },
            "anker_100w_braided"),

        new UsbCableSpec(
            UsbCableKind.UsbB,
            "USB Type-A → B (Orange-Emblem Shielded)",
            "USB-A→B",
            "Type-A to Type-B Molded (Orange Emblem)",
            "#FF6D00",
            "#14161B",
            "Glossy black shielded USB Type-A to Type-B cable with charcoal molded barrels, bright orange oval USB emblems & 3-ribbed relief boots (native Arduino Uno & Mega 2560).",
            new[] { MicrocontrollerBoardType.ArduinoUnoR3R4, MicrocontrollerBoardType.ArduinoMega2560 },
            "black_orange_emblem"),

        new UsbCableSpec(
            UsbCableKind.MicroUsb,
            "Micro-USB 2.0 Data Cable",
            "MICRO-USB",
            "Micro-B 5-Pin Shielded",
            "#FF9100",
            "#1C2430",
            "Data-sync Micro-USB cable native to ESP32 DevKit v1 (CP2102/CH340) and STM32 Blue Pill.",
            new[] { MicrocontrollerBoardType.Esp32DevKitV1, MicrocontrollerBoardType.Stm32BluePillF103C8 },
            "micro_shielded")
    };

    public static UsbCableKind GetRecommendedCable(MicrocontrollerBoardType board) => board switch
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 => UsbCableKind.UsbB,
        MicrocontrollerBoardType.Esp32DevKitV1 or MicrocontrollerBoardType.Stm32BluePillF103C8 => UsbCableKind.MicroUsb,
        MicrocontrollerBoardType.WemosEsp32S2Mini => UsbCableKind.UsbTypeC,
        _ => UsbCableKind.UsbTypeC
    };

    public static UsbCableSpec GetCableSpec(UsbCableKind kind)
    {
        if (kind == UsbCableKind.None)
            return DisconnectedSpec;

        foreach (var spec in AllCables)
        {
            if (spec.Kind == kind)
                return spec;
        }

        return DisconnectedSpec;
    }

    public static bool RequiresAdapter(UsbCableKind cable, MicrocontrollerBoardType board)
    {
        if (cable == UsbCableKind.None || board == MicrocontrollerBoardType.None)
            return false;

        return !GetCableSpec(cable).NativeBoards.Contains(board);
    }

    public static bool IsCableCompatible(
        UsbCableKind cable,
        MicrocontrollerBoardType board,
        out bool isNativeMatch,
        out string compatibilityNote)
        => IsCableCompatible(cable, board, adapterModeEnabled: true, out isNativeMatch, out compatibilityNote);

    public static bool IsCableCompatible(
        UsbCableKind cable,
        MicrocontrollerBoardType board,
        bool adapterModeEnabled,
        out bool isNativeMatch,
        out string compatibilityNote)
    {
        if (cable == UsbCableKind.None)
        {
            isNativeMatch = false;
            compatibilityNote = "No USB cable connected. Drag or click a cable from the 2.5D Cable Rack to the dev board.";
            return false;
        }

        UsbCableSpec spec = GetCableSpec(cable);
        isNativeMatch = spec.NativeBoards.Contains(board);

        if (isNativeMatch)
        {
            compatibilityNote = $"Native direct connection via {spec.DisplayName} ({spec.ConnectorCode}).";
            return true;
        }

        UsbCableSpec recommended = GetCableSpec(GetRecommendedCable(board));
        if (!adapterModeEnabled)
        {
            compatibilityNote = $"Plug mismatch: {spec.ShortTag} does not fit {FormatBoardShortName(board)}'s {recommended.ShortTag} jack. Switch cable or enable USB Adapter.";
            return false;
        }

        compatibilityNote = $"Connected via USB OTG/Type-C adapter ({spec.ShortTag} -> {recommended.ShortTag}).";
        return true;
    }

    public static string GetBoardPortDescription(
        MicrocontrollerBoardType board,
        UsbCableKind cable,
        string comPort = "COM3 (Virtual USB-UART)")
    {
        string boardLabel = FormatBoardShortName(board);
        UsbCableSpec spec = GetCableSpec(cable);
        return cable == UsbCableKind.None
            ? $"{boardLabel} · Unplugged ({comPort})"
            : $"{boardLabel} · {spec.ShortTag} · {comPort}";
    }

    internal static string FormatBoardShortName(MicrocontrollerBoardType board) => board switch
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4 => "Arduino Uno R3/R4",
        MicrocontrollerBoardType.ArduinoMega2560 => "Arduino Mega 2560",
        MicrocontrollerBoardType.Esp32DevKitV1 => "ESP32 DevKit v1",
        MicrocontrollerBoardType.WemosEsp32S2Mini => "Wemos ESP32-S2 Mini",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => "STM32 Blue Pill",
        _ => "Breadboard MCU"
    };
}

/// <summary>Executes static-analyzed C++ or MicroPython firmware step-by-step against virtual breadboard hardware.</summary>
public static class VirtualFirmwareSimulator
{
    private const int WaveformSampleCount = 64;

    public static VirtualFirmwareRuntimeState SimulateFromCode(
        string code,
        TargetProgrammingLanguage language,
        MicrocontrollerBoardType boardType,
        UsbCableKind cableKind,
        int tickIndex,
        IReadOnlyList<string>? userSerialInputs = null) =>
        SimulateFromCode(code, (BoxCodingLanguage)language, boardType, cableKind, tickIndex, userSerialInputs);

    public static VirtualFirmwareRuntimeState SimulateFromCode(
        string code,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        UsbCableKind cableKind,
        int tickIndex,
        IReadOnlyList<string>? userSerialInputs = null)
    {
        string boardName = ProgrammerCatalog.FormatBoardShortName(boardType);
        var analysis = BoxCodeErrorAnalyzer.Analyze(code, language, boardType);

        if (!analysis.IsValid)
        {
            var errLines = new List<string>
            {
                $"[SIM-HALT] {boardName}: {analysis.ErrorCount} compile/syntax error(s) prevent execution."
            };
            foreach (var diag in analysis.Diagnostics.Take(6))
                errLines.Add($"  {diag.DisplaySummary}");

            var flatWave = Enumerable.Range(0, WaveformSampleCount)
                .Select(i => new WaveformSample(i * 0.5, "IDLE", 0.0, false, 0))
                .ToList();

            return new VirtualFirmwareRuntimeState(
                IsRunning: false,
                BoardType: boardType,
                ActiveBoardName: boardName,
                CableKind: cableKind,
                LcdLine1: "COMPILE ERROR",
                LcdLine2: analysis.StatusSummary,
                DigitalPinStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                PwmPinValues: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                IsBuzzerActive: false,
                BuzzerFrequencyHz: 0,
                SerialMonitorLines: errLines,
                WaveformSamples: flatWave,
                TickCount: tickIndex);
        }

        double railVolts = boardType is MicrocontrollerBoardType.Esp32DevKitV1
            or MicrocontrollerBoardType.WemosEsp32S2Mini
            or MicrocontrollerBoardType.Stm32BluePillF103C8
            ? 3.3
            : 5.0;

        var digitalPinTimelines = new Dictionary<string, List<bool>>(StringComparer.OrdinalIgnoreCase);
        var pwmPins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var serialLines = new List<string>();

        string lcdLine1 = "AOI VIRTUAL MCU";
        string lcdLine2 = $"Tick #{tickIndex} READY";
        bool buzzerActive = false;
        int buzzerHz = 0;

        foreach (var act in analysis.ExtractedActions)
        {
            if (act.IsOutput && !act.IsPwm && act.BuzzerHz == 0 && act.PinName != "I2C_LCD")
            {
                if (!digitalPinTimelines.TryGetValue(act.PinName, out var list))
                {
                    list = new List<bool>();
                    digitalPinTimelines[act.PinName] = list;
                }
                list.Add(act.IsDigitalHigh);
            }

            if (act.IsPwm)
            {
                pwmPins[act.PinName] = Math.Clamp(act.PwmDuty, 0, 255);
            }

            if (act.BuzzerHz > 0)
            {
                buzzerActive = true;
                buzzerHz = act.BuzzerHz;
            }

            if (!string.IsNullOrWhiteSpace(act.LcdLine1))
                lcdLine1 = ResolveDynamicLcdToken(act.LcdLine1, tickIndex);
            if (!string.IsNullOrWhiteSpace(act.LcdLine2))
                lcdLine2 = ResolveDynamicLcdToken(act.LcdLine2, tickIndex);

            if (!string.IsNullOrWhiteSpace(act.SerialText))
            {
                double tSec = Math.Max(0, tickIndex) * 0.25;
                serialLines.Add($"[{tSec:00.00}s] {act.SerialText}");
            }
        }

        var digitalStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int safeTick = Math.Abs(tickIndex);
        foreach (var (pin, timeline) in digitalPinTimelines)
        {
            if (timeline.Count >= 2)
                digitalStates[pin] = timeline[safeTick % timeline.Count];
            else if (timeline.Count == 1)
                digitalStates[pin] = timeline[0];
        }

        if (userSerialInputs != null)
        {
            foreach (string input in userSerialInputs)
            {
                if (string.IsNullOrWhiteSpace(input)) continue;
                string trimmed = input.Trim();
                serialLines.Add($"> TX: {trimmed}");
                serialLines.Add($"< RX [{boardName}]: ACK '{trimmed}' (tick={tickIndex})");
            }
        }

        if (serialLines.Count == 0)
        {
            serialLines.Add($"[{safeTick * 0.25:00.00}s] {boardName} running virtual firmware loop.");
        }

        // Generate 64 oscilloscope waveform samples
        string waveChannel = pwmPins.Keys.FirstOrDefault()
            ?? digitalStates.Keys.FirstOrDefault()
            ?? (buzzerActive ? $"BUZ({buzzerHz}Hz)" : "GPIO");

        int wavePwm = pwmPins.TryGetValue(waveChannel, out int dutyVal)
            ? dutyVal
            : (digitalStates.TryGetValue(waveChannel, out bool hi) ? (hi ? 255 : 0) : (buzzerActive ? 128 : 0));

        bool hasTimeToggle = pwmPins.ContainsKey(waveChannel) ||
                             buzzerActive ||
                             (digitalPinTimelines.TryGetValue(waveChannel, out var tl) && tl.Count >= 2);

        double dutyRatio = Math.Clamp(wavePwm / 255.0, 0.0, 1.0);
        const int periodSamples = 16;
        var samples = new List<WaveformSample>(WaveformSampleCount);

        for (int s = 0; s < WaveformSampleCount; s++)
        {
            double timeMs = s * 0.5;
            bool sampleHigh;
            if (pwmPins.ContainsKey(waveChannel) || buzzerActive)
            {
                int phase = (s + safeTick * 2) % periodSamples;
                sampleHigh = phase < Math.Max(1, (int)Math.Round(dutyRatio * periodSamples));
            }
            else if (hasTimeToggle)
            {
                int blockIdx = ((s / 16) + safeTick) % 2;
                sampleHigh = blockIdx == 0;
            }
            else
            {
                sampleHigh = digitalStates.TryGetValue(waveChannel, out bool state) && state;
            }

            samples.Add(new WaveformSample(
                TimeMs: timeMs,
                ChannelName: waveChannel,
                Voltage: sampleHigh ? railVolts : 0.0,
                IsDigitalHigh: sampleHigh,
                PwmDuty: wavePwm));
        }

        return new VirtualFirmwareRuntimeState(
            IsRunning: true,
            BoardType: boardType,
            ActiveBoardName: boardName,
            CableKind: cableKind,
            LcdLine1: lcdLine1.Length > 16 ? lcdLine1[..16] : lcdLine1,
            LcdLine2: lcdLine2.Length > 16 ? lcdLine2[..16] : lcdLine2,
            DigitalPinStates: digitalStates,
            PwmPinValues: pwmPins,
            IsBuzzerActive: buzzerActive,
            BuzzerFrequencyHz: buzzerHz,
            SerialMonitorLines: serialLines,
            WaveformSamples: samples,
            TickCount: tickIndex);
    }

    private static string ResolveDynamicLcdToken(string text, int tickIndex)
    {
        double tempSim = 23.4 + ((tickIndex % 5) * 0.3);
        double distSim = 18.0 + ((tickIndex % 7) * 2.5);
        return text
            .Replace("{temp}", tempSim.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{hum}", (48 + (tickIndex % 6)).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{dist}", distSim.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }
}
