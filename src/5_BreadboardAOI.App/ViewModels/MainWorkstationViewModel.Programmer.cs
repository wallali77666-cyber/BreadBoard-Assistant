// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.Programmer.cs
// PURPOSE: Partial class managing USB hardware programmer selection, cable
//          attachment, firmware analysis/flashing UI, live virtual firmware
//          execution (Serial Monitor, PWM/Logic Oscilloscope, CAD LED/LCD/
//          Buzzer sync), and Experimental Lab (LAB EXP) tools.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    private readonly List<string> _userSerialInputs = new();
    private DispatcherTimer? _virtualFirmwareTimer;
    private ProgrammerUploadDialog? _programmerUploadDialog;
    private int _firmwareTick;
    private string _activeSimulatedCode = string.Empty;
    private BoxCodingLanguage _activeSimulatedLanguage = BoxCodingLanguage.ArduinoCpp;

    // ---- USB Cable & Hardware Programmer State -----------------------------

    public IReadOnlyList<UsbCableSpec> AvailableUsbCables { get; } = ProgrammerCatalog.AllCables;

    [ObservableProperty]
    private UsbCableKind _selectedUsbCableKind = UsbCableKind.UsbB;

    [ObservableProperty]
    private bool _isProgrammerCableConnected;

    [ObservableProperty]
    private bool _showHardwareProgrammer = true;

    [ObservableProperty]
    private string _programmerStatusText = "DISCONNECTED — Select a USB cable and click CONNECT CABLE";

    [ObservableProperty]
    private string _connectedBoardSummary = "No board connected";

    [ObservableProperty]
    private string _cableCompatibilityNote = "USB Type-B selected";

    [ObservableProperty]
    private bool _isProgrammerUploading;

    [ObservableProperty]
    private double _programmerUploadProgress;

    [ObservableProperty]
    private string _selectedProgrammerComPort = "VIRTUAL-SIM (Live Breadboard Execution)";

    [ObservableProperty]
    private bool _isVirtualFirmwareRunning;

    [ObservableProperty]
    private string _activeFirmwareCode = string.Empty;

    [ObservableProperty]
    private BoxCodingLanguage _activeFirmwareLanguage = BoxCodingLanguage.ArduinoCpp;

    // ---- Live Virtual Firmware / Serial / Oscilloscope Telemetry -----------

    [ObservableProperty]
    private string _virtualSerialLog = "[SERIAL @ 115200] Ready — connect USB cable & upload firmware for live breadboard execution.\n";

    [ObservableProperty]
    private string _serialInputCommandText = string.Empty;

    [ObservableProperty]
    private string _oscilloscopeSummaryText = "CH1: IDLE (0 Hz, 0% Duty)";

    [ObservableProperty]
    private string _oscilloscopeWaveformPoints = "0,48 220,48";

    public ObservableCollection<WaveformSample> OscilloscopeSamples { get; } = new();

    // ---- Experimental Lab (LAB EXP): Thermal / Auto-Fix / AI Prompt --------

    [ObservableProperty]
    private bool _showThermalHeatmap;

    [ObservableProperty]
    private string _thermalSummaryText = "Thermal overlay off";

    [ObservableProperty]
    private string _experimentalCircuitPrompt = "Ultrasonic distance radar with buzzer";

    [ObservableProperty]
    private string _experimentalLabStatus = "Ready — Choose an experimental lab tool below.";

    public IReadOnlyList<string> PresetCircuitPrompts { get; } = new[]
    {
        "Ultrasonic distance radar with buzzer",
        "DHT11 temperature weather station with LCD",
        "Traffic light with red yellow green LEDs",
        "555 timer astable blinker",
        "Push button doorbell alarm"
    };

    public bool IsTypeCCableSelected => SelectedUsbCableKind == UsbCableKind.UsbTypeC;

    public bool IsTypeBCableSelected => SelectedUsbCableKind == UsbCableKind.UsbB;

    public bool IsMicroUsbCableSelected => SelectedUsbCableKind == UsbCableKind.MicroUsb;

    public UsbCableSpec SelectedCableSpec => ProgrammerCatalog.GetCableSpec(SelectedUsbCableKind);

    public string SelectedCableBadgeText => SelectedCableSpec.DisplayName;

    public string ConnectCableButtonLabel => IsProgrammerCableConnected ? "DISCONNECT CABLE" : "CONNECT CABLE";

    public string FirmwareStatusBadge => IsProgrammerUploading
        ? "FLASHING FIRMWARE..."
        : IsVirtualFirmwareRunning
            ? "RUNNING ON BOARD"
            : IsProgrammerCableConnected
                ? "USB CONNECTED (READY)"
                : "NO USB LINK";

    // ---- Observable Property Change Hooks ----------------------------------

    partial void OnSelectedUsbCableKindChanged(UsbCableKind value)
    {
        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();
        ProgrammerCatalog.IsCableCompatible(value, effectiveBoard, out _, out string note);
        CableCompatibilityNote = note;

        UsbCableSpec spec = ProgrammerCatalog.GetCableSpec(value);
        ConnectedBoardSummary = IsProgrammerCableConnected
            ? ProgrammerCatalog.GetBoardPortDescription(effectiveBoard, value, SelectedProgrammerComPort)
            : "No board connected";
        ProgrammerStatusText = IsProgrammerCableConnected
            ? $"CONNECTED · {ActiveDevBoardName} via {spec.DisplayName} (READY FOR UPLOAD)"
            : $"{spec.DisplayName} selected — click CONNECT CABLE";

        NotifyProgrammerComputedProperties();
        SyncProgrammerToRenderer();
    }

    partial void OnIsProgrammerCableConnectedChanged(bool value)
    {
        if (!value && IsVirtualFirmwareRunning)
            StopVirtualFirmware();

        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();
        UsbCableSpec spec = ProgrammerCatalog.GetCableSpec(SelectedUsbCableKind);
        ConnectedBoardSummary = value
            ? ProgrammerCatalog.GetBoardPortDescription(effectiveBoard, SelectedUsbCableKind, SelectedProgrammerComPort)
            : "No board connected";
        ProgrammerStatusText = value
            ? $"CONNECTED · {ActiveDevBoardName} via {spec.DisplayName} (READY FOR UPLOAD)"
            : "DISCONNECTED";

        NotifyProgrammerComputedProperties();
        SyncProgrammerToRenderer();
    }

    partial void OnShowHardwareProgrammerChanged(bool value)
    {
        _renderer.Layers.ShowHardwareProgrammer = value;
        SyncProgrammerToRenderer();
    }

    partial void OnIsProgrammerUploadingChanged(bool value)
    {
        OnPropertyChanged(nameof(FirmwareStatusBadge));
        SyncProgrammerToRenderer();
    }

    partial void OnProgrammerUploadProgressChanged(double value)
    {
        _renderer.ProgrammerUploadProgress = (float)Math.Clamp(value / 100.0, 0.0, 1.0);
    }

    partial void OnIsVirtualFirmwareRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(FirmwareStatusBadge));
        SyncProgrammerToRenderer();
    }

    partial void OnShowThermalHeatmapChanged(bool value)
    {
        _renderer.Layers.ShowThermalHeatmap = value;
        RefreshThermalHeatmap();
        StatusLine = value
            ? $"Thermal Heatmap ENABLED — {ThermalSummaryText}"
            : "Thermal Heatmap DISABLED.";
    }

    // ---- Board / Cable Synchronization -------------------------------------

    public void OnDevBoardTypeChangedForProgrammer(MicrocontrollerBoardType boardType)
    {
        MicrocontrollerBoardType effectiveBoard = boardType == MicrocontrollerBoardType.None
            ? MicrocontrollerBoardType.ArduinoUnoR3R4
            : boardType;

        UsbCableKind recommended = ProgrammerCatalog.GetRecommendedCable(effectiveBoard);
        if (recommended != UsbCableKind.None && SelectedUsbCableKind != recommended)
        {
            SelectedUsbCableKind = recommended;
            return;
        }

        ProgrammerCatalog.IsCableCompatible(SelectedUsbCableKind, effectiveBoard, out _, out string note);
        CableCompatibilityNote = note;
        ConnectedBoardSummary = IsProgrammerCableConnected
            ? ProgrammerCatalog.GetBoardPortDescription(effectiveBoard, SelectedUsbCableKind, SelectedProgrammerComPort)
            : "No board connected";
        NotifyProgrammerComputedProperties();
        SyncProgrammerToRenderer();
    }

    public void SyncProgrammerToRenderer()
    {
        _renderer.Layers.ShowHardwareProgrammer = ShowHardwareProgrammer;
        _renderer.Layers.ShowThermalHeatmap = ShowThermalHeatmap;
        _renderer.ActiveUsbCableKind = SelectedUsbCableKind;
        _renderer.IsProgrammerConnected = IsProgrammerCableConnected;
        _renderer.IsProgrammerUploading = IsProgrammerUploading;
        _renderer.ProgrammerUploadProgress = (float)Math.Clamp(ProgrammerUploadProgress / 100.0, 0.0, 1.0);
    }

    // ---- Cable & Programmer Commands ---------------------------------------

    [RelayCommand]
    public void SelectUsbCable(string? cableParam)
    {
        if (string.IsNullOrWhiteSpace(cableParam))
            return;

        string normalized = cableParam.Trim().ToLowerInvariant();
        UsbCableKind target = normalized switch
        {
            "typec" or "type-c" or "usb-c" or "usbc" or "usbtypec" => UsbCableKind.UsbTypeC,
            "usbb" or "usb-b" or "typeb" or "type-b" or "usbbprinter" => UsbCableKind.UsbB,
            "micro" or "micro-usb" or "microusb" => UsbCableKind.MicroUsb,
            "none" => UsbCableKind.None,
            _ => Enum.TryParse(cableParam, ignoreCase: true, out UsbCableKind parsed)
                ? parsed
                : SelectedUsbCableKind
        };

        SelectedUsbCableKind = target;
        ShowHardwareProgrammer = target != UsbCableKind.None;
    }

    [RelayCommand]
    public void ConnectUsbCable()
    {
        if (SelectedDevBoardType == MicrocontrollerBoardType.None)
            SelectedDevBoardType = MicrocontrollerBoardType.Esp32DevKitV1;

        IsDevBoardDocked = true;
        ShowHardwareProgrammer = true;

        if (SelectedUsbCableKind == UsbCableKind.None)
            SelectedUsbCableKind = ProgrammerCatalog.GetRecommendedCable(SelectedDevBoardType);

        ProgrammerCatalog.IsCableCompatible(
            SelectedUsbCableKind,
            SelectedDevBoardType,
            out _,
            out string note);

        CableCompatibilityNote = note;
        ConnectedBoardSummary = ProgrammerCatalog.GetBoardPortDescription(
            SelectedDevBoardType,
            SelectedUsbCableKind,
            "COM-SIM");

        IsProgrammerCableConnected = true;
        ProgrammerStatusText =
            $"CONNECTED · {ActiveDevBoardName} via {ProgrammerCatalog.GetCableSpec(SelectedUsbCableKind).DisplayName} (READY FOR UPLOAD)";
        StatusLine = ProgrammerStatusText;
        SyncProgrammerToRenderer();
    }

    [RelayCommand]
    public void DisconnectUsbCable()
    {
        _virtualFirmwareTimer?.Stop();
        IsVirtualFirmwareRunning = false;
        _renderer.ActiveFirmwareState = null;
        IsProgrammerCableConnected = false;
        ConnectedBoardSummary = "No board connected";
        ProgrammerStatusText = "DISCONNECTED";
        StatusLine = "USB Programmer cable disconnected.";
        SyncProgrammerToRenderer();
    }

    [RelayCommand]
    public void ToggleUsbCableConnection()
    {
        if (IsProgrammerCableConnected)
            DisconnectUsbCable();
        else
            ConnectUsbCable();
    }

    [RelayCommand]
    public void OpenProgrammerUploadDialog()
    {
        OpenProgrammerUploadDialogCore(overrideCode: null, overrideLanguage: null);
    }

    public void OpenProgrammerUploadDialogCore(string? overrideCode, BoxCodingLanguage? overrideLanguage = null)
    {
        try
        {
            if (!IsProgrammerCableConnected)
                ConnectUsbCable();

            if (overrideLanguage.HasValue)
                ActiveFirmwareLanguage = overrideLanguage.Value;

            string codeToEdit = ResolveUploadFirmwareCode(overrideCode, ActiveFirmwareLanguage);
            ActiveFirmwareCode = codeToEdit;

            if (_programmerUploadDialog is { IsLoaded: true })
            {
                _programmerUploadDialog.LoadFirmwareContext(codeToEdit, ActiveFirmwareLanguage);
                if (_programmerUploadDialog.WindowState == WindowState.Minimized)
                    _programmerUploadDialog.WindowState = WindowState.Normal;
                _programmerUploadDialog.Activate();
                return;
            }

            _programmerUploadDialog = new ProgrammerUploadDialog(this, codeToEdit, ActiveFirmwareLanguage);
            if (Application.Current?.MainWindow is { IsLoaded: true } ownerWindow)
                _programmerUploadDialog.Owner = ownerWindow;

            _programmerUploadDialog.Closed += (_, _) => _programmerUploadDialog = null;
            _programmerUploadDialog.Show();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"Unable to open Firmware Programmer dialog: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task<ProgrammerUploadResult> UploadFirmwareToBoardAsync()
    {
        string code = ResolveUploadFirmwareCode(ActiveFirmwareCode, ActiveFirmwareLanguage);
        ActiveFirmwareCode = code;
        return await UploadFirmwareCoreAsync(code, ActiveFirmwareLanguage, SelectedProgrammerComPort)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Validates firmware with <see cref="BoxCodeErrorAnalyzer"/>, animates the
    /// compile/flash sequence onto the CAD programmer/USB cable, and starts
    /// live 250 ms virtual firmware execution upon completion.
    /// </summary>
    public async Task<ProgrammerUploadResult> UploadFirmwareCoreAsync(
        string code,
        BoxCodingLanguage language,
        string targetPort)
    {
        if (!IsProgrammerCableConnected)
            ConnectUsbCable();

        MicrocontrollerBoardType boardType = GetEffectiveTargetBoard();
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(boardType);

        string effectivePort = string.IsNullOrWhiteSpace(targetPort)
            ? (string.IsNullOrWhiteSpace(SelectedProgrammerComPort)
                ? "VIRTUAL-SIM (Live Breadboard Execution)"
                : SelectedProgrammerComPort)
            : targetPort.Trim();

        ActiveFirmwareCode = code;
        ActiveFirmwareLanguage = language;
        SelectedProgrammerComPort = effectivePort;

        IsProgrammerUploading = true;
        ProgrammerUploadProgress = 0;

        var log = new StringBuilder();
        log.AppendLine($"[1/4] Verifying syntax & pin rules for {profile.DisplayName} ({language})...");

        IReadOnlyCollection<string> wiredPins = ExtractWiredMcuPins();
        BoxCodeAnalysisReport analysis = BoxCodeErrorAnalyzer.Analyze(code, language, boardType, wiredPins);
        if (analysis.ErrorCount > 0)
        {
            foreach (BoxCodeDiagnostic diag in analysis.Diagnostics)
                log.AppendLine($"  [{diag.SeverityLabel}] {diag.DisplaySummary}");

            BoxCodeDiagnostic first = analysis.Diagnostics[0];
            IsProgrammerUploading = false;
            ProgrammerUploadProgress = 0;
            ProgrammerStatusText = $"COMPILE FAILED: {first.Code} — {first.Message}";
            AppendSerialLog($"[COMPILE FAILED] {first.Code}: {first.Message}");
            StatusLine = ProgrammerStatusText;

            return new ProgrammerUploadResult(
                Success: false,
                BoardDisplayName: profile.DisplayName,
                CableKind: SelectedUsbCableKind,
                TargetPort: effectivePort,
                UsedHardwareCli: false,
                LogOutput: log.ToString(),
                CodeAnalysis: analysis,
                InitialRuntimeState: null);
        }

        ProgrammerUploadProgress = 35;
        ProgrammerStatusText = $"[2/4] Compiling sketch for {profile.DisplayName}...";
        log.AppendLine($"[2/4] Compiling sketch for {profile.DisplayName} ({analysis.WarningCount} warning(s))...");
        await Task.Delay(110).ConfigureAwait(true);
        if (_disposed)
        {
            IsProgrammerUploading = false;
            return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
        }

        ProgrammerUploadProgress = 75;
        ProgrammerStatusText = $"[3/4] Connecting over {SelectedCableSpec.DisplayName} ({effectivePort})...";
        log.AppendLine($"[3/4] Connecting over {ProgrammerCatalog.GetBoardPortDescription(boardType, SelectedUsbCableKind, effectivePort)}...");
        await Task.Delay(140).ConfigureAwait(true);
        if (_disposed)
        {
            IsProgrammerUploading = false;
            return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
        }

        bool isPhysicalComPort =
            !effectivePort.StartsWith("VIRTUAL-SIM", StringComparison.OrdinalIgnoreCase) &&
            !effectivePort.Equals("COM-SIM", StringComparison.OrdinalIgnoreCase);

        bool usedHardwareCli = false;
        if (isPhysicalComPort)
        {
            (bool executedAndSucceeded, string cliOutput) =
                await TryFlashWithArduinoCliAsync(code, language, boardType, effectivePort).ConfigureAwait(true);
            if (_disposed)
            {
                IsProgrammerUploading = false;
                return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
            }

            usedHardwareCli = executedAndSucceeded;
            if (!string.IsNullOrWhiteSpace(cliOutput))
                log.AppendLine(cliOutput);
        }

        ProgrammerUploadProgress = 100;
        IsProgrammerUploading = false;

        VirtualFirmwareRuntimeState initialState = StartVirtualFirmwareSimulation();

        log.AppendLine(usedHardwareCli
            ? $"[4/4] Hardware flashing complete on {effectivePort} & live virtual execution running on {profile.DisplayName}!"
            : $"[4/4] Live virtual execution running on {profile.DisplayName}!");
        ProgrammerStatusText = $"UPLOAD COMPLETE · Running on {ActiveDevBoardName}";
        StatusLine = ProgrammerStatusText;

        return new ProgrammerUploadResult(
            Success: true,
            BoardDisplayName: profile.DisplayName,
            CableKind: SelectedUsbCableKind,
            TargetPort: effectivePort,
            UsedHardwareCli: usedHardwareCli,
            LogOutput: log.ToString(),
            CodeAnalysis: analysis,
            InitialRuntimeState: initialState);
    }

    private static async Task<(bool ExecutedAndSucceeded, string OutputLog)> TryFlashWithArduinoCliAsync(
        string code,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        string port)
    {
        string? arduinoCliPath = language == BoxCodingLanguage.ArduinoCpp
            ? FindExecutableInPath("arduino-cli.exe", "arduino-cli")
            : null;
        if (string.IsNullOrWhiteSpace(arduinoCliPath))
            return (false, $"[INFO] arduino-cli not found in PATH — running live virtual execution on {boardType}.");

        string? fqbn = ResolveArduinoCliFqbn(boardType);
        if (string.IsNullOrWhiteSpace(fqbn))
            return (false, $"[INFO] Hardware CLI upload unavailable/failed on {port} — falling back to live virtual execution.");

        string sketchDir = Path.Combine(Path.GetTempPath(), "BreadboardAOI_Sketch_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(sketchDir);
            string sketchName = Path.GetFileName(sketchDir);
            string sketchFile = Path.Combine(sketchDir, sketchName + ".ino");
            await File.WriteAllTextAsync(sketchFile, code ?? string.Empty).ConfigureAwait(false);

            var cliLog = new StringBuilder();
            TimeSpan commandTimeout = TimeSpan.FromSeconds(15);

            string[][] steps =
            {
                new[] { "compile", "--fqbn", fqbn, sketchDir },
                new[] { "upload", "-p", port, "--fqbn", fqbn, sketchDir }
            };

            foreach (string[] args in steps)
            {
                (bool stepOk, string stepOut) = await RunCliProcessAsync(
                    arduinoCliPath,
                    args,
                    commandTimeout).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stepOut))
                    cliLog.AppendLine(stepOut);
                if (!stepOk)
                    return (false, $"[INFO] Hardware CLI upload unavailable/failed on {port} — falling back to live virtual execution.");
            }

            return (true, cliLog.ToString().TrimEnd());
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return (false, $"[INFO] Hardware CLI upload unavailable/failed on {port} — falling back to live virtual execution.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(sketchDir))
                    Directory.Delete(sketchDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static string? ResolveArduinoCliFqbn(MicrocontrollerBoardType boardType)
        => boardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "arduino:avr:uno",
            MicrocontrollerBoardType.ArduinoMega2560 => "arduino:avr:mega",
            MicrocontrollerBoardType.Esp32DevKitV1 => "esp32:esp32:esp32",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "esp32:esp32:esp32s2",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "STMicroelectronics:stm32:GenF1",
            _ => null
        };

    private static string? FindExecutableInPath(params string[] executableNames)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (string name in executableNames)
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException)
                {
                    // Skip malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static async Task<(bool Succeeded, string Output)> RunCliProcessAsync(
        string executablePath,
        string[] arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return (false, string.Empty);

        using var cts = new CancellationTokenSource(timeout);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            string stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            string combined = string.IsNullOrEmpty(stderr)
                ? stdout
                : string.IsNullOrEmpty(stdout) ? stderr : stdout + Environment.NewLine + stderr;

            return (process.ExitCode == 0, combined);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill races on process teardown.
            }

            return (false, string.Empty);
        }
    }

    private VirtualFirmwareRuntimeState StartVirtualFirmwareSimulation()
    {
        _activeSimulatedCode = ActiveFirmwareCode;
        _activeSimulatedLanguage = ActiveFirmwareLanguage;
        _firmwareTick = 0;

        VirtualFirmwareRuntimeState initial = ExecuteFirmwareSimulationStep();
        EnsureVirtualFirmwareTimer().Start();
        IsVirtualFirmwareRunning = true;
        return initial;
    }

    [RelayCommand]
    public void StopVirtualFirmware()
    {
        _virtualFirmwareTimer?.Stop();
        IsVirtualFirmwareRunning = false;
        _renderer.ActiveFirmwareState = null;
        OscilloscopeSummaryText = "CH1: STOPPED (0 Hz, 0% Duty)";
        OscilloscopeWaveformPoints = "0,48 220,48";
        ProgrammerStatusText = IsProgrammerCableConnected
            ? $"CONNECTED · {ActiveDevBoardName} via {SelectedCableSpec.DisplayName} (HALTED)"
            : "DISCONNECTED";
        AppendSerialLog("[SIM] Virtual firmware execution halted.");
    }

    [RelayCommand]
    public void SendSerialInput()
    {
        if (string.IsNullOrWhiteSpace(SerialInputCommandText))
            return;

        string line = SerialInputCommandText.Trim();
        _userSerialInputs.Add(line);
        AppendSerialLog($"> {line}");
        SerialInputCommandText = string.Empty;

        ExecuteFirmwareSimulationStep();
    }

    [RelayCommand]
    public void ClearSerialMonitor()
    {
        VirtualSerialLog = "[SERIAL @ 115200] Buffer cleared.\n";
    }

    // ---- LAB (EXP) Commands: Auto-Fix, Prompt Synthesizer, Thermal Heatmap --

    [RelayCommand]
    private void AutoFixAllViolations()
    {
        RecordUndoSnapshot("Auto-fix breadboard DRC violations");
        ExperimentalLabEngine.TryAutoFixViolations(
            _draft,
            _coordinator?.LatestPacket,
            out int fixedCount,
            out string summary);

        IsBuildMode = true;
        ClearPendingBuild();
        CommitDraft();
        RefreshThermalHeatmap();

        ExperimentalLabStatus = summary;
        BuilderHint = summary;
        StatusLine = fixedCount > 0 ? summary : "LAB Auto-Fix: no auto-fixable DRC faults detected.";
    }

    [RelayCommand]
    private void GenerateCircuitFromPrompt(string? presetPrompt = null)
    {
        if (!string.IsNullOrWhiteSpace(presetPrompt))
            ExperimentalCircuitPrompt = presetPrompt.Trim();

        string prompt = ExperimentalCircuitPrompt.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ExperimentalLabStatus = "Enter a circuit description first (e.g. 'Ultrasonic distance radar with buzzer').";
            StatusLine = "LAB Circuit Generator: enter a prompt first.";
            return;
        }

        RecordUndoSnapshot($"Generate circuit: {prompt}");
        bool built = ExperimentalLabEngine.BuildCircuitFromPrompt(
            prompt,
            _draft,
            out MicrocontrollerBoardType recBoard,
            out List<BoxBlockItem> blocks,
            out string summary);

        if (built)
        {
            IsBuildMode = true;
            if (recBoard != MicrocontrollerBoardType.None)
            {
                SelectedDevBoardType = recBoard;
                IsDevBoardDocked = true;
            }

            MicrocontrollerBoardType effectiveBoard = recBoard != MicrocontrollerBoardType.None
                ? recBoard
                : GetEffectiveTargetBoard();

            if (blocks.Count > 0)
                ActiveFirmwareCode = BoxCodeGenerator.GenerateCode(blocks, effectiveBoard, ActiveFirmwareLanguage);

            ClearPendingBuild();
            CommitDraft();
            RefreshThermalHeatmap();
        }

        ExperimentalLabStatus = summary;
        BuilderHint = summary;
        StatusLine = summary;
    }

    [RelayCommand]
    private void ToggleThermalHeatmap()
    {
        ShowThermalHeatmap = !ShowThermalHeatmap;
    }

    public void RefreshThermalHeatmap()
    {
        IReadOnlyList<ThermalHotspot> hotspots = ExperimentalLabEngine.ComputeThermalHotspots(
            _draft.Parts,
            _coordinator?.LatestPacket,
            _draft.Matrix);

        _renderer.ActiveThermalHotspots = hotspots;

        if (!ShowThermalHeatmap)
        {
            ThermalSummaryText = "Thermal overlay off";
            return;
        }

        if (hotspots.Count == 0)
        {
            ThermalSummaryText = "0 active thermal nodes";
            return;
        }

        double totalMw = hotspots.Sum(h => h.PowerMilliwatts);
        double maxMw = hotspots.Max(h => h.PowerMilliwatts);
        int overloaded = hotspots.Count(h => h.IsOverloaded);
        ThermalSummaryText = overloaded > 0
            ? $"OVERLOAD ALERT: {overloaded} hotspot(s) · Peak {maxMw:0.#} mW / Total {totalMw:0.#} mW"
            : $"Nominal IR: {hotspots.Count} node(s) · Peak {maxMw:0.#} mW / Total {totalMw:0.#} mW";
    }

    // ---- Internal Helpers ---------------------------------------------------

    private MicrocontrollerBoardType GetEffectiveTargetBoard()
    {
        return SelectedDevBoardType == MicrocontrollerBoardType.None
            ? MicrocontrollerBoardType.ArduinoUnoR3R4
            : SelectedDevBoardType;
    }

    private string ResolveUploadFirmwareCode(string? overrideCode, BoxCodingLanguage language)
    {
        if (!string.IsNullOrWhiteSpace(overrideCode))
            return overrideCode;

        if (_boxCodingStudioWindow is { IsLoaded: true, DataContext: BoxCodingStudioViewModel studioVm }
            && !string.IsNullOrWhiteSpace(studioVm.GeneratedCode))
        {
            return studioVm.GeneratedCode;
        }

        if (!string.IsNullOrWhiteSpace(ActiveFirmwareCode))
            return ActiveFirmwareCode;

        MicrocontrollerBoardType board = GetEffectiveTargetBoard();
        if (_draft.Parts.Count > 0)
        {
            List<BoxBlockItem> detectedBlocks = BoxCodeGenerator.AutoDetectFromCircuit(_draft.Parts, board);
            return BoxCodeGenerator.GenerateCode(detectedBlocks, board, language);
        }

        BoxBlockItem[] starterBlocks =
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, board),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, board),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, board),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SerialPrint, board),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, board)
        };
        return BoxCodeGenerator.GenerateCode(starterBlocks, board, language);
    }

    private IReadOnlyCollection<string> ExtractWiredMcuPins()
    {
        var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OffBoardJumperWire wire in ActiveDevBoardWires)
        {
            CollectPinToken(wire.SourceEndpointKey, pins);
            CollectPinToken(wire.TargetEndpointKey, pins);
        }

        return pins;

        static void CollectPinToken(string key, HashSet<string> target)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("MCU", StringComparison.OrdinalIgnoreCase))
                return;

            int colon = key.LastIndexOf(':');
            if (colon >= 0 && colon < key.Length - 1)
                target.Add(key.Substring(colon + 1));
        }
    }

    private DispatcherTimer EnsureVirtualFirmwareTimer()
    {
        if (_virtualFirmwareTimer is not null)
            return _virtualFirmwareTimer;

        _virtualFirmwareTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _virtualFirmwareTimer.Tick += (_, _) =>
        {
            if (!_disposed && IsVirtualFirmwareRunning)
                ExecuteFirmwareSimulationStep();
        };
        return _virtualFirmwareTimer;
    }

    private VirtualFirmwareRuntimeState ExecuteFirmwareSimulationStep()
    {
        MicrocontrollerBoardType board = GetEffectiveTargetBoard();
        List<string>? userInputs = null;
        if (_userSerialInputs.Count > 0)
        {
            userInputs = new List<string>(_userSerialInputs);
            _userSerialInputs.Clear();
        }

        string codeToSimulate = string.IsNullOrWhiteSpace(_activeSimulatedCode)
            ? ActiveFirmwareCode
            : _activeSimulatedCode;

        VirtualFirmwareRuntimeState state = VirtualFirmwareSimulator.SimulateFromCode(
            codeToSimulate,
            _activeSimulatedLanguage,
            board,
            SelectedUsbCableKind,
            _firmwareTick++,
            userInputs);

        _renderer.ActiveFirmwareState = state;

        foreach (string line in state.SerialMonitorLines)
            AppendSerialLog(line);

        UpdateOscilloscopeTelemetry(state);
        SyncLcdFromFirmwareState(state);

        return state;
    }

    private void SyncLcdFromFirmwareState(VirtualFirmwareRuntimeState state)
    {
        if (string.IsNullOrWhiteSpace(state.LcdLine1) && string.IsNullOrWhiteSpace(state.LcdLine2))
            return;

        bool circuitHasLcd = _draft.Parts.Any(p =>
            (p.Value ?? p.SymbolId ?? string.Empty).Contains("LCD", StringComparison.OrdinalIgnoreCase)
            || (p.Value ?? p.SymbolId ?? string.Empty).Contains("1602", StringComparison.OrdinalIgnoreCase));

        bool firmwareHasCustomLcd = !string.Equals(state.LcdLine1, "AOI VIRTUAL MCU", StringComparison.Ordinal);

        if (ShowLcdSimulation || circuitHasLcd || firmwareHasCustomLcd)
        {
            if (!ShowLcdSimulation)
                ShowLcdSimulation = true;

            LcdSimulatedRow0 = state.LcdLine1;
            LcdSimulatedRow1 = state.LcdLine2;
            _renderer.Layers.ShowLcdSimulation = true;
            _renderer.Layers.LcdSimulatedRow0 = state.LcdLine1;
            _renderer.Layers.LcdSimulatedRow1 = state.LcdLine2;
        }
    }

    private void UpdateOscilloscopeTelemetry(VirtualFirmwareRuntimeState state)
    {
        OscilloscopeSamples.Clear();
        for (int i = 0; i < state.WaveformSamples.Count; i++)
            OscilloscopeSamples.Add(state.WaveformSamples[i]);

        if (state.WaveformSamples.Count == 0)
        {
            OscilloscopeSummaryText = state.IsBuzzerActive
                ? $"CH1: BUZZER {state.BuzzerFrequencyHz} Hz"
                : $"CH1: TICK #{state.TickCount} (0 Hz, 0% Duty)";
            OscilloscopeWaveformPoints = "0,48 220,48";
            return;
        }

        var points = new StringBuilder(state.WaveformSamples.Count * 10);
        int count = state.WaveformSamples.Count;
        double width = 220.0;
        double topMargin = 6.0;
        double height = 42.0;

        for (int i = 0; i < count; i++)
        {
            WaveformSample sample = state.WaveformSamples[i];
            double x = count > 1 ? (i / (double)(count - 1)) * width : 0.0;
            double normalizedV = Math.Clamp(sample.Voltage / 5.0, 0.0, 1.0);
            double y = (topMargin + height) - (normalizedV * height);
            if (i > 0)
                points.Append(' ');
            points.Append(x.ToString("0.0", CultureInfo.InvariantCulture));
            points.Append(',');
            points.Append(y.ToString("0.0", CultureInfo.InvariantCulture));
        }

        OscilloscopeWaveformPoints = points.ToString();

        WaveformSample latest = state.WaveformSamples[^1];
        string chName = string.IsNullOrWhiteSpace(latest.ChannelName) ? "CH1" : latest.ChannelName;
        OscilloscopeSummaryText = state.IsBuzzerActive
            ? $"{chName}: {latest.Voltage:0.00}V · {latest.PwmDuty}% Duty · {state.BuzzerFrequencyHz} Hz"
            : $"{chName}: {latest.Voltage:0.00}V · {(latest.IsDigitalHigh ? "HIGH" : "LOW")} · {latest.PwmDuty}% Duty";
    }

    private void AppendSerialLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string current = VirtualSerialLog ?? string.Empty;
        string combined = string.IsNullOrEmpty(current)
            ? line + "\n"
            : current.EndsWith('\n')
                ? current + line + "\n"
                : current + "\n" + line + "\n";

        string[] lines = combined.Split('\n');
        if (lines.Length > 90)
            combined = string.Join('\n', lines[^90..]);

        VirtualSerialLog = combined;
    }

    private void NotifyProgrammerComputedProperties()
    {
        OnPropertyChanged(nameof(IsTypeCCableSelected));
        OnPropertyChanged(nameof(IsTypeBCableSelected));
        OnPropertyChanged(nameof(IsMicroUsbCableSelected));
        OnPropertyChanged(nameof(SelectedCableSpec));
        OnPropertyChanged(nameof(SelectedCableBadgeText));
        OnPropertyChanged(nameof(ConnectCableButtonLabel));
        OnPropertyChanged(nameof(FirmwareStatusBadge));
    }
}
