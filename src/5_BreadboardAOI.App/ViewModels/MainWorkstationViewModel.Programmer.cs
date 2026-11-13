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
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BreadboardAOI.App.ViewModels;

public sealed record TargetBoardSlotOption(
    string BoardInstanceId,
    MicrocontrollerBoardType BoardType,
    string DisplayLabel)
{
    public override string ToString() => DisplayLabel;
}

public sealed partial class MainWorkstationViewModel
{
    private readonly List<string> _userSerialInputs = new();
    private readonly HardwareSerialMonitorService _hardwareSerialMonitor = new();
    private bool _hardwareSerialEventsWired;
    private DispatcherTimer? _virtualFirmwareTimer;
    private ProgrammerUploadDialog? _programmerUploadDialog;
    private int _firmwareTick;
    private string _activeSimulatedCode = string.Empty;
    private BoxCodingLanguage _activeSimulatedLanguage = BoxCodingLanguage.ArduinoCpp;

    // ---- USB Cable & Hardware Programmer State -----------------------------

    public IReadOnlyList<UsbCableSpec> AvailableUsbCables { get; } = ProgrammerCatalog.AllCables;

    public ObservableCollection<PhysicalComPortEntry> AvailableProgrammerComPorts { get; } = new();

    public ObservableCollection<TargetBoardSlotOption> AvailableTargetBoardSlots { get; } = new();

    public ObservableCollection<BoxCodeDiagnostic> LastCliCompileDiagnostics { get; } = new();

    public IReadOnlyList<int> AvailableUploadSpeeds { get; } = ProgrammerIdeCatalog.AvailableUploadSpeeds;

    public IReadOnlyList<string> AvailableFlashModes { get; } = ProgrammerIdeCatalog.AvailableFlashModes;

    public IReadOnlyList<string> AvailableFlashSizes { get; } = ProgrammerIdeCatalog.AvailableFlashSizes;

    public IReadOnlyList<string> AvailablePartitionSchemes { get; } = ProgrammerIdeCatalog.AvailablePartitionSchemes;

    public IReadOnlyList<int> AvailableCpuFrequenciesMHz { get; } = ProgrammerIdeCatalog.AvailableCpuFrequenciesMHz;

    public IReadOnlyList<ProgrammerToolOption> AvailableProgrammerTools { get; } = ProgrammerIdeCatalog.AllProgrammerTools;

    public IReadOnlyList<string> AvailableMicroPythonTools { get; } = ProgrammerIdeCatalog.AvailableMicroPythonTools;

    [ObservableProperty]
    private UsbCableKind _selectedUsbCableKind = UsbCableKind.UsbB;

    [ObservableProperty]
    private bool _isProgrammerCableConnected;

    [ObservableProperty]
    private bool _showHardwareProgrammer = true;

    [ObservableProperty]
    private bool _isUsbAdapterEnabled;

    [ObservableProperty]
    private bool _isCableMismatchWarning;

    [ObservableProperty]
    private string _targetProgrammerBoardInstanceId = "MCU_1";

    [ObservableProperty]
    private UsbCableKind _pendingCableDragKind = UsbCableKind.None;

    [ObservableProperty]
    private string _programmerStatusText = "DISCONNECTED — Select/drag a USB cable from the 2.5D Cable Rack to a Dev Board";

    [ObservableProperty]
    private string _connectedBoardSummary = "No board connected";

    [ObservableProperty]
    private string _cableCompatibilityNote = "USB Type-A → B selected (Uno / Mega)";

    [ObservableProperty]
    private bool _isProgrammerUploading;

    [ObservableProperty]
    private double _programmerUploadProgress;

    [ObservableProperty]
    private string _selectedProgrammerComPort = ProgrammerIdeCatalog.VirtualSimPortDisplay;

    [ObservableProperty]
    private string _selectedBoardVariantId = "uno_r3";

    [ObservableProperty]
    private string _customFqbnOverride = string.Empty;

    [ObservableProperty]
    private int _selectedUploadSpeedBaud = 115200;

    [ObservableProperty]
    private string _selectedFlashMode = "qio";

    [ObservableProperty]
    private string _selectedFlashSize = "4MB";

    [ObservableProperty]
    private string _selectedPartitionScheme = "default";

    [ObservableProperty]
    private int _selectedCpuFrequencyMHz = 240;

    [ObservableProperty]
    private bool _usbCdcOnBoot = true;

    [ObservableProperty]
    private string _selectedProgrammerToolId = "default";

    [ObservableProperty]
    private bool _uploadUsingProgrammer;

    [ObservableProperty]
    private bool _verboseUploadOutput;

    [ObservableProperty]
    private bool _allowVirtualFallbackOnPhysicalPort;

    [ObservableProperty]
    private string _selectedMicroPythonTool = "auto";

    [ObservableProperty]
    private bool _isHardwareSerialOpen;

    [ObservableProperty]
    private string _hardwareSerialStatusText = "Hardware Serial Idle";

    [ObservableProperty]
    private string _lastUploadDiagnosticSummary = "Ready to Verify or Upload";

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
    private long _virtualSerialRevision;

    public long ExternalSerialTrafficRevision { get; private set; }

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

    public string ConnectCableButtonLabel => IsProgrammerCableConnected ? "UNPLUG CABLE" : "CONNECT CABLE";

    public bool IsGlobalBoardConnected => IsProgrammerCableConnected;

    public string GlobalConnectionBadgeText => IsProgrammerCableConnected
        ? (IsVirtualFirmwareRunning ? "CONNECTED · LIVE" : "CONNECTED")
        : IsCableMismatchWarning
            ? "PLUG MISMATCH"
            : "DISCONNECTED";

    public string GlobalSendButtonLabel => IsProgrammerCableConnected ? "SEND" : "SEND / UPLOAD";

    public string FirmwareStatusBadge => IsProgrammerUploading
        ? "FLASHING FIRMWARE..."
        : IsVirtualFirmwareRunning
            ? "RUNNING ON BOARD"
            : IsProgrammerCableConnected
                ? "CONNECTED (READY)"
                : IsCableMismatchWarning
                    ? "PLUG MISMATCH"
                    : "NO USB LINK";

    public IReadOnlyList<BoardVariantPreset> AvailableBoardVariants =>
        ProgrammerIdeCatalog.GetVariantsForBoard(GetEffectiveTargetBoard());

    public BoardVariantPreset ActiveBoardVariant =>
        ProgrammerIdeCatalog.FindVariantById(SelectedBoardVariantId) is { } match && match.BoardType == GetEffectiveTargetBoard()
            ? match
            : ProgrammerIdeCatalog.GetDefaultVariant(GetEffectiveTargetBoard());

    public bool SupportsEspFlashOptions => ActiveBoardVariant.SupportsFlashOptions;

    public bool SupportsUsbCdcOption => ActiveBoardVariant.SupportsUsbCdcOnBoot;

    public bool SupportsCpuFreqOption => ActiveBoardVariant.SupportsCpuFreqSelection;

    public string EffectiveFqbnPreview =>
        ProgrammerIdeCatalog.BuildEffectiveFqbn(GetEffectiveTargetBoard(), BuildCurrentArduinoUploadOptions(verifyOnly: false));

    // ---- Observable Property Change Hooks ----------------------------------

    partial void OnSelectedUsbCableKindChanged(UsbCableKind value)
    {
        ReevaluateCableCompatibility(allowDisconnectOnMismatch: IsProgrammerCableConnected);
    }

    partial void OnIsUsbAdapterEnabledChanged(bool value)
    {
        ReevaluateCableCompatibility(allowDisconnectOnMismatch: IsProgrammerCableConnected);
        StatusLine = value
            ? $"USB Adapter ENABLED — {CableCompatibilityNote}"
            : $"USB Adapter DISABLED — {CableCompatibilityNote}";
    }

    partial void OnTargetProgrammerBoardInstanceIdChanged(string value)
    {
        SyncVariantToBoard(GetEffectiveTargetBoard());
        ReevaluateCableCompatibility(allowDisconnectOnMismatch: IsProgrammerCableConnected);
        OnPropertyChanged(nameof(AvailableBoardVariants));
        OnPropertyChanged(nameof(ActiveBoardVariant));
        OnPropertyChanged(nameof(SupportsEspFlashOptions));
        OnPropertyChanged(nameof(SupportsUsbCdcOption));
        OnPropertyChanged(nameof(SupportsCpuFreqOption));
        OnPropertyChanged(nameof(EffectiveFqbnPreview));
    }

    partial void OnPendingCableDragKindChanged(UsbCableKind value)
    {
        _renderer.PendingCableDragKind = value;
    }

    partial void OnSelectedBoardVariantIdChanged(string value)
    {
        BoardVariantPreset active = ActiveBoardVariant;
        if (active.DefaultUploadBaud > 0)
            SelectedUploadSpeedBaud = active.DefaultUploadBaud;
        OnPropertyChanged(nameof(ActiveBoardVariant));
        OnPropertyChanged(nameof(SupportsEspFlashOptions));
        OnPropertyChanged(nameof(SupportsUsbCdcOption));
        OnPropertyChanged(nameof(SupportsCpuFreqOption));
        OnPropertyChanged(nameof(EffectiveFqbnPreview));
        SyncProgrammerToRenderer();
    }

    partial void OnCustomFqbnOverrideChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveFqbnPreview));
        SyncProgrammerToRenderer();
    }

    partial void OnSelectedUploadSpeedBaudChanged(int value)
    {
        OnPropertyChanged(nameof(EffectiveFqbnPreview));
        SyncProgrammerToRenderer();
    }

    partial void OnSelectedFlashModeChanged(string value) => OnPropertyChanged(nameof(EffectiveFqbnPreview));
    partial void OnSelectedFlashSizeChanged(string value) => OnPropertyChanged(nameof(EffectiveFqbnPreview));
    partial void OnSelectedPartitionSchemeChanged(string value) => OnPropertyChanged(nameof(EffectiveFqbnPreview));
    partial void OnSelectedCpuFrequencyMHzChanged(int value) => OnPropertyChanged(nameof(EffectiveFqbnPreview));
    partial void OnUsbCdcOnBootChanged(bool value) => OnPropertyChanged(nameof(EffectiveFqbnPreview));
    partial void OnSelectedProgrammerToolIdChanged(string value) => SyncProgrammerToRenderer();

    partial void OnSelectedProgrammerComPortChanged(string value)
    {
        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();
        if (IsProgrammerCableConnected)
        {
            ConnectedBoardSummary = ProgrammerCatalog.GetBoardPortDescription(effectiveBoard, SelectedUsbCableKind, value);
        }
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
            ? $"CONNECTED · {_renderer.McuRuleEngine.GetBoardProfile(effectiveBoard).DisplayName} via {spec.DisplayName} (READY)"
            : IsCableMismatchWarning
                ? $"PLUG MISMATCH · {CableCompatibilityNote}"
                : "DISCONNECTED — Drag or select a cable from 2.5D Cable Rack";

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
        OnPropertyChanged(nameof(GlobalConnectionBadgeText));
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

    public void InitializeProgrammerSubsystem()
    {
        EnsureHardwareSerialWired();
        RefreshProgrammerComPorts();
        RefreshTargetBoardSlots();
        SyncVariantToBoard(GetEffectiveTargetBoard());
        ReevaluateCableCompatibility(allowDisconnectOnMismatch: false);
    }

    public void DisposeProgrammerResources()
    {
        if (_hardwareSerialEventsWired)
        {
            _hardwareSerialMonitor.LineReceived -= OnHardwareSerialLineReceived;
            _hardwareSerialMonitor.MonitorStatusChanged -= OnHardwareSerialStatusChanged;
            _hardwareSerialEventsWired = false;
        }
        _hardwareSerialMonitor.Dispose();
    }

    private void EnsureHardwareSerialWired()
    {
        if (_hardwareSerialEventsWired)
            return;

        _hardwareSerialMonitor.LineReceived += OnHardwareSerialLineReceived;
        _hardwareSerialMonitor.MonitorStatusChanged += OnHardwareSerialStatusChanged;
        _hardwareSerialEventsWired = true;
    }

    private void OnHardwareSerialLineReceived(string line)
    {
        void Apply()
        {
            ExternalSerialTrafficRevision++;
            AppendSerialLog($"[HW RX] {line}");
        }
        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    private void OnHardwareSerialStatusChanged(string status)
    {
        void Apply()
        {
            IsHardwareSerialOpen = _hardwareSerialMonitor.IsOpen;
            HardwareSerialStatusText = status;
        }
        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke(Apply);
    }

    public void RefreshTargetBoardSlots()
    {
        MicrocontrollerBoardType primary = SelectedDevBoardType;
        MicrocontrollerBoardType secondary = SecondaryDevBoardType;

        AvailableTargetBoardSlots.Clear();
        if (IsDevBoardDocked && primary != MicrocontrollerBoardType.None)
        {
            string pName = _renderer.McuRuleEngine.GetBoardProfile(primary).DisplayName;
            AvailableTargetBoardSlots.Add(new TargetBoardSlotOption("MCU_1", primary, $"MCU_1 · {pName}"));
        }
        if (IsDevBoardDocked && secondary != MicrocontrollerBoardType.None)
        {
            string sName = _renderer.McuRuleEngine.GetBoardProfile(secondary).DisplayName;
            AvailableTargetBoardSlots.Add(new TargetBoardSlotOption("MCU_2", secondary, $"MCU_2 · {sName}"));
        }

        if (AvailableTargetBoardSlots.Count == 0)
        {
            string fallbackName = _renderer.McuRuleEngine.GetBoardProfile(MicrocontrollerBoardType.ArduinoUnoR3R4).DisplayName;
            AvailableTargetBoardSlots.Add(new TargetBoardSlotOption("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, $"MCU_1 · {fallbackName}"));
        }

        if (!AvailableTargetBoardSlots.Any(s => string.Equals(s.BoardInstanceId, TargetProgrammerBoardInstanceId, StringComparison.OrdinalIgnoreCase)))
        {
            TargetProgrammerBoardInstanceId = AvailableTargetBoardSlots[0].BoardInstanceId;
        }
    }

    public void OnDevBoardTypeChangedForProgrammer(MicrocontrollerBoardType boardType)
    {
        RefreshTargetBoardSlots();
        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();
        SyncVariantToBoard(effectiveBoard);

        // Preserve user's explicit cable choice while disconnected unless none is chosen;
        // if board swaps to another type while none/default, recommend its native plug.
        if (SelectedUsbCableKind == UsbCableKind.None)
        {
            UsbCableKind recommended = ProgrammerCatalog.GetRecommendedCable(effectiveBoard);
            if (recommended != UsbCableKind.None)
            {
                SelectedUsbCableKind = recommended;
                return;
            }
        }

        ReevaluateCableCompatibility(allowDisconnectOnMismatch: IsProgrammerCableConnected);
        OnPropertyChanged(nameof(AvailableBoardVariants));
        OnPropertyChanged(nameof(ActiveBoardVariant));
        OnPropertyChanged(nameof(SupportsEspFlashOptions));
        OnPropertyChanged(nameof(SupportsUsbCdcOption));
        OnPropertyChanged(nameof(SupportsCpuFreqOption));
        OnPropertyChanged(nameof(EffectiveFqbnPreview));
    }

    private void SyncVariantToBoard(MicrocontrollerBoardType board)
    {
        BoardVariantPreset def = ProgrammerIdeCatalog.GetDefaultVariant(board);
        BoardVariantPreset? cur = ProgrammerIdeCatalog.FindVariantById(SelectedBoardVariantId);
        if (cur is null || cur.BoardType != board)
        {
            SelectedBoardVariantId = def.Id;
            if (def.DefaultUploadBaud > 0)
                SelectedUploadSpeedBaud = def.DefaultUploadBaud;
        }
    }

    private void ReevaluateCableCompatibility(bool allowDisconnectOnMismatch)
    {
        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();
        bool compatible = ProgrammerCatalog.IsCableCompatible(
            SelectedUsbCableKind,
            effectiveBoard,
            IsUsbAdapterEnabled,
            out _,
            out string note);

        IsCableMismatchWarning = !compatible && SelectedUsbCableKind != UsbCableKind.None;
        CableCompatibilityNote = note;

        if (!compatible && allowDisconnectOnMismatch && IsProgrammerCableConnected)
        {
            IsProgrammerCableConnected = false;
            ProgrammerStatusText = $"PLUG MISMATCH (UNPLUGGED) · {note}";
            StatusLine = ProgrammerStatusText;
            return;
        }

        UsbCableSpec spec = ProgrammerCatalog.GetCableSpec(SelectedUsbCableKind);
        ConnectedBoardSummary = IsProgrammerCableConnected
            ? ProgrammerCatalog.GetBoardPortDescription(effectiveBoard, SelectedUsbCableKind, SelectedProgrammerComPort)
            : "No board connected";
        ProgrammerStatusText = IsProgrammerCableConnected
            ? $"CONNECTED · {_renderer.McuRuleEngine.GetBoardProfile(effectiveBoard).DisplayName} via {spec.DisplayName} (READY)"
            : IsCableMismatchWarning
                ? $"PLUG MISMATCH · {note}"
                : $"{spec.DisplayName} selected — drag to Dev Board USB port or click CONNECT CABLE";

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
        _renderer.TargetProgrammerBoardInstanceId = TargetProgrammerBoardInstanceId;
        _renderer.IsCableAdapterEnabled = IsUsbAdapterEnabled;
        _renderer.IsCableMismatchWarning = IsCableMismatchWarning;
        _renderer.PendingCableDragKind = PendingCableDragKind;
        string shortPort = ProgrammerIdeCatalog.ExtractRawComPort(SelectedProgrammerComPort) ?? "SIM";
        string baseFqbn = ActiveBoardVariant.BaseFqbn.Split(':').LastOrDefault() ?? "mcu";
        _renderer.ActiveBoardPortSummaryText = $"{baseFqbn} · {SelectedUploadSpeedBaud} · {shortPort}";
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
    public void ToggleUsbAdapterMode()
    {
        IsUsbAdapterEnabled = !IsUsbAdapterEnabled;
    }

    [RelayCommand]
    public void ConnectUsbCable()
    {
        if (SelectedDevBoardType == MicrocontrollerBoardType.None)
            SelectedDevBoardType = MicrocontrollerBoardType.Esp32DevKitV1;

        IsDevBoardDocked = true;
        ShowHardwareProgrammer = true;
        RefreshTargetBoardSlots();

        MicrocontrollerBoardType effectiveBoard = GetEffectiveTargetBoard();

        if (SelectedUsbCableKind == UsbCableKind.None)
            SelectedUsbCableKind = ProgrammerCatalog.GetRecommendedCable(effectiveBoard);

        bool compatible = ProgrammerCatalog.IsCableCompatible(
            SelectedUsbCableKind,
            effectiveBoard,
            IsUsbAdapterEnabled,
            out _,
            out string note);

        CableCompatibilityNote = note;
        IsCableMismatchWarning = !compatible;

        if (!compatible)
        {
            IsProgrammerCableConnected = false;
            ConnectedBoardSummary = "No board connected (Plug mismatch)";
            ProgrammerStatusText = $"PLUG MISMATCH · {note}";
            StatusLine = ProgrammerStatusText;
            BuilderHint = ProgrammerStatusText;
            NotifyProgrammerComputedProperties();
            SyncProgrammerToRenderer();
            return;
        }

        ConnectedBoardSummary = ProgrammerCatalog.GetBoardPortDescription(
            effectiveBoard,
            SelectedUsbCableKind,
            SelectedProgrammerComPort);

        IsProgrammerCableConnected = true;
        string boardDisplay = _renderer.McuRuleEngine.GetBoardProfile(effectiveBoard).DisplayName;
        ProgrammerStatusText =
            $"CONNECTED · {boardDisplay} via {ProgrammerCatalog.GetCableSpec(SelectedUsbCableKind).DisplayName} (READY FOR UPLOAD)";
        StatusLine = ProgrammerStatusText;
        BuilderHint = $"USB Connected ({TargetProgrammerBoardInstanceId}): {boardDisplay} ready for SEND / UPLOAD.";
        SyncProgrammerToRenderer();
    }

    [RelayCommand]
    public void DisconnectUsbCable()
    {
        PendingCableDragKind = UsbCableKind.None;
        _virtualFirmwareTimer?.Stop();
        IsVirtualFirmwareRunning = false;
        _renderer.ActiveFirmwareState = null;
        IsProgrammerCableConnected = false;
        ConnectedBoardSummary = "No board connected";
        ProgrammerStatusText = "DISCONNECTED — Select/drag a cable from 2.5D Cable Rack";
        StatusLine = "USB Programmer cable disconnected.";
        BuilderHint = "USB cable unplugged. Drag from 2.5D Cable Rack or Programmer USB Jack to reconnect.";
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

    public void StartCablePlacementFromRack(UsbCableKind cableKind, float canvasX, float canvasY)
    {
        if (cableKind == UsbCableKind.None)
            return;

        SelectedUsbCableKind = cableKind;
        ShowHardwareProgrammer = true;
        PendingCableDragKind = cableKind;
        _renderer.CableDragCursorCanvas = new SkiaSharp.SKPoint(canvasX, canvasY);
        BuilderHint = $"Dragging {ProgrammerCatalog.GetCableSpec(cableKind).DisplayName} — release/click on a Dev Board USB port to plug in.";
    }

    public void UpdateCablePlacementCursor(float canvasX, float canvasY)
    {
        if (PendingCableDragKind == UsbCableKind.None)
            return;

        _renderer.CableDragCursorCanvas = new SkiaSharp.SKPoint(canvasX, canvasY);
    }

    public void CancelCablePlacement()
    {
        if (PendingCableDragKind == UsbCableKind.None)
            return;

        PendingCableDragKind = UsbCableKind.None;
        BuilderHint = "USB cable placement cancelled.";
    }

    public void CompleteCablePlacementOnBoard(string boardInstanceId, UsbCableKind? dragKind = null)
    {
        if (!string.IsNullOrWhiteSpace(boardInstanceId))
            TargetProgrammerBoardInstanceId = boardInstanceId;

        if (dragKind.HasValue && dragKind.Value != UsbCableKind.None)
            SelectedUsbCableKind = dragKind.Value;

        PendingCableDragKind = UsbCableKind.None;
        ConnectUsbCable();
    }

    /// <summary>
    /// Handles interactive clicks on the 2.5D Hardware Programmer, 2.5D Cable Rack,
    /// Dev Board USB receptacles, and connected USB cables across ALL workstation modes.
    /// </summary>
    public bool TryHandleProgrammerViewportAction(double viewportX, double viewportY, bool secondary)
    {
        float x = (float)viewportX;
        float y = (float)viewportY;

        if (secondary)
        {
            if (PendingCableDragKind != UsbCableKind.None)
            {
                CancelCablePlacement();
                return true;
            }

            if (_renderer.TryHitTestConnectedCableOrPlug(x, y))
            {
                DisconnectUsbCable();
                return true;
            }

            return false;
        }

        // Complete pending click-to-plug cable placement if user clicked a Dev Board USB port
        if (PendingCableDragKind != UsbCableKind.None)
        {
            if (_renderer.TryHitTestBoardUsbReceptacle(x, y, out string? targetBoardId, out _) && !string.IsNullOrWhiteSpace(targetBoardId))
            {
                CompleteCablePlacementOnBoard(targetBoardId, PendingCableDragKind);
                return true;
            }

            if (_renderer.TryHitTestCableRackCard(x, y, out UsbCableKind switchedKind))
            {
                StartCablePlacementFromRack(switchedKind, x, y);
                return true;
            }

            CancelCablePlacement();
            return true;
        }

        if (_renderer.TryHitTestProgrammerAdapterToggle(x, y))
        {
            ToggleUsbAdapterMode();
            return true;
        }

        if (_renderer.TryHitTestProgrammerUploadButton(x, y) || _renderer.TryHitTestProgrammerSendButton(x, y))
        {
            OpenProgrammerUploadDialog();
            return true;
        }

        if (_renderer.TryHitTestCableRackCard(x, y, out UsbCableKind rackCardKind))
        {
            StartCablePlacementFromRack(rackCardKind, x, y);
            return true;
        }

        if (_renderer.TryHitTestProgrammerJack(x, y))
        {
            UsbCableKind kindToPlace = SelectedUsbCableKind == UsbCableKind.None
                ? ProgrammerCatalog.GetRecommendedCable(GetEffectiveTargetBoard())
                : SelectedUsbCableKind;
            StartCablePlacementFromRack(kindToPlace, x, y);
            return true;
        }

        if (_renderer.TryHitTestBoardUsbReceptacle(x, y, out string? boardId, out UsbCableKind nativeUsbKind) && !string.IsNullOrWhiteSpace(boardId))
        {
            if (!IsProgrammerCableConnected && SelectedUsbCableKind == UsbCableKind.None && nativeUsbKind != UsbCableKind.None)
                SelectedUsbCableKind = nativeUsbKind;

            CompleteCablePlacementOnBoard(boardId, SelectedUsbCableKind);
            return true;
        }

        return false;
    }

    public bool ShouldInterceptSecondaryViewportAction(double viewportX, double viewportY)
        => PendingCableDragKind != UsbCableKind.None
           || _renderer.TryHitTestConnectedCableOrPlug((float)viewportX, (float)viewportY);

    [RelayCommand]
    public void RefreshProgrammerComPorts()
    {
        IReadOnlyList<PhysicalComPortEntry> discovered = HardwareSerialMonitorService.DiscoverAvailablePorts();
        string current = SelectedProgrammerComPort;

        AvailableProgrammerComPorts.Clear();
        foreach (PhysicalComPortEntry entry in discovered)
            AvailableProgrammerComPorts.Add(entry);

        if (string.IsNullOrWhiteSpace(current))
        {
            SelectedProgrammerComPort = ProgrammerIdeCatalog.VirtualSimPortDisplay;
        }
        else if (!ProgrammerIdeCatalog.IsVirtualPort(current))
        {
            string? raw = ProgrammerIdeCatalog.ExtractRawComPort(current);
            PhysicalComPortEntry? match = discovered.FirstOrDefault(p =>
                string.Equals(p.PortName, raw, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                SelectedProgrammerComPort = match.DisplayLabel;
        }

        int hardwareCount = discovered.Count(p => !p.IsVirtualSimulator);
        StatusLine = hardwareCount > 0
            ? $"COM Refresh: Found {hardwareCount} hardware USB-UART port(s)."
            : "COM Refresh: No physical COM port attached (Virtual Simulator available).";
    }

    [RelayCommand]
    public void OpenHardwareSerialMonitor()
    {
        EnsureHardwareSerialWired();
        if (ProgrammerIdeCatalog.IsVirtualPort(SelectedProgrammerComPort))
        {
            HardwareSerialStatusText = "Select a physical COMx port (not VIRTUAL-SIM) to open hardware serial.";
            AppendSerialLog($"[HW SERIAL] {HardwareSerialStatusText}");
            return;
        }

        bool opened = _hardwareSerialMonitor.TryOpen(SelectedProgrammerComPort, SelectedUploadSpeedBaud, out string msg);
        IsHardwareSerialOpen = opened;
        HardwareSerialStatusText = msg;
        AppendSerialLog($"[HW SERIAL] {msg}");
        StatusLine = msg;
    }

    [RelayCommand]
    public void CloseHardwareSerialMonitor()
    {
        _hardwareSerialMonitor.Close();
        IsHardwareSerialOpen = false;
        HardwareSerialStatusText = "Hardware Serial Monitor closed.";
        AppendSerialLog("[HW SERIAL] Port closed.");
    }

    [RelayCommand]
    public void PulseDtrReset()
    {
        if (_hardwareSerialMonitor.PulseDtrReset(out string msg))
            AppendSerialLog($"[HW RESET] {msg}");
        else
            AppendSerialLog($"[HW RESET] {msg}");
        StatusLine = msg;
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
            if (overrideLanguage.HasValue)
                ActiveFirmwareLanguage = overrideLanguage.Value;

            string codeToEdit = ResolveUploadFirmwareCode(overrideCode, ActiveFirmwareLanguage);
            ActiveFirmwareCode = codeToEdit;
            RefreshProgrammerComPorts();
            RefreshTargetBoardSlots();

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
    public Task<ProgrammerUploadResult> UploadFirmwareToBoardAsync()
    {
        string code = ResolveUploadFirmwareCode(ActiveFirmwareCode, ActiveFirmwareLanguage);
        ActiveFirmwareCode = code;
        return UploadFirmwareWithOptionsAsync(code, ActiveFirmwareLanguage, SelectedProgrammerComPort, verifyOnly: false);
    }

    [RelayCommand]
    public Task<ProgrammerUploadResult> VerifyCompileOnlyAsync()
    {
        string code = ResolveUploadFirmwareCode(ActiveFirmwareCode, ActiveFirmwareLanguage);
        ActiveFirmwareCode = code;
        return UploadFirmwareWithOptionsAsync(code, ActiveFirmwareLanguage, SelectedProgrammerComPort, verifyOnly: true);
    }

    public ArduinoUploadOptions BuildCurrentArduinoUploadOptions(bool verifyOnly) => new(
        BoardType: GetEffectiveTargetBoard(),
        BoardVariantId: SelectedBoardVariantId,
        CustomFqbnOverride: CustomFqbnOverride,
        UploadSpeedBaud: SelectedUploadSpeedBaud,
        FlashMode: SelectedFlashMode,
        FlashSize: SelectedFlashSize,
        PartitionScheme: SelectedPartitionScheme,
        CpuFrequencyMHz: SelectedCpuFrequencyMHz,
        UsbCdcOnBoot: UsbCdcOnBoot,
        ProgrammerToolId: SelectedProgrammerToolId,
        VerifyOnly: verifyOnly,
        UploadUsingProgrammer: UploadUsingProgrammer,
        VerboseOutput: VerboseUploadOutput,
        AllowVirtualFallbackOnPhysicalPort: AllowVirtualFallbackOnPhysicalPort,
        MicroPythonTool: SelectedMicroPythonTool,
        TargetBoardInstanceId: TargetProgrammerBoardInstanceId);

    /// <summary>
    /// Validates firmware with <see cref="BoxCodeErrorAnalyzer"/>, performs real Arduino CLI or MicroPython
    /// upload when targeting a physical COM port (reporting failures honestly unless virtual fallback is opted in),
    /// or runs the live virtual breadboard simulator when VIRTUAL-SIM is targeted.
    /// </summary>
    public Task<ProgrammerUploadResult> UploadFirmwareCoreAsync(
        string code,
        BoxCodingLanguage language,
        string targetPort) =>
        UploadFirmwareWithOptionsAsync(code, language, targetPort, verifyOnly: false);

    public async Task<ProgrammerUploadResult> UploadFirmwareWithOptionsAsync(
        string code,
        BoxCodingLanguage language,
        string targetPort,
        bool verifyOnly)
    {
        MicrocontrollerBoardType boardType = GetEffectiveTargetBoard();
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(boardType);

        string effectivePort = string.IsNullOrWhiteSpace(targetPort)
            ? (string.IsNullOrWhiteSpace(SelectedProgrammerComPort)
                ? ProgrammerIdeCatalog.VirtualSimPortDisplay
                : SelectedProgrammerComPort)
            : targetPort.Trim();

        ActiveFirmwareCode = code;
        ActiveFirmwareLanguage = language;
        SelectedProgrammerComPort = effectivePort;
        LastCliCompileDiagnostics.Clear();

        if (!verifyOnly && !IsProgrammerCableConnected)
        {
            const string noCableMsg =
                "[UPLOAD BLOCKED] No USB cable is connected between the Programmer and Dev Board.\n" +
                "Drag or click a USB cable from the 2.5D Cable Rack onto the Dev Board USB receptacle (or click CONNECT CABLE).";
            ProgrammerStatusText = "DISCONNECTED — Connect USB cable manually before uploading";
            LastUploadDiagnosticSummary = "Blocked: No manual USB cable connection";
            StatusLine = ProgrammerStatusText;
            return new ProgrammerUploadResult(
                Success: false,
                BoardDisplayName: profile.DisplayName,
                CableKind: SelectedUsbCableKind,
                TargetPort: effectivePort,
                UsedHardwareCli: false,
                LogOutput: noCableMsg,
                CodeAnalysis: null,
                InitialRuntimeState: null);
        }

        if (!verifyOnly && !ProgrammerCatalog.IsCableCompatible(SelectedUsbCableKind, boardType, IsUsbAdapterEnabled, out _, out string compatNote))
        {
            string mismatchMsg = $"[PLUG MISMATCH] {compatNote}\nSelect the matching USB cable or enable USB Adapter Mode before uploading.";
            IsCableMismatchWarning = true;
            ProgrammerStatusText = $"PLUG MISMATCH · {compatNote}";
            LastUploadDiagnosticSummary = compatNote;
            StatusLine = ProgrammerStatusText;
            return new ProgrammerUploadResult(
                Success: false,
                BoardDisplayName: profile.DisplayName,
                CableKind: SelectedUsbCableKind,
                TargetPort: effectivePort,
                UsedHardwareCli: false,
                LogOutput: mismatchMsg,
                CodeAnalysis: null,
                InitialRuntimeState: null);
        }

        IsProgrammerUploading = true;
        ProgrammerUploadProgress = 0;

        ArduinoUploadOptions ideOptions = BuildCurrentArduinoUploadOptions(verifyOnly);
        string fqbn = ProgrammerIdeCatalog.BuildEffectiveFqbn(boardType, ideOptions);

        var log = new StringBuilder();
        log.AppendLine(verifyOnly
            ? $"[1/3] Static & Semantic Verification for {profile.DisplayName} ({language}) · FQBN: {fqbn}..."
            : $"[1/4] Verifying syntax & pin rules for {profile.DisplayName} ({language}) · FQBN: {fqbn}...");

        IReadOnlyCollection<string> wiredPins = ExtractWiredMcuPins();
        BoxCodeAnalysisReport analysis = BoxCodeErrorAnalyzer.Analyze(code, language, boardType, wiredPins);
        if (analysis.ErrorCount > 0)
        {
            foreach (BoxCodeDiagnostic diag in analysis.Diagnostics)
            {
                log.AppendLine($"  [{diag.SeverityLabel}] {diag.DisplaySummary}");
                LastCliCompileDiagnostics.Add(diag);
            }

            BoxCodeDiagnostic first = analysis.Diagnostics[0];
            IsProgrammerUploading = false;
            ProgrammerUploadProgress = 0;
            ProgrammerStatusText = $"VERIFY FAILED: {first.Code} — {first.Message}";
            LastUploadDiagnosticSummary = ProgrammerStatusText;
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
        ProgrammerStatusText = $"[2/4] Compiling ({fqbn}) for {profile.DisplayName}...";
        log.AppendLine($"[2/4] Static check passed ({analysis.WarningCount} warning(s))...");
        await Task.Delay(90).ConfigureAwait(true);
        if (_disposed)
        {
            IsProgrammerUploading = false;
            return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
        }

        bool isPhysicalComPort = !ProgrammerIdeCatalog.IsVirtualPort(effectivePort);
        string resultTargetPort = isPhysicalComPort
            ? (ProgrammerIdeCatalog.ExtractRawComPort(effectivePort) ?? effectivePort)
            : effectivePort;
        bool usedHardwareCli = false;

        if (verifyOnly)
        {
            ProgrammerUploadProgress = 70;
            ProgrammerStatusText = language == BoxCodingLanguage.ArduinoCpp
                ? $"[3/3] Compiling verification build ({fqbn})..."
                : "[3/3] Running Python AST & syntax verification...";
            log.AppendLine(ProgrammerStatusText);

            if (language == BoxCodingLanguage.ArduinoCpp)
            {
                var cliRes = await TryCompileWithArduinoCliAsync(code, fqbn, ideOptions.VerboseOutput).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(cliRes.OutputLog))
                    log.AppendLine(cliRes.OutputLog);
                foreach (var d in cliRes.Diagnostics)
                    LastCliCompileDiagnostics.Add(d);

                ProgrammerUploadProgress = 100;
                IsProgrammerUploading = false;
                bool verifyOk = cliRes.ToolMissing ? true : cliRes.Succeeded;
                LastUploadDiagnosticSummary = cliRes.ToolMissing
                    ? $"Static Verify OK ({profile.DisplayName})"
                    : cliRes.Succeeded
                        ? $"Verify OK ({profile.DisplayName})"
                        : (cliRes.Diagnostics.FirstOrDefault()?.DisplaySummary ?? $"CLI compile failed ({fqbn})");
                ProgrammerStatusText = cliRes.ToolMissing
                    ? $"STATIC VERIFY OK · {analysis.WarningCount} warning(s) (arduino-cli not installed)"
                    : cliRes.Succeeded
                        ? $"CLI COMPILE VERIFIED · {fqbn}"
                        : $"CLI COMPILE FAILED · {Math.Max(1, cliRes.Diagnostics.Count)} error(s)";
                StatusLine = ProgrammerStatusText;

                return new ProgrammerUploadResult(verifyOk, profile.DisplayName, SelectedUsbCableKind, resultTargetPort, cliRes.Succeeded, log.ToString(), analysis, null);
            }
            else
            {
                var pyRes = await TryCheckPythonSyntaxWithCliAsync(code).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(pyRes.OutputLog))
                    log.AppendLine(pyRes.OutputLog);
                foreach (var d in pyRes.Diagnostics)
                    LastCliCompileDiagnostics.Add(d);

                ProgrammerUploadProgress = 100;
                IsProgrammerUploading = false;
                bool verifyOk = pyRes.ToolMissing ? true : pyRes.Succeeded;
                ProgrammerStatusText = pyRes.ToolMissing
                    ? $"STATIC PY VERIFY OK · {analysis.WarningCount} warning(s)"
                    : pyRes.Succeeded
                        ? "PYTHON AST SYNTAX VERIFIED"
                        : "PYTHON AST SYNTAX ERROR";
                LastUploadDiagnosticSummary = ProgrammerStatusText;
                StatusLine = ProgrammerStatusText;
                return new ProgrammerUploadResult(verifyOk, profile.DisplayName, SelectedUsbCableKind, resultTargetPort, pyRes.Succeeded, log.ToString(), analysis, null);
            }
        }

        if (isPhysicalComPort)
        {
            string? rawPort = ProgrammerIdeCatalog.ExtractRawComPort(effectivePort);
            if (string.IsNullOrWhiteSpace(rawPort))
            {
                IsProgrammerUploading = false;
                ProgrammerUploadProgress = 0;
                log.AppendLine($"[ERROR] Invalid COM port identifier '{effectivePort}'.");
                ProgrammerStatusText = $"UPLOAD FAILED: Invalid port '{effectivePort}'";
                return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
            }

            // Temporarily close live serial monitor on the target COM port so the uploader can claim the UART!
            bool reopenSerialAfterFlash = _hardwareSerialMonitor.IsOpen &&
                string.Equals(_hardwareSerialMonitor.ActivePortName, rawPort, StringComparison.OrdinalIgnoreCase);
            if (reopenSerialAfterFlash)
                CloseHardwareSerialMonitor();

            ProgrammerUploadProgress = 75;
            ProgrammerStatusText = $"[3/4] Flashing over {SelectedCableSpec.DisplayName} -> {rawPort} @ {ideOptions.UploadSpeedBaud} baud...";
            log.AppendLine($"[3/4] Target device: {ProgrammerCatalog.GetBoardPortDescription(boardType, SelectedUsbCableKind, rawPort)} (raw port: {rawPort})...");

            (bool executedAndSucceeded, string cliOutput, IReadOnlyList<BoxCodeDiagnostic> cliDiags) =
                language == BoxCodingLanguage.ArduinoCpp
                    ? await TryFlashWithArduinoCliAsync(code, fqbn, rawPort, ideOptions).ConfigureAwait(true)
                    : await TryFlashMicroPythonCliAsync(code, rawPort, ideOptions).ConfigureAwait(true);

            if (_disposed)
            {
                IsProgrammerUploading = false;
                return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
            }

            foreach (BoxCodeDiagnostic cd in cliDiags)
                LastCliCompileDiagnostics.Add(cd);

            if (!string.IsNullOrWhiteSpace(cliOutput))
                log.AppendLine(cliOutput);

            usedHardwareCli = executedAndSucceeded;

            if (!executedAndSucceeded)
            {
                if (!AllowVirtualFallbackOnPhysicalPort)
                {
                    log.AppendLine($"[PHYSICAL UPLOAD FAILED] Transfer to {rawPort} halted (enable Allow Virtual Fallback only for offline simulation).");
                    ProgrammerUploadProgress = 0;
                    IsProgrammerUploading = false;
                    ProgrammerStatusText = $"PHYSICAL UPLOAD FAILED on {rawPort} — see diagnostics";
                    LastUploadDiagnosticSummary = $"Physical flash failed on {rawPort} (virtual fallback disabled)";
                    StatusLine = ProgrammerStatusText;
                    AppendSerialLog($"[UPLOAD FAILED] Physical transfer to {rawPort} failed. Enable 'Allow Virtual Simulator Fallback' for offline simulation.");
                    return new ProgrammerUploadResult(
                        Success: false,
                        BoardDisplayName: profile.DisplayName,
                        CableKind: SelectedUsbCableKind,
                        TargetPort: resultTargetPort,
                        UsedHardwareCli: false,
                        LogOutput: log.ToString(),
                        CodeAnalysis: analysis,
                        InitialRuntimeState: null);
                }

                log.AppendLine($"[FALLBACK] Physical upload to {rawPort} did not complete — starting Virtual Breadboard execution (Opt-In fallback enabled).");
            }
            else if (reopenSerialAfterFlash)
            {
                OpenHardwareSerialMonitor();
            }
        }
        else
        {
            ProgrammerUploadProgress = 75;
            ProgrammerStatusText = $"[3/4] Connecting over {SelectedCableSpec.DisplayName} ({effectivePort})...";
            log.AppendLine($"[3/4] Connecting over {ProgrammerCatalog.GetBoardPortDescription(boardType, SelectedUsbCableKind, effectivePort)}...");
            await Task.Delay(120).ConfigureAwait(true);
            if (_disposed)
            {
                IsProgrammerUploading = false;
                return new ProgrammerUploadResult(false, profile.DisplayName, SelectedUsbCableKind, effectivePort, false, log.ToString(), analysis, null);
            }
        }

        ProgrammerUploadProgress = 100;
        IsProgrammerUploading = false;

        VirtualFirmwareRuntimeState initialState = StartVirtualFirmwareSimulation();

        log.AppendLine(usedHardwareCli
            ? $"[4/4] Hardware flashing complete on {effectivePort} & live telemetry running on {profile.DisplayName}!"
            : $"[4/4] Live virtual execution running on {profile.DisplayName}!");
        ProgrammerStatusText = usedHardwareCli
            ? $"HARDWARE FLASHED · {profile.DisplayName} on {effectivePort}"
            : $"UPLOAD COMPLETE · Virtual execution on {profile.DisplayName}";
        LastUploadDiagnosticSummary = ProgrammerStatusText;
        StatusLine = ProgrammerStatusText;

        return new ProgrammerUploadResult(
            Success: true,
            BoardDisplayName: profile.DisplayName,
            CableKind: SelectedUsbCableKind,
            TargetPort: resultTargetPort,
            UsedHardwareCli: usedHardwareCli,
            LogOutput: log.ToString(),
            CodeAnalysis: analysis,
            InitialRuntimeState: initialState);
    }

    private static async Task<(bool Succeeded, bool ToolMissing, string OutputLog, IReadOnlyList<BoxCodeDiagnostic> Diagnostics)>
        TryCompileWithArduinoCliAsync(string code, string fqbn, bool verboseOutput)
    {
        string? arduinoCliPath = FindToolchainExecutable("arduino-cli.exe", "arduino-cli");
        if (string.IsNullOrWhiteSpace(arduinoCliPath))
        {
            return (false, true, "[INFO] arduino-cli not found in PATH or standard install folders — static & semantic analyzer verification used.", Array.Empty<BoxCodeDiagnostic>());
        }

        string sketchDir = Path.Combine(Path.GetTempPath(), "BreadboardAOI_Verify_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(sketchDir);
            string sketchName = Path.GetFileName(sketchDir);
            await File.WriteAllTextAsync(Path.Combine(sketchDir, sketchName + ".ino"), code ?? string.Empty).ConfigureAwait(false);
            string[] args = ProgrammerIdeCatalog.BuildCompileArguments(fqbn, sketchDir, verboseOutput);
            (bool ok, string output) = await RunCliProcessAsync(arduinoCliPath, args, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            var diags = ProgrammerIdeCatalog.ParseArduinoCliDiagnostics(output);
            return (ok, false, output, diags);
        }
        finally
        {
            CleanupTempDir(sketchDir);
        }
    }

    private static async Task<(bool ExecutedAndSucceeded, string OutputLog, IReadOnlyList<BoxCodeDiagnostic> Diagnostics)>
        TryFlashWithArduinoCliAsync(
            string code,
            string fqbn,
            string rawPort,
            ArduinoUploadOptions options)
    {
        string? arduinoCliPath = FindToolchainExecutable("arduino-cli.exe", "arduino-cli");
        if (string.IsNullOrWhiteSpace(arduinoCliPath))
        {
            return (false,
                $"[TOOLCHAIN MISSING] 'arduino-cli' was not found in PATH or standard folders.\n" +
                $"Install Arduino CLI (https://arduino.github.io/arduino-cli) to flash C++ firmware over {rawPort} (FQBN: {fqbn}).",
                Array.Empty<BoxCodeDiagnostic>());
        }

        string sketchDir = Path.Combine(Path.GetTempPath(), "BreadboardAOI_Sketch_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(sketchDir);
            string sketchName = Path.GetFileName(sketchDir);
            string sketchFile = Path.Combine(sketchDir, sketchName + ".ino");
            await File.WriteAllTextAsync(sketchFile, code ?? string.Empty).ConfigureAwait(false);

            var cliLog = new StringBuilder();
            TimeSpan compileTimeout = TimeSpan.FromSeconds(60);
            TimeSpan uploadTimeout = TimeSpan.FromSeconds(45);

            string[] compileArgs = ProgrammerIdeCatalog.BuildCompileArguments(fqbn, sketchDir, options.VerboseOutput);
            cliLog.AppendLine($"> arduino-cli {string.Join(" ", compileArgs)}");
            (bool compOk, string compOut) = await RunCliProcessAsync(arduinoCliPath, compileArgs, compileTimeout).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(compOut))
                cliLog.AppendLine(compOut);

            var parsedDiags = ProgrammerIdeCatalog.ParseArduinoCliDiagnostics(compOut).ToList();
            if (!compOk)
            {
                cliLog.AppendLine($"[ERROR] arduino-cli compile failed for FQBN '{fqbn}'.");
                return (false, cliLog.ToString().TrimEnd(), parsedDiags);
            }

            if (options.VerifyOnly)
                return (true, cliLog.ToString().TrimEnd(), parsedDiags);

            string[] uploadArgs = ProgrammerIdeCatalog.BuildUploadArguments(fqbn, sketchDir, rawPort, options);
            cliLog.AppendLine($"> arduino-cli {string.Join(" ", uploadArgs)}");
            (bool upOk, string upOut) = await RunCliProcessAsync(arduinoCliPath, uploadArgs, uploadTimeout).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(upOut))
                cliLog.AppendLine(upOut);

            if (!upOk)
            {
                cliLog.AppendLine($"[ERROR] Physical upload to '{rawPort}' failed. Check USB cable, COM port selection, bootloader/BOOT0/RST mode, and installed board core.");
                return (false, cliLog.ToString().TrimEnd(), parsedDiags);
            }

            return (true, cliLog.ToString().TrimEnd(), parsedDiags);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"[ERROR] Physical upload error on {rawPort}: {ex.Message}", Array.Empty<BoxCodeDiagnostic>());
        }
        finally
        {
            CleanupTempDir(sketchDir);
        }
    }

    private static async Task<(bool Succeeded, bool ToolMissing, string OutputLog, IReadOnlyList<BoxCodeDiagnostic> Diagnostics)>
        TryCheckPythonSyntaxWithCliAsync(string code)
    {
        string? pythonPath = FindToolchainExecutable("python.exe", "python3.exe", "python");
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return (false, true, "[INFO] Python interpreter not found — static & semantic analyzer verification used.", Array.Empty<BoxCodeDiagnostic>());
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "BreadboardAOI_PyChk_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            string pyFile = Path.Combine(tempDir, "main.py");
            await File.WriteAllTextAsync(pyFile, code ?? string.Empty).ConfigureAwait(false);

            (bool ok, string output) = await RunCliProcessAsync(
                pythonPath,
                new[] { "-m", "py_compile", pyFile },
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var diags = ProgrammerIdeCatalog.ParsePythonSyntaxDiagnostics(output);
            string msg = ok
                ? "[PYTHON AST] Syntax & bytecode compilation passed (python -m py_compile)."
                : output;
            return (ok, false, msg, diags);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static async Task<(bool ExecutedAndSucceeded, string OutputLog, IReadOnlyList<BoxCodeDiagnostic> Diagnostics)>
        TryFlashMicroPythonCliAsync(
            string code,
            string rawPort,
            ArduinoUploadOptions options)
    {
        var syntaxCheck = await TryCheckPythonSyntaxWithCliAsync(code).ConfigureAwait(false);
        if (!syntaxCheck.ToolMissing && !syntaxCheck.Succeeded)
        {
            return (false,
                $"[MICROPYTHON SYNTAX ERROR]\n{syntaxCheck.OutputLog}",
                syntaxCheck.Diagnostics);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "BreadboardAOI_Py_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            string scriptFile = Path.Combine(tempDir, "main.py");
            await File.WriteAllTextAsync(scriptFile, code ?? string.Empty).ConfigureAwait(false);

            var (execBase, args) = ProgrammerIdeCatalog.BuildMicroPythonTransferCommand(
                options.MicroPythonTool,
                rawPort,
                scriptFile,
                options.UploadSpeedBaud);

            string? toolPath = FindToolchainExecutable(execBase + ".exe", execBase);
            if (!string.IsNullOrWhiteSpace(toolPath))
            {
                (bool ok, string output) = await RunCliProcessAsync(toolPath, args, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var log = new StringBuilder();
                log.AppendLine($"> {execBase} {string.Join(" ", args)}");
                if (!string.IsNullOrWhiteSpace(output))
                    log.AppendLine(output);
                if (!ok)
                    log.AppendLine($"[ERROR] MicroPython transfer via {execBase} failed on {rawPort}. Check board REPL availability and COM permissions.");
                return (ok, log.ToString().TrimEnd(), syntaxCheck.Diagnostics);
            }

            // Fallback: try launching `python -m mpremote ...` when python exists even if shim isn't in PATH
            string? pythonPath = FindToolchainExecutable("python.exe", "python3.exe", "python");
            if (!string.IsNullOrWhiteSpace(pythonPath))
            {
                string[] pyModArgs = new[] { "-m", execBase }.Concat(args).ToArray();
                (bool modOk, string modOut) = await RunCliProcessAsync(pythonPath, pyModArgs, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var log = new StringBuilder();
                log.AppendLine($"> python -m {execBase} {string.Join(" ", args)}");
                if (!string.IsNullOrWhiteSpace(modOut))
                    log.AppendLine(modOut);
                if (!modOk)
                    log.AppendLine($"[TOOL MISSING / FAILED] Could not transfer main.py via '{execBase}' on {rawPort}. Install 'pip install mpremote' (or ampy / rshell) for physical MicroPython upload.");
                return (modOk, log.ToString().TrimEnd(), syntaxCheck.Diagnostics);
            }

            return (false,
                $"[MICROPYTHON TOOL MISSING] Neither '{execBase}' nor Python ('mpremote'/'ampy'/'rshell') was found in PATH.\n" +
                $"Install MicroPython CLI (e.g., 'pip install mpremote') to copy main.py onto {rawPort}.",
                syntaxCheck.Diagnostics);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"[ERROR] MicroPython transfer error on {rawPort}: {ex.Message}", syntaxCheck.Diagnostics);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static void CleanupTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static string? FindToolchainExecutable(params string[] executableNames)
    {
        string? inPath = FindExecutableInPath(executableNames);
        if (!string.IsNullOrWhiteSpace(inPath))
            return inPath;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var extraDirs = new List<string>
        {
            Path.Combine(programFiles, "Arduino CLI"),
            Path.Combine(localAppData, "Programs", "arduino-cli"),
            Path.Combine(localAppData, "Arduino15", "packages", "arduino", "tools"),
            Path.Combine(programFiles, "Arduino IDE", "resources", "app", "lib", "backend", "resources")
        };

        try
        {
            foreach (string pyDir in Directory.GetDirectories(programFiles, "Python3*"))
            {
                extraDirs.Add(pyDir);
                extraDirs.Add(Path.Combine(pyDir, "Scripts"));
            }
            string userPyBase = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(userPyBase))
            {
                foreach (string pyDir in Directory.GetDirectories(userPyBase, "Python3*"))
                {
                    extraDirs.Add(pyDir);
                    extraDirs.Add(Path.Combine(pyDir, "Scripts"));
                }
            }
        }
        catch
        {
            // Ignore restricted directories
        }

        foreach (string dir in extraDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (string name in executableNames)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

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
        ExternalSerialTrafficRevision++;

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
        ExternalSerialTrafficRevision++;
        AppendSerialLog("[SIM] Virtual firmware execution halted.");
    }

    [RelayCommand]
    public void SendSerialInput()
    {
        if (string.IsNullOrWhiteSpace(SerialInputCommandText))
            return;

        string line = SerialInputCommandText.Trim();
        SerialInputCommandText = string.Empty;
        ExternalSerialTrafficRevision++;

        if (_hardwareSerialMonitor.IsOpen)
        {
            if (_hardwareSerialMonitor.TrySendLine(line, out string err))
            {
                AppendSerialLog($"[HW TX] > {line}");
            }
            else
            {
                AppendSerialLog($"[HW TX ERROR] {err}");
            }
            return;
        }

        _userSerialInputs.Add(line);
        AppendSerialLog($"> {line}");
        ExecuteFirmwareSimulationStep();
    }

    [RelayCommand]
    public void ClearSerialMonitor()
    {
        ExternalSerialTrafficRevision++;
        VirtualSerialLog = "[SERIAL @ 115200] Buffer cleared.\n";
        VirtualSerialRevision++;
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

    internal MicrocontrollerBoardType GetEffectiveTargetBoard()
    {
        if (string.Equals(TargetProgrammerBoardInstanceId, "MCU_2", StringComparison.OrdinalIgnoreCase) &&
            SecondaryDevBoardType != MicrocontrollerBoardType.None)
        {
            return SecondaryDevBoardType;
        }

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

    internal IReadOnlyCollection<string> ExtractWiredMcuPins()
    {
        var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string targetInst = string.IsNullOrWhiteSpace(TargetProgrammerBoardInstanceId)
            ? "MCU_1"
            : TargetProgrammerBoardInstanceId;

        foreach (OffBoardJumperWire wire in ActiveDevBoardWires)
        {
            CollectPinToken(wire.SourceEndpointKey, targetInst, pins);
            CollectPinToken(wire.TargetEndpointKey, targetInst, pins);
        }

        return pins;

        static void CollectPinToken(string key, string targetInst, HashSet<string> target)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("MCU", StringComparison.OrdinalIgnoreCase))
                return;

            int colon = key.LastIndexOf(':');
            if (colon <= 0 || colon >= key.Length - 1)
                return;

            string inst = key.Substring(0, colon);
            if (string.Equals(inst, targetInst, StringComparison.OrdinalIgnoreCase))
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
        VirtualSerialRevision++;
    }

    private void NotifyProgrammerComputedProperties()
    {
        OnPropertyChanged(nameof(IsTypeCCableSelected));
        OnPropertyChanged(nameof(IsTypeBCableSelected));
        OnPropertyChanged(nameof(IsMicroUsbCableSelected));
        OnPropertyChanged(nameof(SelectedCableSpec));
        OnPropertyChanged(nameof(SelectedCableBadgeText));
        OnPropertyChanged(nameof(ConnectCableButtonLabel));
        OnPropertyChanged(nameof(IsGlobalBoardConnected));
        OnPropertyChanged(nameof(GlobalConnectionBadgeText));
        OnPropertyChanged(nameof(GlobalSendButtonLabel));
        OnPropertyChanged(nameof(FirmwareStatusBadge));
        OnPropertyChanged(nameof(IsCableMismatchWarning));
        OnPropertyChanged(nameof(AvailableBoardVariants));
        OnPropertyChanged(nameof(ActiveBoardVariant));
        OnPropertyChanged(nameof(SupportsEspFlashOptions));
        OnPropertyChanged(nameof(SupportsUsbCdcOption));
        OnPropertyChanged(nameof(SupportsCpuFreqOption));
        OnPropertyChanged(nameof(EffectiveFqbnPreview));

        if (_boxCodingStudioWindow is { DataContext: BoxCodingStudioViewModel boxVm })
        {
            boxVm.UpdateProgrammerConnectionState(IsProgrammerCableConnected, GlobalConnectionBadgeText, IsCableMismatchWarning);
        }
    }
}
