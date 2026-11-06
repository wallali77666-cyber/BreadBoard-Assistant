// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.cs
// PURPOSE: Binds the 60 FPS AOI workstation to WPF. Owns the coordinator, the
//          SkiaSharp CAD renderer, the demo catalogue, layer switches, the DRC
//          list, the pin inspector and the streamed tutor transcript.
//
// THREADING: coordinator callbacks arrive on E-Core / P-Core worker threads and
// are marshalled onto the dispatcher. Observable state is refreshed by a 200 ms
// dispatcher timer, never per rendered frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.Views;
using BreadboardAOI.App.Workspace;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Core.KiCad;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;
using BreadboardAOI.Vision;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace BreadboardAOI.App.ViewModels;

/// <summary>One selectable entry in the left-hand demo catalogue.</summary>
public sealed class DemoScenarioItem
{
    public DemoScenarioItem(DemoScenario scenario)
    {
        Scenario = scenario;
        Title = DemoScenarioCatalog.GetTitle(scenario);
        Description = DemoScenarioCatalog.GetDescription(scenario);
    }

    public DemoScenario Scenario { get; }

    public string Title { get; }

    public string Description { get; }
}

/// <summary>Where the cognitive Qwen tutor runs.</summary>
public enum AiRouteMethod
{
    LocalQwen = 0,
    ColabTunnel = 1,
    LocalProvider = 2,
    ManualEndpoint = 3
}

/// <summary>One row in the AI ROUTING method picker.</summary>
public sealed record AiRouteOption(
    AiRouteMethod Method,
    string Title,
    string Badge,
    string Summary);

/// <summary>Camera source option shown by the toolbar cycle button.</summary>
public sealed record CameraOption(CameraSource Source, string Label);

/// <summary>Part palette entry for the X-ray circuit builder.</summary>
public sealed record BuildPartOption(ComponentKind Kind, string Label, string Hint);

/// <summary>Footprint choices: a 2-lead part, a 3-pin row, or a DIP across the trench.</summary>
public sealed record PinCountOption(int Count, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Quick value / IC-type preset for the next builder part.</summary>
public sealed record PartValuePreset(
    string Label,
    string Value,
    string? SymbolId = null,
    int? PinCount = null)
{
    public override string ToString() => Label;
}

/// <summary>One placed part rendered by the builder's parts list.</summary>
public sealed class DraftPartRow
{
    public DraftPartRow(DraftPart part)
    {
        Part = part;
    }

    public DraftPart Part { get; }

    public string Display => Part.Display;

    public string Detail => string.IsNullOrWhiteSpace(Part.Value)
        ? $"{Part.Pin1HoleId}  \u2192  {Part.Pin2HoleId}"
        : $"{Part.Value}   \u00b7   {Part.Pin1HoleId}  \u2192  {Part.Pin2HoleId}";

    public string SymbolNote => string.IsNullOrWhiteSpace(Part.SymbolId)
        ? "symbol: kind default"
        : Part.SymbolId;
}

/// <summary>Flattened DRC row for the right-hand violations list.</summary>
public sealed class ViolationRow
{
    public ViolationRow(DrcViolation violation)
    {
        IsDevBoard = false;
        Code = violation.Code;
        Severity = violation.Severity;
        ComponentId = violation.ComponentId;
        OffendingStripId = violation.OffendingStripId;
        BadHole = string.IsNullOrEmpty(violation.BadHole.HoleId) ? "--" : violation.BadHole.HoleId;
        TargetHole = string.IsNullOrEmpty(violation.SuggestedTargetHole.HoleId)
            ? "--"
            : violation.SuggestedTargetHole.HoleId;
        Message = violation.TechnicalMessage;
        CodeLabel = string.Create(CultureInfo.InvariantCulture, $"ERC {(ushort)violation.Code}");
        SeverityLabel = violation.Severity == DrcSeverity.Critical ? "CRITICAL" : "WARNING";
    }

    public ViolationRow(McuDrcViolation mcuViolation)
    {
        IsDevBoard = true;
        Code = ViolationCode.ErcVoltageOverload;
        Severity = mcuViolation.Severity == McuDrcSeverity.CriticalDamageRisk ? DrcSeverity.Critical : DrcSeverity.Warning;
        ComponentId = mcuViolation.NetName;
        OffendingStripId = 0;
        BadHole = mcuViolation.OffendingPins.Count > 0 ? mcuViolation.OffendingPins[0].PinId : "--";
        TargetHole = "--";
        Message = $"{mcuViolation.Title}: {mcuViolation.DetailedMessage} Remediation: {mcuViolation.RemediationAdvice}";
        CodeLabel = mcuViolation.RuleCode;
        SeverityLabel = mcuViolation.Severity == McuDrcSeverity.CriticalDamageRisk ? "CRITICAL RISK" : "WARNING";
    }

    public ViolationCode Code { get; }

    public DrcSeverity Severity { get; }

    public string ComponentId { get; }

    public int OffendingStripId { get; }

    public string BadHole { get; }

    public string TargetHole { get; }

    public string Message { get; }

    public string CodeLabel { get; }

    public string SeverityLabel { get; }

    /// <summary>True when this row came from the off-board MCU rule engine.</summary>
    public bool IsDevBoard { get; }

    public bool IsCritical => Severity == DrcSeverity.Critical;

    public bool HasCorrection => TargetHole != "--";
}


/// <summary>Runtime hardware allocation row for the CPU & GPU compute inspector.</summary>
public readonly record struct ComputeAllocationRow(
    string Role,
    string Cores,
    string Work,
    string Mask,
    string State,
    bool IsPinned,
    string CurrentCore);

/// <summary>Workstation view-model: owns the pipeline lifecycle and all dock state.</summary>
public sealed partial class MainWorkstationViewModel : ObservableObject, IDisposable
{
    private readonly SkiaArOverlayRenderer _renderer = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _remoteTimer;
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly DispatcherTimer _qwenProbeTimer;
    private int _qwenProbeBusy;
    private readonly object _transcriptGate = new();
    private readonly StringBuilder _transcriptBuffer = new(4096);
    private readonly List<ViolationRow> _violationScratch = new(16);

    // X-ray builder: the draft owns the parts, the coordinator only sees snapshots.
    private readonly CircuitDraft _draft = new(PinMatrixLookup.Shared);
    private HoleCoord _pendingBuildHole;
    private bool _hasPendingBuildHole;
    private DevBoardPinHit? _pendingMcuPinHit;
    private bool _demoWasEnabledBeforeBuild;

    private sealed record WorkstationCircuitSnapshot(
        List<DraftPart> Parts,
        List<OffBoardJumperWire> DevBoardWires,
        bool IsDevBoardDocked,
        MicrocontrollerBoardType SelectedDevBoardType,
        string Description);

    private readonly Stack<WorkstationCircuitSnapshot> _workstationUndoStack = new();
    private readonly Stack<WorkstationCircuitSnapshot> _workstationRedoStack = new();

    public int UndoStackDepth => _workstationUndoStack.Count;
    public int RedoStackDepth => _workstationRedoStack.Count;

    public void RecordUndoSnapshot(string description)
    {
        _workstationUndoStack.Push(new WorkstationCircuitSnapshot(
            _draft.Parts.Select(p => p with { Pins = (string[])p.Pins.Clone() }).ToList(),
            ActiveDevBoardWires.ToList(),
            IsDevBoardDocked,
            SelectedDevBoardType,
            description));
        _workstationRedoStack.Clear();
    }

    // Remote AI / Colab link. All of these are touched from the dispatcher only.
    private readonly GoogleTokenStore _tokenStore = new();
    private readonly GoogleClientConfigStore _clientStore = new();
    private readonly ManualEndpointStore _endpointStore = new();
    private readonly ColabDriveBridge _drive = new();
    private readonly HttpClient _probeHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private GoogleOAuthClient? _oauth;
    private GoogleTokenSet? _googleTokens;
    private ColabSession? _colabSession;
    private RemoteTutorTarget? _appliedRemoteTarget;
    private RemoteTutorTarget? _savedEndpoint;
    private int _remotePolling;

    private InspectionPipelineCoordinator? _coordinator;
    private bool _transcriptDirty;
    private bool _suppressScenarioSync;
    private string? _pendingImagePath;
    private bool _disposed;
    private bool _isSyncingPartValue;

    public static readonly IReadOnlyList<JumperColorOption> JumperColorOptions = ComponentCatalog.StandardJumperColors;

    public IReadOnlyList<JumperColorOption> JumperColors => JumperColorOptions;

    private static readonly PartValuePreset[] ResistorPresets = ConvertPresets(ComponentCatalog.StandardResistors);
    private static readonly PartValuePreset[] CapacitorPresets = ConvertPresets(ComponentCatalog.StandardCapacitors);
    private static readonly PartValuePreset[] LedPresets = ConvertPresets(ComponentCatalog.StandardLeds);
    private static readonly PartValuePreset[] IcPresets = ConvertPresets(ComponentCatalog.StandardIcs);
    private static readonly PartValuePreset[] ButtonPresets = ConvertPresets(ComponentCatalog.StandardButtons);
    private static readonly PartValuePreset[] DiodePresets = ConvertPresets(ComponentCatalog.StandardDiodes);
    private static readonly PartValuePreset[] TransistorPresets = ConvertPresets(ComponentCatalog.StandardTransistors);
    private static readonly PartValuePreset[] PotentiometerPresets = ConvertPresets(ComponentCatalog.StandardPotentiometers);
    private static readonly PartValuePreset[] RegulatorPresets = ConvertPresets(ComponentCatalog.StandardRegulators);
    private static readonly PartValuePreset[] BuzzerPresets = ConvertPresets(ComponentCatalog.StandardBuzzers);
    private static readonly PartValuePreset[] SensorPresets = ConvertPresets(ComponentCatalog.StandardSensors);
    private static readonly PartValuePreset[] BatteryPresets = ConvertPresets(ComponentCatalog.StandardBatteries);

    private static PartValuePreset[] ConvertPresets(IReadOnlyList<PartPreset> presets)
    {
        var array = new PartValuePreset[presets.Count];
        for (int i = 0; i < presets.Count; i++)
            array[i] = new PartValuePreset(presets[i].Label, presets[i].Value, presets[i].SymbolId, presets[i].PinCount);
        return array;
    }

    // ---- Adobe-style workspace layout --------------------------------------

    [ObservableProperty]
    private bool _isLeftDockVisible = true;

    [ObservableProperty]
    private bool _isRightDockVisible = true;

    [ObservableProperty]
    private double _leftDockWidth = 270;

    [ObservableProperty]
    private double _rightDockWidth = 338;

    /// <summary>Selected tab in the left dock: SCENE / BUILDER / REMOTE AI / VIEW.</summary>
    [ObservableProperty]
    private int _selectedLeftTabIndex;

    /// <summary>Zero when the left dock is hidden, so the canvas reclaims the space.</summary>
    public GridLength LeftDockColumnWidth =>
        new(IsLeftDockVisible ? LeftDockWidth : 0d);

    /// <summary>Zero when the right dock is hidden, so the canvas reclaims the space.</summary>
    public GridLength RightDockColumnWidth =>
        new(IsRightDockVisible ? RightDockWidth : 0d);

    public MainWorkstationViewModel()
        : this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
    {
    }

    public MainWorkstationViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _layoutSaveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            SaveWorkspaceLayout();
        };

        LoadWorkspaceLayout();

        ScenarioItems = new ObservableCollection<DemoScenarioItem>();
        foreach (DemoScenario scenario in DemoScenarioCatalog.All)
            ScenarioItems.Add(new DemoScenarioItem(scenario));

        CameraOptions = new ReadOnlyCollection<CameraOption>(new[]
        {
            new CameraOption(CameraSource.Synthetic, "SYNTHETIC BOARD"),
            new CameraOption(CameraSource.Camera0, "CAMERA 0 (WEBCAM)"),
            new CameraOption(CameraSource.Camera1, "CAMERA 1 (SECONDARY)"),
            new CameraOption(CameraSource.Camera2, "CAMERA 2 (USB)"),
            new CameraOption(CameraSource.Camera3, "CAMERA 3 (USB)")
        });

        Violations = new ObservableCollection<ViolationRow>();
        DraftParts = new ObservableCollection<DraftPartRow>();

        BuildPartOptions = new ReadOnlyCollection<BuildPartOption>(new[]
        {
            new BuildPartOption(ComponentKind.JumperWire, "JUMPER", "Plain wire between two tie points"),
            new BuildPartOption(ComponentKind.Resistor, "RESISTOR", "220R current limiter / 10K pull-up"),
            new BuildPartOption(ComponentKind.Led, "LED", "5 mm indicator, anode to the first hole"),
            new BuildPartOption(ComponentKind.Capacitor, "CAPACITOR", "10 uF timing or decoupling cap"),
            new BuildPartOption(ComponentKind.Diode, "DIODE", "1N4148 / 1N4007 / Zener diode"),
            new BuildPartOption(ComponentKind.Transistor, "TRANSISTOR", "2N3904 NPN / 2N3906 PNP / MOSFET"),
            new BuildPartOption(ComponentKind.IntegratedCircuit, "IC", "NE555P timer / Op-Amp across trench"),
            new BuildPartOption(ComponentKind.Potentiometer, "POTENTIOMETER", "10k trimpot or rotary dial"),
            new BuildPartOption(ComponentKind.VoltageRegulator, "REGULATOR", "L7805 +5V / LD33 +3.3V TO-220"),
            new BuildPartOption(ComponentKind.Sensor, "SENSOR", "LDR, Thermistor, HC-SR04, DHT11, PIR"),
            new BuildPartOption(ComponentKind.Battery, "BATTERY", "9V / MB102 / 4xAA / CR2032 power source"),
            new BuildPartOption(ComponentKind.Buzzer, "BUZZER", "5V active piezo buzzer"),
            new BuildPartOption(ComponentKind.PushButton, "BUTTON", "6 mm tact switch"),
            new BuildPartOption(ComponentKind.Microcontroller, "DEV BOARD", "Arduino Uno / Mega / ESP32 / S2 Mini / STM32")
        });

        PinCountOptions = new ReadOnlyCollection<PinCountOption>(new[]
        {
            new PinCountOption(2, "2-LEAD (R, C, LED, Diode, Sensor)"),
            new PinCountOption(3, "3-PIN ROW (TO-92 / TO-220 / Pot)"),
            new PinCountOption(4, "4-PIN ROW (HC-SR04 / DHT11 / MQ-2)"),
            new PinCountOption(8, "DIP-8 (555, op-amp)"),
            new PinCountOption(14, "DIP-14 (74LS00 ...)"),
            new PinCountOption(16, "DIP-16"),
            new PinCountOption(18, "DIP-18"),
            new PinCountOption(20, "DIP-20"),
            new PinCountOption(24, "DIP-24"),
            new PinCountOption(28, "DIP-28 (ATmega328)"),
            new PinCountOption(40, "DIP-40 (ATmega32)")
        });

        // The footprint list must exist before the palette selection fires its
        // change handler, which arms a default package for the IC entry.
        SelectedPartOption = BuildPartOptions[0];

        SelectedPinCount = PinCountOptions[0];

        _suppressScenarioSync = true;
        SelectedScenario = ScenarioItems[0];
        _suppressScenarioSync = false;

        SelectedCamera = CameraOptions[0];

        _renderer.HoverChanged += OnRendererHoverChanged;
        _renderer.Layers.ShowCameraUnderlay = ShowCameraUnderlay;
        _renderer.Layers.ShowAllStrips = ShowAllStrips;
        _renderer.Layers.ShowRealisticParts = ShowRealisticParts;
        _renderer.Layers.ShowLcdSimulation = ShowLcdSimulation;
        _renderer.Layers.LcdSimulatedRow0 = LcdSimulatedRow0;
        _renderer.Layers.LcdSimulatedRow1 = LcdSimulatedRow1;

        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _telemetryTimer.Tick += (_, _) => RefreshTelemetry();

        _remoteTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _remoteTimer.Tick += (_, _) => _ = PollColabAsync(force: false);

        _qwenProbeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _qwenProbeTimer.Tick += (_, _) => _ = RefreshQwenConnectionAsync();

        LoadGoogleSettings();

        // Demo/QA switch: preselect a scenario by keyword (555, floating, short)
        // so a support session can reproduce one fault without clicking SCENE.
        string? startupDemo = Environment.GetEnvironmentVariable("BREADBOARD_START_DEMO");
        if (!string.IsNullOrWhiteSpace(startupDemo))
            SelectScenarioByKeyword(startupDemo.Trim());

        string? startupImage = Environment.GetEnvironmentVariable("BREADBOARD_IMAGE_PATH");
        if (!string.IsNullOrWhiteSpace(startupImage) && File.Exists(startupImage))
            _pendingImagePath = startupImage;
    }

    // ---- catalogue / toolbar ------------------------------------------------

    public ObservableCollection<DemoScenarioItem> ScenarioItems { get; }

    public IReadOnlyList<CameraOption> CameraOptions { get; }

    /// <summary>Part palette for the X-ray builder.</summary>
    public IReadOnlyList<BuildPartOption> BuildPartOptions { get; }

    /// <summary>Footprint choices for the next placed part.</summary>
    public IReadOnlyList<PinCountOption> PinCountOptions { get; }

    /// <summary>Parts currently on the builder's breadboard.</summary>
    public ObservableCollection<DraftPartRow> DraftParts { get; }

    public string AppTitle => "BREADBOARD AOI / WORKSTATION";

    public string AppSubtitle => "830-POINT MATRIX  ·  KICAD DRC  ·  QWEN3-VL TUTOR";

    [ObservableProperty]
    private DemoScenarioItem? _selectedScenario;

    [ObservableProperty]
    private bool _demoModeEnabled = true;

    [ObservableProperty]
    private CameraOption? _selectedCamera;

    /// <summary>Absolute path of the still image currently replayed, or null.</summary>
    [ObservableProperty]
    private string? _loadedImagePath;

    [ObservableProperty]
    private bool _isVoiceMuted = IsEnvironmentFlagSet("BREADBOARD_MUTE_VOICE");

    [ObservableProperty]
    private string _cameraLabel = "CAMERA: SYNTHETIC BOARD";

    [ObservableProperty]
    private string _statusLine = "Initializing workstation...";

    // ---- layer switches ----------------------------------------------------

    [ObservableProperty]
    private bool _showPinMatrixDots = true;

    [ObservableProperty]
    private bool _showXRayStrips = true;

    [ObservableProperty]
    private bool _showRatsnestArrows = true;

    [ObservableProperty]
    private bool _showComponentBoxes;

    /// <summary>Draw every part as its physical 2D symbol rather than a CAD rectangle.</summary>
    [ObservableProperty]
    private bool _showRealisticParts = true;

    [ObservableProperty]
    private bool _showComponentLabels = true;

    [ObservableProperty]
    private bool _showRulers = true;

    [ObservableProperty]
    private bool _showTelemetry = true;

    [ObservableProperty]
    private bool _showCameraUnderlay;

    // ---- X-ray circuit builder --------------------------------------------

    /// <summary>
    /// While true the DRC judges the hand-built circuit instead of YOLO/demo
    /// detections. Toggled by the toolbar and the BUILDER dock.
    /// </summary>
    [ObservableProperty]
    private bool _isBuildMode;

    [ObservableProperty]
    private BuildPartOption? _selectedPartOption;

    [ObservableProperty]
    private DraftPartRow? _selectedDraftPart;

    /// <summary>Footprint of the next placed part: 2 leads, a 3-pin row, or a DIP.</summary>
    [ObservableProperty]
    private PinCountOption? _selectedPinCount;

    /// <summary>Optional printed value, e.g. 4K7 or NE555P. Blank uses the kind default.</summary>
    [ObservableProperty]
    private string _newPartValue = string.Empty;

    /// <summary>Optional KiCad symbol id, e.g. Timer/NE555P. Blank uses the kind default.</summary>
    [ObservableProperty]
    private string _newPartSymbol = string.Empty;

    /// <summary>One-click values for the selected palette kind (resistor, capacitor, IC type ...).</summary>
    public ObservableCollection<PartValuePreset> PartValuePresets { get; } = new();

    /// <summary>Preset picked in the BUILDER dock; fills value, symbol and pin count.</summary>
    [ObservableProperty]
    private PartValuePreset? _selectedPartValuePreset;

    /// <summary>Value editor for the part selected in PLACED PARTS.</summary>
    [ObservableProperty]
    private string _editPartValue = string.Empty;

    /// <summary>KiCad symbol id chosen for the selected part's builder footprint.</summary>
    [ObservableProperty]
    private string _editPartSymbol = string.Empty;

    public bool IsResistorSelected => SelectedPartOption?.Kind == ComponentKind.Resistor;
    public bool IsCapacitorSelected => SelectedPartOption?.Kind == ComponentKind.Capacitor;
    public bool IsIcSelected => SelectedPartOption?.Kind == ComponentKind.IntegratedCircuit;
    public bool IsJumperSelected => SelectedPartOption?.Kind == ComponentKind.JumperWire;
    public bool IsLedSelected => SelectedPartOption?.Kind == ComponentKind.Led;
    public bool IsButtonSelected => SelectedPartOption?.Kind == ComponentKind.PushButton;
    public bool IsDiodeSelected => SelectedPartOption?.Kind == ComponentKind.Diode;
    public bool IsTransistorSelected => SelectedPartOption?.Kind == ComponentKind.Transistor;
    public bool IsPotentiometerSelected => SelectedPartOption?.Kind == ComponentKind.Potentiometer;
    public bool IsRegulatorSelected => SelectedPartOption?.Kind == ComponentKind.VoltageRegulator;
    public bool IsBuzzerSelected => SelectedPartOption?.Kind == ComponentKind.Buzzer;
    public bool IsSensorSelected => SelectedPartOption?.Kind == ComponentKind.Sensor;
    public bool IsBatterySelected => SelectedPartOption?.Kind == ComponentKind.Battery;
    public bool IsDevBoardSelected => SelectedPartOption?.Kind == ComponentKind.Microcontroller;

    public string ActiveDevBoardName => SelectedDevBoardType switch
    {
        MicrocontrollerBoardType.None => "NO DEV BOARD (STANDALONE)",
        MicrocontrollerBoardType.ArduinoUnoR3R4 => "ARDUINO UNO",
        MicrocontrollerBoardType.ArduinoMega2560 => "MEGA 2560",
        MicrocontrollerBoardType.Esp32DevKitV1 => "ESP32 DEVKIT",
        MicrocontrollerBoardType.WemosEsp32S2Mini => "WEMOS S2 MINI",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => "STM32 BLUE PILL",
        _ => "DEV BOARD"
    };

    // ---- Off-Board Microcontrollers & Dev Boards (Builder Mode) -------------
    [ObservableProperty]
    private MicrocontrollerBoardType _selectedDevBoardType = MicrocontrollerBoardType.ArduinoUnoR3R4;

    partial void OnSelectedDevBoardTypeChanged(MicrocontrollerBoardType value)
    {
        OnPropertyChanged(nameof(IsNoneSelected));
        OnPropertyChanged(nameof(IsUnoSelected));
        OnPropertyChanged(nameof(IsMegaSelected));
        OnPropertyChanged(nameof(IsEsp32Selected));
        OnPropertyChanged(nameof(IsWemosSelected));
        OnPropertyChanged(nameof(IsStm32Selected));
    }

    public bool IsNoneSelected => SelectedDevBoardType == MicrocontrollerBoardType.None;
    public bool IsUnoSelected => SelectedDevBoardType == MicrocontrollerBoardType.ArduinoUnoR3R4;
    public bool IsMegaSelected => SelectedDevBoardType == MicrocontrollerBoardType.ArduinoMega2560;
    public bool IsEsp32Selected => SelectedDevBoardType == MicrocontrollerBoardType.Esp32DevKitV1;
    public bool IsWemosSelected => SelectedDevBoardType == MicrocontrollerBoardType.WemosEsp32S2Mini;
    public bool IsStm32Selected => SelectedDevBoardType == MicrocontrollerBoardType.Stm32BluePillF103C8;

    [ObservableProperty]
    private OffBoardDockPlacement _selectedDevBoardPlacement = OffBoardDockPlacement.BottomMargin;

    [ObservableProperty]
    private bool _isDevBoardDocked = true;

    [ObservableProperty]
    private string _devBoardSummary = "Arduino Uno R3: 14 Digital I/O, 6 Analog Inputs, 5V Logic, USB-B Connector.";

    [ObservableProperty]
    private string _devBoardStrappingNotes = "VIN: 7-12V, 5V Rail: max 500mA USB / 1A external. Standard 5V TTL logic level.";

    public IReadOnlyList<McuPinDefinition> SelectedDevBoardPins =>
        _renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType).Pins;

    // ---- AVOmeter (Digital Multimeter) state --------------------------------
    [ObservableProperty]
    private bool _isAvometerActive = true;

    [ObservableProperty]
    private string _avometerMode = "V_DC";

    [ObservableProperty]
    private string _avometerBlackHole = "BOT_GND_1";

    [ObservableProperty]
    private string _avometerRedHole = "TOP_VCC_1";

    [ObservableProperty]
    private string _avometerDisplayValue = " 5.000";

    [ObservableProperty]
    private string _avometerUnit = "V DC";

    [ObservableProperty]
    private string _avometerStatus = "READY · AUTO-RANGE";

    [ObservableProperty]
    private string _avometerSecondaryReading = "MAX 5.000V · MIN 4.998V";

    [ObservableProperty]
    private double _avometerBarPercent = 50.0;

    [ObservableProperty]
    private bool _isContinuityBeep;

    [ObservableProperty]
    private JumperColorOption _selectedJumperColor = JumperColorOptions[0];

    [ObservableProperty]
    private string _resistorBand1Color = "#D32F2F";

    [ObservableProperty]
    private string _resistorBand2Color = "#D32F2F";

    [ObservableProperty]
    private string _resistorBand3Color = "#794022";

    [ObservableProperty]
    private string _resistorBand4Color = "#CFB53B";

    [ObservableProperty]
    private string _resistorBandDescription = "220 Ω  (Red · Red · Brown · Gold ±5%)";

    // ---- Adobe-style workspace layout --------------------------------------

    private void LoadWorkspaceLayout()
    {
        WorkspaceLayoutState layout = WorkspaceLayoutStore.Load();

        // Properties (not backing fields) keep MVVM Toolkit's analyzer happy and
        // raise the column-width notifications for the first binding pass.
        IsLeftDockVisible = layout.LeftDockVisible;
        IsRightDockVisible = layout.RightDockVisible;
        LeftDockWidth = layout.LeftDockWidth;
        RightDockWidth = layout.RightDockWidth;
        SelectedLeftTabIndex = layout.ActiveLeftTab;

        // QA / demo switches. BREADBOARD_START_LAYOUT accepts the same keys as
        // the VIEW presets; BREADBOARD_START_VIEW opens that dock tab.
        string? startLayout = Environment.GetEnvironmentVariable("BREADBOARD_START_LAYOUT");
        if (!string.IsNullOrWhiteSpace(startLayout))
            ApplyLayoutPreset(startLayout);

        if (IsEnvironmentFlagSet("BREADBOARD_START_VIEW"))
            SelectedLeftTabIndex = 4;
        else if (IsEnvironmentFlagSet("BREADBOARD_START_CUSTOMIZE"))
            SelectedLeftTabIndex = 3;

        // Create the layout file on the first run. Doing it here (rather than
        // only on exit) means an operator can inspect or back up the defaults,
        // and a force-killed session still leaves a valid layout behind.
        SaveWorkspaceLayout();
    }

    /// <summary>
    /// Flushes the pending debounce immediately. The window calls this from
    /// Closing so a layout change survives even if the app is shut down before
    /// the 700 ms timer fires.
    /// </summary>
    public void SaveWorkspaceNow()
    {
        if (_disposed)
            return;

        _layoutSaveTimer.Stop();
        SaveWorkspaceLayout();
    }

    private void SaveWorkspaceLayout()
    {
        WorkspaceLayoutStore.Save(new WorkspaceLayoutState(
            IsLeftDockVisible,
            IsRightDockVisible,
            LeftDockWidth,
            RightDockWidth,
            SelectedLeftTabIndex));
    }

    private void ScheduleLayoutSave()
    {
        if (_disposed)
            return;

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    /// <summary>
    /// Presets shown in the VIEW panel. They mirror an InDesign workspace:
    /// show both docks, focus the canvas, or work with only one dock.
    /// </summary>
    [RelayCommand]
    private void ApplyLayoutPreset(string? preset)
    {
        string key = string.IsNullOrWhiteSpace(preset)
            ? "workstation"
            : preset.Trim().ToLowerInvariant();

        switch (key)
        {
            case "canvas":
                IsLeftDockVisible = false;
                IsRightDockVisible = false;
                StatusLine = "LAYOUT: WIDE CANVAS - both docks hidden. Use PANELS to bring them back.";
                break;

            case "inspect":
                IsLeftDockVisible = false;
                IsRightDockVisible = true;
                RightDockWidth = Math.Max(RightDockWidth, 338);
                StatusLine = "LAYOUT: INSPECT FOCUS - left dock hidden, DRC dock kept.";
                break;

            case "builder":
                IsLeftDockVisible = true;
                IsRightDockVisible = false;
                LeftDockWidth = Math.Max(LeftDockWidth, 320);
                SelectedLeftTabIndex = 1;
                StatusLine = "LAYOUT: BUILDER FOCUS - BUILDER dock open, DRC dock hidden.";
                break;

            case "customize":
                IsLeftDockVisible = true;
                SelectedLeftTabIndex = 3;
                StatusLine = "LAYOUT: CUSTOMIZE - pipeline engine toggles active.";
                break;

            case "reset":
            case "workstation":
            default:
                IsLeftDockVisible = true;
                IsRightDockVisible = true;
                LeftDockWidth = 270;
                RightDockWidth = 338;
                SelectedLeftTabIndex = 0;
                StatusLine = "LAYOUT: WORKSTATION - SCENE + DRC docks, default widths.";
                break;
        }

        ScheduleLayoutSave();
    }

    [RelayCommand]
    private void ResetLayout()
    {
        ApplyLayoutPreset("workstation");
        StatusLine = "LAYOUT RESET to the default workstation arrangement.";
        ScheduleLayoutSave();
    }

    /// <summary>
    /// Always-available escape hatch after WIDE CANVAS hides the VIEW panel
    /// itself. Restores both docks without discarding the saved widths.
    /// </summary>
    [RelayCommand]
    private void RestoreDocks()
    {
        IsLeftDockVisible = true;
        IsRightDockVisible = true;
        SelectedLeftTabIndex = 4;
        StatusLine = "LAYOUT: docks restored. Pick a preset or resize them in VIEW.";
        ScheduleLayoutSave();
    }

    partial void OnIsLeftDockVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(LeftDockColumnWidth));
        ScheduleLayoutSave();
    }

    partial void OnIsRightDockVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(RightDockColumnWidth));
        ScheduleLayoutSave();
    }

    partial void OnLeftDockWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftDockColumnWidth));
        ScheduleLayoutSave();
    }

    partial void OnRightDockWidthChanged(double value)
    {
        OnPropertyChanged(nameof(RightDockColumnWidth));
        ScheduleLayoutSave();
    }

    partial void OnSelectedLeftTabIndexChanged(int value) => ScheduleLayoutSave();

    [ObservableProperty]
    private string _viewportTitle = "2D BOARD VIEWPORT / X-RAY OVERLAY";

    [ObservableProperty]
    private string _builderHint = "Pick a part, then click its first tie point in the viewport.";

    [ObservableProperty]
    private string _circuitSummary = "EMPTY BOARD";

    [ObservableProperty]
    private string _netlistText = "NO NETS RESOLVED";

    [ObservableProperty]
    private string _buildPin1Text = string.Empty;

    [ObservableProperty]
    private string _buildPin2Text = string.Empty;

    [ObservableProperty]
    private string _localProviderStatus = "LOCAL AI: NOT PROBED";

    /// <summary>"LM STUDIO · first-token 240 MS · total 2.1 S" from the last turn.</summary>
    [ObservableProperty]
    private string _tutorLatency = "TUTOR TURN: NO DATA";

    [ObservableProperty]
    private bool _showAllStrips = true;

    // ---- DRC dock ----------------------------------------------------------

    public ObservableCollection<ViolationRow> Violations { get; }

    [ObservableProperty]
    private string _drcStatus = "AWAITING FRAME";

    [ObservableProperty]
    private string _drcSeverityKey = "Idle";

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _componentCount;

    [ObservableProperty]
    private string _violationSummary = "0 CRITICAL  ·  0 WARNING";

    // ---- pin inspector -----------------------------------------------------

    [ObservableProperty]
    private string _hoverHoleId = "--";

    [ObservableProperty]
    private string _hoverStripId = "--";

    [ObservableProperty]
    private string _hoverNetName = "NO PIN UNDER CURSOR";

    [ObservableProperty]
    private string _hoverStripType = "--";

    [ObservableProperty]
    private string _hoverOccupancy = "--";

    // ---- tutor dock --------------------------------------------------------

    [ObservableProperty]
    private string _tutorTranscript = string.Empty;

    [ObservableProperty]
    private string _tutorBackend = "TUTOR: INITIALIZING";

    /// <summary>True when the Qwen endpoint answers a health probe or a real completion.</summary>
    [ObservableProperty]
    private bool _isQwenConnected;

    /// <summary>Compact label beside the green connection light in the command bar.</summary>
    [ObservableProperty]
    private string _qwenConnectionLabel = "QWEN OFFLINE";

    [ObservableProperty]
    private bool _isTutorBusy;

    // ---- remote AI / colab link -------------------------------------------

    [ObservableProperty]
    private string _googleAccountStatus = "NOT LINKED";

    [ObservableProperty]
    private string _remoteAiStatus = "Colab link idle. Local tutor is active.";

    [ObservableProperty]
    private bool _useColabAi = IsEnvironmentFlagSet("BREADBOARD_USE_COLAB");

    [ObservableProperty]
    private string _googleClientId = string.Empty;

    [ObservableProperty]
    private string _googleClientSecret = string.Empty;

    [ObservableProperty]
    private string _manualEndpoint = string.Empty;

    [ObservableProperty]
    private string _manualApiKey = string.Empty;

    /// <summary>The single line the Colab notebook prints: BBAOI1|url|key.</summary>
    [ObservableProperty]
    private string _pairCode = string.Empty;

    [ObservableProperty]
    private bool _isRemoteBusy;

    [ObservableProperty]
    private string _remoteRouteLabel = "TUTOR ROUTE: LOCAL";

    // ---- Customization / Pipeline Engine Toggles ----------------------------

    [ObservableProperty]
    private bool _isOpenCvEnabled = true;

    [ObservableProperty]
    private bool _isYoloEnabled = true;

    [ObservableProperty]
    private bool _isHandGateEnabled = true;

    [ObservableProperty]
    private bool _isDrcEnabled = true;

    [ObservableProperty]
    private bool _isAlarmEnabled = true;

    [ObservableProperty]
    private bool _isCognitiveTutorEnabled = true;

    [ObservableProperty]
    private bool _isRuleBasedAdviceEnabled;

    [ObservableProperty]
    private bool _isAffinityEnabled = true;

    [ObservableProperty]
    private bool _isBmoVoiceStylingEnabled = true;

    public bool IsVoiceEnabled
    {
        get => !IsVoiceMuted;
        set => IsVoiceMuted = !value;
    }

    // ---- AI routing: method first, then only that method's settings ---------

    /// <summary>The four places the Qwen tutor can run. The picker selects one.</summary>
    public IReadOnlyList<AiRouteOption> AiRouteOptions { get; } =
    [
        new(AiRouteMethod.LocalQwen, "LOCAL QWEN", "ON THIS PC",
            "llama-server on the RTX 3060. No tunnel, no account."),
        new(AiRouteMethod.ColabTunnel, "COLAB TUNNEL", "REMOTE",
            "Paste the pair code from the Colab notebook, or auto-connect through Drive."),
        new(AiRouteMethod.LocalProvider, "LM STUDIO / OLLAMA", "LOCAL SERVER",
            "Reuse a model that is already loaded in your own desktop provider."),
        new(AiRouteMethod.ManualEndpoint, "MANUAL ENDPOINT", "ADVANCED",
            "Type an https tunnel URL and bearer key yourself.")
    ];

    [ObservableProperty]
    private AiRouteMethod _selectedAiRoute = AiRouteMethod.LocalQwen;

    public bool IsLocalQwenRoute => SelectedAiRoute == AiRouteMethod.LocalQwen;

    public bool IsColabRoute => SelectedAiRoute == AiRouteMethod.ColabTunnel;

    public bool IsLocalProviderRoute => SelectedAiRoute == AiRouteMethod.LocalProvider;

    public bool IsManualRoute => SelectedAiRoute == AiRouteMethod.ManualEndpoint;

    /// <summary>Header for the single settings card below the picker.</summary>
    public string SelectedAiRouteTitle => SelectedAiRoute switch
    {
        AiRouteMethod.ColabTunnel => "COLAB TUNNEL SETTINGS",
        AiRouteMethod.LocalProvider => "LM STUDIO / OLLAMA SETTINGS",
        AiRouteMethod.ManualEndpoint => "MANUAL ENDPOINT SETTINGS",
        _ => "LOCAL QWEN SETTINGS"
    };

    partial void OnSelectedAiRouteChanged(AiRouteMethod value)
    {
        OnPropertyChanged(nameof(IsLocalQwenRoute));
        OnPropertyChanged(nameof(IsColabRoute));
        OnPropertyChanged(nameof(IsLocalProviderRoute));
        OnPropertyChanged(nameof(IsManualRoute));
        OnPropertyChanged(nameof(SelectedAiRouteTitle));
    }

    // ---- model linking: any OpenAI-compatible server, any model ------------

    /// <summary>Model ids the active server currently advertises on /v1/models.</summary>
    public ObservableCollection<string> TutorModels { get; } = new();

    [ObservableProperty]
    private string? _selectedTutorModel;

    /// <summary>Exact model id typed by the operator; accepts ids not present in the list.</summary>
    [ObservableProperty]
    private string _tutorModelId = string.Empty;

    partial void OnSelectedTutorModelChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            TutorModelId = value;
    }

    [ObservableProperty]
    private string _tutorModelStatus = "MODEL: AUTO-DISCOVERY";

    /// <summary>
    /// Reads the server's own model list. Works for llama-server, LM Studio,
    /// Ollama, vLLM and any other OpenAI-compatible endpoint.
    /// </summary>
    [RelayCommand]
    private async Task RefreshTutorModelsAsync()
    {
        QwenMoEService? qwen = _coordinator?.QwenService;
        if (qwen is null)
        {
            TutorModelStatus = "MODEL: TUTOR DISABLED";
            return;
        }

        TutorModelStatus = "MODEL: LISTING /v1/models ...";
        IReadOnlyList<string> models = await qwen.ListAvailableModelsAsync().ConfigureAwait(true);

        TutorModels.Clear();
        foreach (string model in models)
            TutorModels.Add(model);

        OnPropertyChanged(nameof(HasTutorModels));

        string active = qwen.ActiveModelAlias;
        TutorModelId = active;
        SelectedTutorModel = models.FirstOrDefault(
            m => string.Equals(m, active, StringComparison.OrdinalIgnoreCase));

        TutorModelStatus = models.Count == 0
            ? $"MODEL: NOT LISTED · ACTIVE {active}"
            : $"MODEL: {models.Count} AVAILABLE · ACTIVE {active}";
    }

    public bool HasTutorModels => TutorModels.Count > 0;

    /// <summary>
    /// Pins the exact model id used for every chat request. The name travels in
    /// the request body, so mixed-case ids, GGUF quant suffixes and remotely
    /// hosted names are all passed through untouched.
    /// </summary>
    [RelayCommand]
    private void ApplyTutorModel()
    {
        QwenMoEService? qwen = _coordinator?.QwenService;
        if (qwen is null)
        {
            TutorModelStatus = "MODEL: TUTOR DISABLED";
            return;
        }

        string? model = NullIfBlank(TutorModelId);
        if (model is null)
        {
            TutorModelStatus = "MODEL: SELECT A MODEL OR TYPE AN EXACT ID";
            return;
        }

        qwen.SetModelAlias(model);

        if (_appliedRemoteTarget is not null)
        {
            RemoteTutorTarget updated = _appliedRemoteTarget with { ModelAlias = model };
            _appliedRemoteTarget = updated;
            RemoteRouteLabel = DescribeRoute(updated);

            if (updated.IsLoopback)
            {
                _savedEndpoint = updated;
                _endpointStore.Save(updated);
            }
        }

        TutorModelStatus = $"MODEL: ACTIVE {model}";
        RemoteAiStatus =
            $"Tutor model pinned to {model}. Any OpenAI-compatible model is accepted; " +
            "a text-only model automatically answers without the image payload.";
        _ = RefreshQwenConnectionAsync();
    }

    // ---- telemetry ---------------------------------------------------------

    [ObservableProperty]
    private string _frameTelemetry = "FRAME 0  ·  YOLO --.- MS  ·  DRC --.--- MS";

    [ObservableProperty]
    private string _visionBackend = "VISION: INITIALIZING";

    [ObservableProperty]
    private string _voiceBackend = "VOICE: INITIALIZING";

    [ObservableProperty]
    private bool _isVoiceEngineBusy;

    /// <summary>
    /// Empty when the requested engine loaded; otherwise the precise reason the
    /// workstation fell back (missing Piper model, no ElevenLabs key, ...).
    /// </summary>
    [ObservableProperty]
    private string _voiceSetupNote = string.Empty;

    /// <summary>ElevenLabs voice catalog loaded from the operator's account.</summary>
    public ObservableCollection<ElevenLabsVoice> ElevenLabsVoices { get; } = new();

    /// <summary>Models offered in the voice dock; turbo is the default for latency.</summary>
    public ObservableCollection<string> ElevenLabsModels { get; } = new()
    {
        ElevenLabsVoiceClient.DefaultModelId,
        "eleven_multilingual_v2",
        "eleven_flash_v2_5"
    };

    [ObservableProperty]
    private string _elevenLabsApiKey = string.Empty;

    [ObservableProperty]
    private string _elevenLabsModelId = ElevenLabsVoiceClient.DefaultModelId;

    [ObservableProperty]
    private ElevenLabsVoice? _selectedElevenLabsVoice;

    [ObservableProperty]
    private bool _isVoiceCatalogBusy;

    [ObservableProperty]
    private string _voiceCatalogNote = "Paste an ElevenLabs key, then refresh to list your voices.";

    /// <summary>Suppresses the auto-apply while the picker is being populated at startup.</summary>
    private bool _voiceSelectionSilent;

    [ObservableProperty]
    private string _vramTelemetry = "VRAM: --";

    [ObservableProperty]
    private string _affinityTelemetry = "AFFINITY: --";

    [ObservableProperty]
    private string _cpuPlanHeadline = "CPU · HYBRID WORKSTATION";

    [ObservableProperty]
    private string _gpuPlanHeadline = "GPU · HARDWARE ACCELERATION";

    [ObservableProperty]
    private string _gpuAllocationText = string.Empty;

    [ObservableProperty]
    private string _hardwareAllocationSummary = string.Empty;

    public ObservableCollection<ComputeAllocationRow> ComputeAllocation { get; } = new();

    [ObservableProperty]
    private string _targetNetlistStatus = "TARGET NETLIST: --";

    /// <summary>True once the pipeline exists; the window uses it to gate commands.</summary>
    public bool IsPipelineReady => _coordinator is not null;

    public SkiaArOverlayRenderer Renderer => _renderer;

    // ---- lifecycle ---------------------------------------------------------

    /// <summary>Starts the pipeline. Called from <c>MainWindow.Loaded</c>.</summary>
    public void Initialize()
    {
        if (_disposed || _coordinator is not null)
            return;

        InspectionPipelineCoordinator coordinator;
        try
        {
            ElevenLabsSettings savedVoice = VoiceSettingsStore.LoadElevenLabs();
            ElevenLabsApiKey = savedVoice.ApiKey;
            ElevenLabsModelId = savedVoice.ModelId;
            PopulateSavedVoice(savedVoice);

            coordinator = new InspectionPipelineCoordinator(new InspectionPipelineOptions
            {
                DemoModeEnabled = true,
                InitialDemoScenario = SelectedScenario?.Scenario ?? DemoScenario.SameStripLedShort,
                CameraSource = _pendingImagePath is not null ? CameraSource.ImageFile : SelectedCamera?.Source ?? CameraSource.Synthetic,
                ImagePath = _pendingImagePath,
                EnableCognitiveTutor = true,
                EnableVoice = true,
                VoicePreference = VoiceSettingsStore.Load(),
                ElevenLabsApiKey = savedVoice.ApiKey,
                ElevenLabsVoiceId = savedVoice.VoiceId,
                ElevenLabsVoiceName = savedVoice.VoiceName,
                ElevenLabsModelId = savedVoice.ModelId,
                EnableAffinity = true,
                EnableCameraPreviewBitmap = true
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"Pipeline startup failed: {ex.Message}";
            return;
        }

        _coordinator = coordinator;
        coordinator.SetVoiceMuted(IsVoiceMuted);
        coordinator.IsOpenCvEnabled = IsOpenCvEnabled;
        coordinator.IsYoloEnabled = IsYoloEnabled;
        coordinator.IsHandGateEnabled = IsHandGateEnabled;
        coordinator.IsDrcEnabled = IsDrcEnabled;
        coordinator.IsAlarmEnabled = IsAlarmEnabled;
        coordinator.IsCognitiveTutorEnabled = IsCognitiveTutorEnabled;
        coordinator.IsRuleBasedAdviceEnabled = IsRuleBasedAdviceEnabled;
        coordinator.ReflexDrcUpdated += OnReflexDrcUpdated;
        coordinator.TutorTurnStarted += OnTutorTurnStarted;
        coordinator.TutorTranscriptTokenReceived += OnTutorToken;
        coordinator.TutorDiagnosticCompleted += OnTutorCompleted;
        coordinator.StatusChanged += OnStatusChanged;
        coordinator.DemoScenarioChanged += OnDemoScenarioChanged;
        coordinator.EmergencyAlarmRaised += OnEmergencyAlarmRaised;
        coordinator.TutorTurnMeasured += OnTutorTurnMeasured;

        OnPropertyChanged(nameof(IsPipelineReady));

        if (IsBuildMode)
        {
            coordinator.SetManualCircuitEnabled(true);
            coordinator.SetManualDetections(_draft.BuildDetections());
        }

        try
        {
            coordinator.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"Pipeline failed to start: {ex.Message}";
        }

        CameraLabel = $"CAMERA: {coordinator.CameraStatus}";

        _telemetryTimer.Start();
        _qwenProbeTimer.Start();
        _ = RefreshQwenConnectionAsync();
        _ = RefreshTutorModelsAsync();
        RefreshTelemetry();

        // Clean CAD board: transparent underlay disabled so breadboard stays crisp.
        // Uploaded photos open directly in the dedicated AI Vision Inspection page.
        ShowCameraUnderlay = false;

        string? startupPhoto = _pendingImagePath;
        _pendingImagePath = null;
        _ = CompleteStartupAsync(startupPhoto);

        DrcStatePacket? packet = coordinator.LatestPacket;
        if (packet is not null)
            OnReflexDrcUpdated(packet);
    }

    /// <summary>
    /// Finishes startup off the constructor path: the remembered tutor endpoint
    /// must be live before any startup photo is inspected. The previous order
    /// opened the studio first, so the first photo was answered by the local
    /// llama-server / rule-based fallback even though LM Studio was saved.
    /// </summary>
    private async Task CompleteStartupAsync(string? startupPhoto)
    {
        try
        {
            if (_savedEndpoint is not null)
            {
                RemoteTutorTarget saved = _savedEndpoint;
                _savedEndpoint = null;
                SelectedAiRoute = saved.IsLoopback ? AiRouteMethod.LocalProvider : AiRouteMethod.ColabTunnel;
                await ConnectEndpointAsync(saved, persist: false).ConfigureAwait(true);
            }
            else if (UseColabAi)
            {
                await PollColabAsync(force: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A dead tutor must never block the workstation; the photo still opens
            // and the fallback tutor reports the precise reason.
            StatusLine = $"Tutor link skipped at startup: {ex.Message}";
        }
        finally
        {
            if (startupPhoto is not null && !_disposed)
                LoadImage(startupPhoto);
        }
    }

    /// <summary>
    /// Link path shared with the AI Live Studio. Probes the provider, adopts the
    /// model it advertises, remembers the endpoint for the next start and refreshes
    /// the MODEL LINK list. Returns true only when the tutor is genuinely live.
    /// </summary>
    public async Task<bool> LinkLocalProviderAsync(string presetId)
    {
        if (_disposed || _coordinator is null)
            return false;

        LocalAiPreset? preset = LocalAiProviders.Find(presetId);
        if (preset is null)
            return false;

        await ConnectLocalProviderAsync(presetId).ConfigureAwait(true);

        if (!_coordinator.UsingRemoteTutor || _appliedRemoteTarget is not { IsLoopback: true } applied)
            return false;

        // The studio link is an explicit operator choice, so it survives a restart
        // exactly like a pasted Colab pair code.
        _savedEndpoint = applied;
        _endpointStore.Save(applied);
        await RefreshTutorModelsAsync().ConfigureAwait(true);
        return _coordinator.QwenService?.IsConnected == true;
    }

    // ---- render loop -------------------------------------------------------

    /// <summary>Paints one CAD viewport frame. Called from <c>SKElement.PaintSurface</c>.</summary>
    public void Render(SKCanvas canvas, int width, int height)
    {
        if (_disposed)
            return;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        _renderer.RenderCadViewport(
            canvas,
            width,
            height,
            coordinator?.BoardBitmap,
            coordinator?.LatestPacket,
            coordinator?.LatestRatsnest);
    }

    public void UpdateHover(double viewportX, double viewportY)
    {
        if (_disposed)
            return;

        _renderer.UpdateHover((float)viewportX, (float)viewportY);
        SyncHoverInspector();
    }

    public void ClearHover()
    {
        if (_disposed)
            return;

        _renderer.ClearHover();
        SyncHoverInspector();
    }

    // ---- CAD Viewport Zoom & Pan --------------------------------------------

    [ObservableProperty]
    private double _viewportZoom = 1.0;

    public string ViewportZoomText => $"{Math.Round(ViewportZoom * 100):0}%";

    partial void OnViewportZoomChanged(double value) => OnPropertyChanged(nameof(ViewportZoomText));

    [RelayCommand]
    public void ZoomIn() => ZoomCadViewport(1.25, null);

    [RelayCommand]
    public void ZoomOut() => ZoomCadViewport(0.8, null);

    [RelayCommand]
    public void ResetZoom()
    {
        _renderer.ResetZoom();
        ViewportZoom = 1.0;
    }

    public void ZoomCadViewport(double factor, (float X, float Y)? focusPoint)
    {
        if (_disposed)
            return;

        if (focusPoint.HasValue)
        {
            _renderer.ZoomAt((float)factor, focusPoint.Value.X, focusPoint.Value.Y);
        }
        else
        {
            _renderer.ZoomAtCenter((float)factor);
        }

        ViewportZoom = Math.Round(_renderer.UserZoomFactor, 2);
    }

    public void PanViewport(float deltaX, float deltaY)
    {
        if (_disposed)
            return;

        _renderer.PanBy(deltaX, deltaY);
    }

    [RelayCommand] public void PanLeft() => PanViewport(50f, 0f);
    [RelayCommand] public void PanRight() => PanViewport(-50f, 0f);
    [RelayCommand] public void PanUp() => PanViewport(0f, 50f);
    [RelayCommand] public void PanDown() => PanViewport(0f, -50f);
    [RelayCommand] public void ResetPan() => ResetZoom();
    private void SuppressUnusedPan() {
    }

    // ---- X-ray builder interactions ---------------------------------------

    /// <summary>
    /// Left click places the two leads of the selected part or routes a jumper wire to/from a dev board pin socket;
    /// right click removes the last part or disconnected wire touching that pin/tie point.
    /// Snapping is delegated to the CAD renderer so a click and a hover always agree on the same target.
    /// </summary>
    public void HandleViewportClick(double viewportX, double viewportY, bool secondary)
    {
        if (_disposed || !IsBuildMode)
            return;

        UpdateHover(viewportX, viewportY);

        // A. Handle clicks on docked Dev Board pins
        if (_renderer.HoveredDevBoardPin is { } mcuPinHit)
        {
            string wireColor = SelectedPartOption?.Kind == ComponentKind.JumperWire
                ? SelectedJumperColor.HexColor
                : ResolveWireColor(SelectedDevBoardWireColor, mcuPinHit.Pin, _hasPendingBuildHole ? _pendingBuildHole.HoleId : null);

            if (secondary)
            {
                var attachedWires = ActiveDevBoardWires.Where(w =>
                    MatchesMcuEndpoint(w.SourceEndpointKey, mcuPinHit.BoardInstanceId, mcuPinHit.Pin) ||
                    MatchesMcuEndpoint(w.TargetEndpointKey, mcuPinHit.BoardInstanceId, mcuPinHit.Pin)).ToList();

                if (attachedWires.Count > 0)
                {
                    RecordUndoSnapshot($"Remove wires from {mcuPinHit.Pin.DisplayLabel}");
                    foreach (var wire in attachedWires)
                        ActiveDevBoardWires.Remove(wire);
                    EvaluateDevBoardCircuit();
                    BuilderHint = $"Removed {attachedWires.Count} jumper wire(s) connected to {mcuPinHit.BoardProfile.DisplayName} [{mcuPinHit.Pin.DisplayLabel}].";
                }
                else
                {
                    BuilderHint = $"No jumper wire connected to {mcuPinHit.BoardProfile.DisplayName} [{mcuPinHit.Pin.DisplayLabel}].";
                }
                return;
            }

            // Left click with a pending breadboard hole
            if (_hasPendingBuildHole)
            {
                RecordUndoSnapshot($"Wire {mcuPinHit.Pin.DisplayLabel} to {_pendingBuildHole.HoleId}");
                string pinKey = $"{mcuPinHit.BoardInstanceId}:{mcuPinHit.Pin.CanonicalId}";
                string holeKey = $"BB:{_pendingBuildHole.HoleId}";
                string net = DetermineDefaultNetName(mcuPinHit.Pin, null);

                var wire = new OffBoardJumperWire(
                    WireId: $"WIRE_{Guid.NewGuid():N}"[..12],
                    NetName: net,
                    SourceEndpointKey: pinKey,
                    TargetEndpointKey: holeKey,
                    InsulationHexColor: wireColor);

                ActiveDevBoardWires.Add(wire);
                string holeId = _pendingBuildHole.HoleId;
                ClearPendingBuild();
                EvaluateDevBoardCircuit();
                BuilderHint = $"Connected {mcuPinHit.BoardProfile.DisplayName} [{mcuPinHit.Pin.DisplayLabel}] to breadboard [{holeId}].";
                return;
            }

            // Left click with a pending MCU pin
            if (_pendingMcuPinHit is not null)
            {
                if (string.Equals(_pendingMcuPinHit.Pin.CanonicalId, mcuPinHit.Pin.CanonicalId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_pendingMcuPinHit.BoardInstanceId, mcuPinHit.BoardInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingBuild();
                    BuilderHint = "Placement cancelled (same MCU pin clicked twice).";
                    return;
                }

                RecordUndoSnapshot($"Wire {_pendingMcuPinHit.Pin.DisplayLabel} to {mcuPinHit.Pin.DisplayLabel}");
                string srcKey = $"{_pendingMcuPinHit.BoardInstanceId}:{_pendingMcuPinHit.Pin.CanonicalId}";
                string dstKey = $"{mcuPinHit.BoardInstanceId}:{mcuPinHit.Pin.CanonicalId}";
                string net = DetermineDefaultNetName(_pendingMcuPinHit.Pin, mcuPinHit.Pin);

                var wire = new OffBoardJumperWire(
                    WireId: $"WIRE_{Guid.NewGuid():N}"[..12],
                    NetName: net,
                    SourceEndpointKey: srcKey,
                    TargetEndpointKey: dstKey,
                    InsulationHexColor: wireColor);

                ActiveDevBoardWires.Add(wire);
                var prev = _pendingMcuPinHit;
                ClearPendingBuild();
                EvaluateDevBoardCircuit();
                BuilderHint = $"Connected {prev.Pin.DisplayLabel} to {mcuPinHit.Pin.DisplayLabel} ({wireColor}).";
                return;
            }

            // Start new wire from MCU pin
            _pendingMcuPinHit = mcuPinHit;
            _hasPendingBuildHole = false;
            _renderer.SetPendingDevBoardPin(mcuPinHit, wireColor);
            BuilderHint = $"Lead 1 = {mcuPinHit.BoardProfile.DisplayName} [{mcuPinHit.Pin.DisplayLabel}]. Now click a breadboard tie point or another pin to complete jumper wire.";
            return;
        }

        // B. Hit test placed components on the breadboard for direct interactive selection
        SKPoint boardPoint = _renderer.Transform.ToBoard((float)viewportX, (float)viewportY);
        if (!_hasPendingBuildHole && TryHitTestDraftPart(boardPoint, out DraftPart hitPart))
        {
            if (secondary)
            {
                RecordUndoSnapshot($"Remove {hitPart.KindLabel} #{hitPart.TrackId}");
                for (int i = 0; i < _draft.Parts.Count; i++)
                {
                    if (_draft.Parts[i].TrackId == hitPart.TrackId)
                    {
                        _draft.RemoveAt(i);
                        ClearPendingBuild();
                        CommitDraft();
                        BuilderHint = $"Removed {hitPart.KindLabel} #{hitPart.TrackId}.";
                        return;
                    }
                }
            }
            else
            {
                SelectDraftPart(hitPart);
                return;
            }
        }

        // C. Handle clicks on breadboard tie points
        if (_renderer.HoveredPin is not { } pin)
        {
            BuilderHint = "Aim at a tie point inside the breadboard or a dev board pin socket.";
            return;
        }

        HoleCoord hole = pin.Hole;

        if (secondary)
        {
            RecordUndoSnapshot($"Remove part or wire touching {hole.HoleId}");
            if (_draft.RemoveLastTouching(hole.HoleId))
            {
                ClearPendingBuild();
                CommitDraft();
                BuilderHint = $"Removed the last part touching {hole.HoleId}.";
            }
            else
            {
                var bbWires = ActiveDevBoardWires.Where(w =>
                    MatchesBreadboardEndpoint(w.SourceEndpointKey, hole.HoleId) ||
                    MatchesBreadboardEndpoint(w.TargetEndpointKey, hole.HoleId)).ToList();

                if (bbWires.Count > 0)
                {
                    foreach (var wire in bbWires)
                        ActiveDevBoardWires.Remove(wire);
                    EvaluateDevBoardCircuit();
                    BuilderHint = $"Removed {bbWires.Count} dev board jumper wire(s) touching {hole.HoleId}.";
                }
                else
                {
                    if (_workstationUndoStack.Count > 0)
                        _workstationUndoStack.Pop();
                    BuilderHint = $"No placed part or wire touches {hole.HoleId}.";
                }
            }
            return;
        }

        // Left click with a pending MCU pin
        if (_pendingMcuPinHit is not null)
        {
            RecordUndoSnapshot($"Wire {_pendingMcuPinHit.Pin.DisplayLabel} to {hole.HoleId}");
            string pinKey = $"{_pendingMcuPinHit.BoardInstanceId}:{_pendingMcuPinHit.Pin.CanonicalId}";
            string holeKey = $"BB:{hole.HoleId}";
            string color = SelectedPartOption?.Kind == ComponentKind.JumperWire
                ? SelectedJumperColor.HexColor
                : ResolveWireColor(SelectedDevBoardWireColor, _pendingMcuPinHit.Pin, hole.HoleId);
            string net = DetermineDefaultNetName(_pendingMcuPinHit.Pin, null);

            var wire = new OffBoardJumperWire(
                WireId: $"WIRE_{Guid.NewGuid():N}"[..12],
                NetName: net,
                SourceEndpointKey: pinKey,
                TargetEndpointKey: holeKey,
                InsulationHexColor: color);

            ActiveDevBoardWires.Add(wire);
            var prev = _pendingMcuPinHit;
            ClearPendingBuild();
            EvaluateDevBoardCircuit();
            BuilderHint = $"Connected {prev.BoardProfile.DisplayName} [{prev.Pin.DisplayLabel}] to breadboard [{hole.HoleId}].";
            return;
        }

        // Standard breadboard-to-breadboard placement
        if (SelectedPartOption is null)
        {
            BuilderHint = "Choose a part kind in the BUILDER dock first.";
            return;
        }

        int footprintPins = SelectedPinCount?.Count ?? 2;

        if (footprintPins >= 3)
        {
            TryPlaceFootprint(SelectedPartOption.Kind, hole.HoleId, footprintPins);
            return;
        }

        if (!_hasPendingBuildHole)
        {
            _pendingBuildHole = hole;
            _hasPendingBuildHole = true;
            string color = SelectedPartOption?.Kind == ComponentKind.JumperWire
                ? SelectedJumperColor.HexColor
                : ResolveWireColor(SelectedDevBoardWireColor, null, hole.HoleId);
            _renderer.SetPendingLead(hole, color);
            BuildPin1Text = hole.HoleId;
            BuilderHint = $"Lead 1 = {hole.HoleId}. Click a second tie point or a dev board pin.";
            return;
        }

        if (string.Equals(_pendingBuildHole.HoleId, hole.HoleId, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingBuild();
            BuilderHint = "Both leads landed on the same hole; placement cancelled.";
            return;
        }

        TryPlacePart(SelectedPartOption.Kind, _pendingBuildHole.HoleId, hole.HoleId);
    }

    /// <summary>
    /// Places a multi-lead part with pin 1 on <paramref name="anchorHoleId"/>.
    /// DIPs straddle the trench; a 3-pin part uses three adjacent columns.
    /// </summary>
    public bool TryPlaceFootprint(ComponentKind kind, string? anchorHoleId, int pinCount)
    {
        string[] pins;
        string? error;

        if (kind == ComponentKind.IntegratedCircuit)
        {
            if (!FootprintPlacer.TryPlaceDualRow(_draft.Matrix, anchorHoleId, pinCount, out pins, out error))
            {
                BuilderHint = error ?? "That DIP does not fit there.";
                return false;
            }
        }
        else if (pinCount >= 3)
        {
            if (!FootprintPlacer.TryPlaceSingleRow(_draft.Matrix, anchorHoleId, pinCount, out pins, out error))
            {
                BuilderHint = error ?? $"A {pinCount}-pin part needs {pinCount} adjacent columns in the same row.";
                return false;
            }
        }
        else
        {
            BuilderHint = "A multi-pin part needs at least three pins.";
            return false;
        }

        string value = ResolveValue(kind, pinCount);
        string symbol = ResolveSymbol(kind, pinCount);

        if (!IsBuildMode)
            IsBuildMode = true;

        RecordUndoSnapshot($"Place {kind} footprint");
        if (!_draft.TryAddFootprint(kind, pins, value, symbol, out DraftPart part, out error))
        {
            if (_workstationUndoStack.Count > 0)
                _workstationUndoStack.Pop();
            BuilderHint = error ?? "That part could not be placed.";
            return false;
        }

        ClearPendingBuild();
        BuildPin1Text = string.Empty;
        BuildPin2Text = string.Empty;
        CommitDraft(part.TrackId);

        BuilderHint = pinCount == 3
            ? $"Placed {part.KindLabel} #{part.TrackId}: pins {string.Join('/', pins)} ({value})."
            : $"Placed {part.KindLabel} #{part.TrackId}: {pinCount} pins across the trench, {pins[0]} to {pins[^1]} ({value}).";

        return true;
    }

    /// <summary>Places a part from the typed lead labels. Returns false with a hint on error.</summary>
    public bool TryPlacePart(ComponentKind kind, string? pin1HoleId, string? pin2HoleId)
    {
        if (!IsBuildMode)
            IsBuildMode = true;

        RecordUndoSnapshot($"Place {kind}");
        if (!_draft.TryAdd(
                kind,
                pin1HoleId,
                pin2HoleId,
                ResolveValue(kind, 2),
                out DraftPart part,
                out string? error))
        {
            if (_workstationUndoStack.Count > 0)
                _workstationUndoStack.Pop();
            BuilderHint = error ?? "That part could not be placed.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(NewPartSymbol))
            _draft.TrySetSymbol(part.TrackId, NewPartSymbol);

        ClearPendingBuild();
        BuildPin1Text = string.Empty;
        BuildPin2Text = string.Empty;
        CommitDraft(part.TrackId);
        BuilderHint = $"Placed {part.KindLabel} #{part.TrackId}: {part.Pin1HoleId} \u2192 {part.Pin2HoleId}.";
        return true;
    }

    public bool TryHitTestDraftPart(SKPoint boardPoint, out DraftPart hitPart)
    {
        hitPart = default;
        var matrix = PinMatrixLookup.Shared;

        if (_draft.Parts.Count == 0 && _coordinator?.LatestPacket?.Components is { Count: > 0 } liveComponents)
        {
            // Auto-import live packet components into draft so the operator can click and edit any visible part directly
            for (int j = 0; j < liveComponents.Count; j++)
            {
                _draft.TryAddDetection(liveComponents[j].RawDetection, out _);
            }
            if (_draft.Parts.Count > 0)
            {
                IsBuildMode = true;
                CommitDraft();
                return TryHitTestDraftPart(boardPoint, out hitPart);
            }
        }

        for (int i = _draft.Parts.Count - 1; i >= 0; i--)
        {
            DraftPart p = _draft.Parts[i];
            if (p.Pins.Length == 2)
            {
                if (matrix.TryGetHole(p.Pin1HoleId, out HoleCoord h1) &&
                    matrix.TryGetHole(p.Pin2HoleId, out HoleCoord h2))
                {
                    float dist = DistanceToSegment(boardPoint.X, boardPoint.Y, h1.Px, h1.Py, h2.Px, h2.Py);
                    if (dist <= 13.0f)
                    {
                        hitPart = p;
                        return true;
                    }
                }
            }
            else if (p.Pins.Length > 2)
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                bool any = false;
                foreach (string pinId in p.Pins)
                {
                    if (matrix.TryGetHole(pinId, out HoleCoord h))
                    {
                        minX = MathF.Min(minX, h.Px);
                        maxX = MathF.Max(maxX, h.Px);
                        minY = MathF.Min(minY, h.Py);
                        maxY = MathF.Max(maxY, h.Py);
                        any = true;
                    }
                }
                if (any && boardPoint.X >= minX - 8f && boardPoint.X <= maxX + 8f &&
                    boardPoint.Y >= minY - 8f && boardPoint.Y <= maxY + 8f)
                {
                    hitPart = p;
                    return true;
                }
            }
        }

        return false;
    }

    private static float DistanceToSegment(float px, float py, float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-4f)
        {
            float ddx = px - x1;
            float ddy = py - y1;
            return MathF.Sqrt(ddx * ddx + ddy * ddy);
        }

        float t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lenSq, 0f, 1f);
        float projX = x1 + t * dx;
        float projY = y1 + t * dy;
        float ex = px - projX;
        float ey = py - projY;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    public void SelectDraftPart(DraftPart part)
    {
        for (int i = 0; i < DraftParts.Count; i++)
        {
            if (DraftParts[i].Part.TrackId == part.TrackId)
            {
                SelectedDraftPart = DraftParts[i];
                break;
            }
        }

        _renderer.SelectedTrackId = part.TrackId;

        foreach (var opt in BuildPartOptions)
        {
            if (opt.Kind == part.Kind)
            {
                SelectedPartOption = opt;
                break;
            }
        }

        _isSyncingPartValue = true;
        try
        {
            NewPartValue = part.Value ?? string.Empty;
            EditPartValue = part.Value ?? string.Empty;
        }
        finally
        {
            _isSyncingPartValue = false;
        }

        if (part.Kind == ComponentKind.Resistor)
        {
            UpdateResistorBandPreview();
        }

        BuilderHint = $"Selected {part.KindLabel} #{part.TrackId} ({part.Value}). Type a new value or click presets to change resistance.";
    }

    /// <summary>Printed value for the next part: typed value, else a sensible default.</summary>
    private string ResolveValue(ComponentKind kind, int pinCount)
    {
        if (!string.IsNullOrWhiteSpace(NewPartValue))
            return NewPartValue.Trim();

        if (kind == ComponentKind.IntegratedCircuit && pinCount >= 4)
            return $"DIP-{pinCount}";

        if (kind == ComponentKind.IntegratedCircuit && pinCount == 3)
            return "MODULE-3";

        return CircuitDraft.DefaultValue(kind);
    }

    /// <summary>KiCad symbol id for the next part, defaulting per kind and pin count.</summary>
    private string ResolveSymbol(ComponentKind kind, int pinCount)
    {
        if (!string.IsNullOrWhiteSpace(NewPartSymbol))
            return NewPartSymbol.Trim();

        if (kind == ComponentKind.IntegratedCircuit && pinCount == 3)
            return "Connector_Generic/Conn_01x03";

        if (kind == ComponentKind.IntegratedCircuit && pinCount is 8 or 14 or 16)
            return KicadSymbolLibrary.DefaultSymbolId(ComponentKind.IntegratedCircuit);

        return KicadSymbolLibrary.DefaultSymbolId(kind);
    }

    [RelayCommand]
    private void PlaceTypedPart()
    {
        if (SelectedPartOption is null)
        {
            BuilderHint = "Choose a part kind first.";
            return;
        }

        string? pin1 = string.IsNullOrWhiteSpace(BuildPin1Text)
            ? (_hasPendingBuildHole ? _pendingBuildHole.HoleId : null)
            : BuildPin1Text;

        int footprintPins = SelectedPinCount?.Count ?? 2;

        // A DIP or 3-pin row is placed whole from pin 1; the second box is unused.
        if (SelectedPartOption.Kind == ComponentKind.IntegratedCircuit && footprintPins >= 3)
        {
            if (string.IsNullOrWhiteSpace(pin1))
            {
                BuilderHint = $"Type pin 1 of the {footprintPins}-pin footprint, for example E10.";
                return;
            }

            TryPlaceFootprint(SelectedPartOption.Kind, pin1, footprintPins);
            return;
        }

        if (string.IsNullOrWhiteSpace(BuildPin2Text))
        {
            BuilderHint = "Type the second tie point, for example D15 or TOP_GND_12.";
            return;
        }

        TryPlacePart(SelectedPartOption.Kind, pin1, BuildPin2Text);
    }

    [RelayCommand]
    private void CancelPendingLead()
    {
        ClearPendingBuild();
        BuilderHint = "Pending lead cleared.";
    }

    [RelayCommand]
    public void UndoCircuitPart()
    {
        if (_workstationUndoStack.Count > 0)
        {
            var prev = _workstationUndoStack.Pop();
            _workstationRedoStack.Push(new WorkstationCircuitSnapshot(
                _draft.Parts.Select(p => p with { Pins = (string[])p.Pins.Clone() }).ToList(),
                ActiveDevBoardWires.ToList(),
                IsDevBoardDocked,
                SelectedDevBoardType,
                prev.Description));

            _draft.RestoreParts(prev.Parts);
            ActiveDevBoardWires.Clear();
            foreach (var w in prev.DevBoardWires)
                ActiveDevBoardWires.Add(w);

            if (SelectedDevBoardType != prev.SelectedDevBoardType)
            {
                SelectedDevBoardType = prev.SelectedDevBoardType;
            }
            if (IsDevBoardDocked != prev.IsDevBoardDocked)
            {
                IsDevBoardDocked = prev.IsDevBoardDocked;
            }
            UpdateDevBoardState();

            ClearPendingBuild();
            CommitDraft();
            EvaluateDevBoardCircuit();
            BuilderHint = "Undo: Restored previous circuit state (Ctrl+Z).";
            return;
        }

        if (_draft.TryUndo())
        {
            ClearPendingBuild();
            CommitDraft();
            EvaluateDevBoardCircuit();
            BuilderHint = "Undo: Restored previous circuit state (Ctrl+Z).";
            return;
        }

        BuilderHint = "Nothing to undo.";
    }

    [RelayCommand]
    public void RedoCircuitPart()
    {
        if (_workstationRedoStack.Count > 0)
        {
            var next = _workstationRedoStack.Pop();
            _workstationUndoStack.Push(new WorkstationCircuitSnapshot(
                _draft.Parts.Select(p => p with { Pins = (string[])p.Pins.Clone() }).ToList(),
                ActiveDevBoardWires.ToList(),
                IsDevBoardDocked,
                SelectedDevBoardType,
                next.Description));

            _draft.RestoreParts(next.Parts);
            ActiveDevBoardWires.Clear();
            foreach (var w in next.DevBoardWires)
                ActiveDevBoardWires.Add(w);

            if (SelectedDevBoardType != next.SelectedDevBoardType)
            {
                SelectedDevBoardType = next.SelectedDevBoardType;
            }
            if (IsDevBoardDocked != next.IsDevBoardDocked)
            {
                IsDevBoardDocked = next.IsDevBoardDocked;
            }
            UpdateDevBoardState();

            ClearPendingBuild();
            CommitDraft();
            EvaluateDevBoardCircuit();
            BuilderHint = "Redo: Re-applied circuit state (Ctrl+Shift+Z).";
            return;
        }

        if (_draft.TryRedo())
        {
            ClearPendingBuild();
            CommitDraft();
            EvaluateDevBoardCircuit();
            BuilderHint = "Redo: Re-applied circuit state (Ctrl+Shift+Z).";
            return;
        }

        BuilderHint = "Nothing to redo.";
    }

    [RelayCommand]
    private void ClearCircuit()
    {
        RecordUndoSnapshot("Clear circuit");
        _draft.Clear();
        ClearPendingBuild();
        CommitDraft();
        BuilderHint = "Board cleared. Click two tie points to place the first part.";
    }

    [RelayCommand]
    private void RemoveSelectedCircuitPart()
    {
        if (SelectedDraftPart is not { } row)
        {
            BuilderHint = "Select a row in PLACED PARTS first.";
            return;
        }

        RecordUndoSnapshot($"Remove {row.Part.KindLabel} #{row.Part.TrackId}");
        int trackId = row.Part.TrackId;
        for (int i = 0; i < _draft.Parts.Count; i++)
        {
            if (_draft.Parts[i].TrackId != trackId)
                continue;

            _draft.RemoveAt(i);
            SelectedDraftPart = null;
            CommitDraft();
            BuilderHint = $"Removed {row.Part.KindLabel} #{trackId}.";
            return;
        }
    }

    /// <summary>Applies the value/symbol editors to the selected part.</summary>
    [RelayCommand]
    private void ApplyPartEdits()
    {
        if (SelectedDraftPart is not { } row)
        {
            BuilderHint = "Select a row in PLACED PARTS first.";
            return;
        }

        RecordUndoSnapshot($"Edit {row.Part.KindLabel} #{row.Part.TrackId}");
        int trackId = row.Part.TrackId;
        string value = EditPartValue?.Trim() ?? string.Empty;
        string symbol = EditPartSymbol?.Trim() ?? string.Empty;

        if (!_draft.TrySetValue(trackId, value) || !_draft.TrySetSymbol(trackId, symbol))
        {
            if (_workstationUndoStack.Count > 0)
                _workstationUndoStack.Pop();
            BuilderHint = $"Part #{trackId} is no longer on the board.";
            return;
        }

        CommitDraft();
        BuilderHint = $"Updated {row.Part.KindLabel} #{trackId}: value '{value}', symbol '{symbol}'.";
    }

    /// <summary>Opens the KiCad symbol browser for the next placed part.</summary>
    [RelayCommand]
    private void BrowseNewPartSymbol() => BrowseSymbol(editingSelected: false);

    /// <summary>Opens the KiCad symbol browser for the selected part.</summary>
    [RelayCommand]
    private void BrowseEditPartSymbol() => BrowseSymbol(editingSelected: true);

    private void BrowseSymbol(bool editingSelected)
    {
        if (editingSelected && SelectedDraftPart is null)
        {
            BuilderHint = "Select a row in PLACED PARTS first.";
            return;
        }

        var dialog = new SymbolPickerDialog(editingSelected ? EditPartSymbol : NewPartSymbol)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true)
            return;

        string? picked = dialog.SelectedSymbolId;

        if (editingSelected)
            EditPartSymbol = picked ?? string.Empty;
        else
            NewPartSymbol = picked ?? string.Empty;

        BuilderHint = string.IsNullOrEmpty(picked)
            ? "Symbol cleared; the footprint will use the kind default."
            : $"Symbol set to {picked}.";
    }

    /// <summary>Copies the demo selected in the SCENE dock into the editable draft.</summary>
    [RelayCommand]
    private void LoadExampleCircuit()
    {
        DemoScenario scenario = SelectedScenario?.Scenario ?? DemoScenario.SameStripLedShort;
        LoadDemoIntoDraft(scenario);
    }

    [RelayCommand]
    private void Load555Circuit() => LoadDemoIntoDraft(DemoScenario.Valid555Blinker);

    private void LoadDemoIntoDraft(DemoScenario scenario)
    {
        DetectedComponent[] detections = DemoScenarioCatalog.GetDetections(scenario);
        if (detections.Length == 0)
        {
            BuilderHint = "That scenario has no parts to copy into the builder.";
            return;
        }

        RecordUndoSnapshot($"Load demo {DemoScenarioCatalog.GetTitle(scenario)}");
        _draft.Clear();
        int copied = 0;

        for (int i = 0; i < detections.Length; i++)
        {
            if (_draft.TryAddDetection(detections[i], out _))
                copied++;
        }

        IsBuildMode = true;
        ClearPendingBuild();
        CommitDraft();
        BuilderHint = $"Loaded {copied} part(s) from {DemoScenarioCatalog.GetTitle(scenario)}.";
    }

    private void CommitDraft(int? preserveTrackId = null)
    {
        int? selectedTrack = preserveTrackId ?? SelectedDraftPart?.Part.TrackId;

        DraftParts.Clear();
        for (int i = 0; i < _draft.Parts.Count; i++)
            DraftParts.Add(new DraftPartRow(_draft.Parts[i]));

        SelectedDraftPart = null;
        if (selectedTrack is { } trackId)
        {
            for (int i = 0; i < DraftParts.Count; i++)
            {
                if (DraftParts[i].Part.TrackId != trackId)
                    continue;

                SelectedDraftPart = DraftParts[i];
                break;
            }
        }

        _renderer.SelectedTrackId = SelectedDraftPart?.Part.TrackId;

        CircuitSummary = _draft.Describe();
        _coordinator?.SetManualDetections(_draft.BuildDetections());
        RefreshNetlist(_coordinator?.LatestPacket);
        UpdateAvometerMeasurement();

        if (IsDevBoardDocked && ActiveDevBoardWires.Count > 0)
            EvaluateDevBoardCircuit();
    }

    private void ClearPendingBuild()
    {
        _hasPendingBuildHole = false;
        _pendingBuildHole = default;
        _pendingMcuPinHit = null;
        _renderer.ClearPendingLead();
    }

    private void RefreshNetlist(DrcStatePacket? packet)
    {
        NetlistText = packet is null ? "NO NETS RESOLVED" : NetlistReport.Format(packet);
    }

    private void SyncHoverInspector()
    {
        PinHoverInfo? hover = _renderer.HoveredPin;

        if (hover is not { } pin)
        {
            HoverHoleId = "--";
            HoverStripId = "--";
            HoverNetName = "NO PIN UNDER CURSOR";
            HoverStripType = "--";
            HoverOccupancy = "--";
            return;
        }

        HoverHoleId = pin.Hole.HoleId;
        HoverStripId = pin.StripId.ToString(CultureInfo.InvariantCulture);
        HoverNetName = string.IsNullOrEmpty(pin.NetName) ? "UNROUTED" : pin.NetName;
        HoverStripType = pin.IsPowerRail ? "POWER RAIL" : "TERMINAL STRIP";
        HoverOccupancy = DescribeOccupancy(pin.StripId);
    }

    private string DescribeOccupancy(int stripId)
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
            return "--";

        DrcStatePacket? packet = coordinator.LatestPacket;
        if (packet is null)
            return "0 LEADS";

        int leads = 0;
        foreach (SnappedComponent component in packet.Components)
        {
            if (component.Pin1Hole.StripId == stripId)
                leads++;

            if (component.Pin2Hole.StripId == stripId)
                leads++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{leads} LEAD{(leads == 1 ? string.Empty : "S")}");
    }

    // ---- commands ----------------------------------------------------------

    /// <summary>
    /// Explicitly sends the active circuit and DRC violations to Qwen AI for multimodal tutoring.
    /// In builder mode, Qwen only runs when this command is invoked.
    /// </summary>
    [RelayCommand]
    private void AskAi()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "AI TUTOR: pipeline is not ready yet.";
            return;
        }

        DrcStatePacket packet = coordinator.LatestPacket ?? DrcStatePacket.Empty;

        // Capture full high-resolution 1000x500 breadboard screenshot for Qwen vision
        byte[] fullBoardPng = _renderer.CaptureFullBoardPng(packet, coordinator.LatestRatsnest);
        coordinator.RequestAiDiagnostic(fullBoardPng);

        string? snapshotPath = null;
        try
        {
            string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BreadboardAOI", "cache");
            Directory.CreateDirectory(cacheDir);
            snapshotPath = Path.Combine(cacheDir, "ask_ai_breadboard.png");
            File.WriteAllBytes(snapshotPath, fullBoardPng);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            snapshotPath = null;
        }

        OpenAiVisionInspector(snapshotPath ?? LoadedImagePath);
        StatusLine = $"AI TUTOR: Studio opened with {packet.Components.Count} component(s) uploaded for Qwen analysis.";
    }

    [RelayCommand]
    private void ReplayDiagnostic()
    {
        _coordinator?.ReplayLatestDiagnostic();
    }

    [RelayCommand]
    private void TestAlarm()
    {
        _coordinator?.TestEmergencyAlarm();
    }

    // Speech & Voice engine logic moved to MainWorkstationViewModel.SpeechVoice.cs
    // Camera, AI Vision Studio, and Schematic Export logic moved to MainWorkstationViewModel.AiVision.cs
    // Remote AI, Google OAuth, and Colab bridge logic moved to MainWorkstationViewModel.RemoteColab.cs
    // ---- coordinator events ------------------------------------------------

    private void OnReflexDrcUpdated(DrcStatePacket packet)
    {
        InvokeOnDispatcher(() =>
        {
            _violationScratch.Clear();
            int critical = 0;
            int warnings = 0;

            for (int i = 0; i < packet.Violations.Count; i++)
            {
                DrcViolation violation = packet.Violations[i];
                _violationScratch.Add(new ViolationRow(violation));

                if (violation.Severity == DrcSeverity.Critical)
                    critical++;
                else
                    warnings++;
            }

            Violations.Clear();
            for (int i = 0; i < _violationScratch.Count; i++)
                Violations.Add(_violationScratch[i]);

            _violationScratch.Clear();

            // Dev-board violations are evaluated separately from the camera DRC
            // packet. Merge them here so a packet refresh does not wipe them and
            // a repeated evaluation does not duplicate them.
            IReadOnlyList<McuDrcViolation> devBoardViolations = _renderer.ActiveDevBoardViolations;
            for (int i = 0; i < devBoardViolations.Count; i++)
            {
                McuDrcViolation violation = devBoardViolations[i];
                Violations.Add(new ViolationRow(violation));
                if (violation.Severity == McuDrcSeverity.CriticalDamageRisk)
                    critical++;
                else
                    warnings++;
            }

            CriticalCount = critical;
            WarningCount = warnings;
            ComponentCount = packet.Components.Count;
            ViolationSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"{critical} CRITICAL  ·  {warnings} WARNING");

            if (IsBuildMode || DemoModeEnabled)
                RefreshNetlist(packet);

            if (critical > 0)
            {
                DrcStatus = critical == 1 ? "1 CRITICAL SHORT" : $"{critical} CRITICAL SHORTS";
                DrcSeverityKey = "Critical";
            }
            else if (warnings > 0)
            {
                DrcStatus = warnings == 1 ? "1 WARNING" : $"{warnings} WARNINGS";
                DrcSeverityKey = "Warning";
            }
            else if (packet.Components.Count == 0)
            {
                DrcStatus = "NO DETECTIONS";
                DrcSeverityKey = "Idle";
            }
            else
            {
                DrcStatus = "DRC PASS · NETLIST CLEAN";
                DrcSeverityKey = "Pass";
            }

            // A demo emergency can arrive a frame before build/import mode takes
            // over. Once the checker reports no critical fault the red banner is
            // stale, so replace it with the live DRC summary.
            if (critical == 0 && StatusLine.StartsWith("EMERGENCY:", StringComparison.Ordinal))
                StatusLine = DrcStatus;

            UpdateAvometerMeasurement();
        });
    }

    private void OnTutorTurnStarted()
    {
        lock (_transcriptGate)
        {
            _transcriptBuffer.Clear();
            _transcriptDirty = true;
        }
    }

    private void OnTutorToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return;

        lock (_transcriptGate)
        {
            _transcriptBuffer.Append(token);
            if (_transcriptBuffer.Length > 12000)
                _transcriptBuffer.Remove(0, _transcriptBuffer.Length - 12000);

            _transcriptDirty = true;
        }
    }

    private void OnTutorCompleted(string answer)
    {
        lock (_transcriptGate)
        {
            _transcriptDirty = true;
        }
    }

    private void OnTutorTurnMeasured(TutorTurnMetrics metrics)
    {
        InvokeOnDispatcher(() => TutorLatency = metrics.Describe());
    }

    private void OnStatusChanged(string message)
    {
        InvokeOnDispatcher(() => StatusLine = message);
    }

    private void OnDemoScenarioChanged(DemoScenario scenario)
    {
        InvokeOnDispatcher(() =>
        {
            _suppressScenarioSync = true;
            for (int i = 0; i < ScenarioItems.Count; i++)
            {
                if (ScenarioItems[i].Scenario != scenario)
                    continue;

                SelectedScenario = ScenarioItems[i];
                break;
            }

            _suppressScenarioSync = false;
        });
    }

    private void OnEmergencyAlarmRaised(string reason)
    {
        InvokeOnDispatcher(() => StatusLine = $"EMERGENCY: {reason}");
    }

    private void OnRendererHoverChanged(object? sender, EventArgs e) => SyncHoverInspector();

    private void InvokeOnDispatcher(Action action)
    {
        if (_disposed)
            return;

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    // ---- property change handlers -----------------------------------------

    /// <summary>Selects the first scenario whose enum name or title contains the keyword.</summary>
    private void SelectScenarioByKeyword(string keyword)
    {
        for (int i = 0; i < ScenarioItems.Count; i++)
        {
            DemoScenarioItem item = ScenarioItems[i];
            if (!item.Scenario.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _suppressScenarioSync = true;
            SelectedScenario = item;
            _suppressScenarioSync = false;
            return;
        }
    }

    partial void OnSelectedScenarioChanged(DemoScenarioItem? value)
    {
        if (_suppressScenarioSync || value is null)
            return;

        // In build mode the DRC judges the draft, so selecting a demo must not
        // silently turn the injector back on. Offer the explicit copy instead.
        if (IsBuildMode)
        {
            BuilderHint = $"'{value.Title}' selected. Press LOAD SELECTED DEMO in the BUILDER dock to copy it.";
            return;
        }

        DemoModeEnabled = true;
        _coordinator?.SetDemoMode(true, value.Scenario);
    }

    partial void OnDemoModeEnabledChanged(bool value)
    {
        _coordinator?.SetDemoMode(value, value ? SelectedScenario?.Scenario : null);
    }

    partial void OnSelectedCameraChanged(CameraOption? value)
    {
        if (value is null)
            return;

        CameraLabel = $"CAMERA: {value.Label}";
        if (value.Source != CameraSource.ImageFile && value.Source != CameraSource.VideoFile)
            _pendingImagePath = null;

        if (value.Source != CameraSource.Synthetic)
            DemoModeEnabled = false;
        if (value.Source == CameraSource.Synthetic && !DemoModeEnabled)
            DemoModeEnabled = true;
            _pendingImagePath = null;

        _coordinator?.SetCameraSource(value.Source);
    }

    partial void OnShowPinMatrixDotsChanged(bool value) => _renderer.Layers.ShowPinMatrixDots = value;

    partial void OnShowXRayStripsChanged(bool value) => _renderer.Layers.ShowXRayStrips = value;

    partial void OnShowRatsnestArrowsChanged(bool value) => _renderer.Layers.ShowRatsnestArrows = value;

    partial void OnShowComponentBoxesChanged(bool value) => _renderer.Layers.ShowComponentBoxes = value;

    partial void OnShowRealisticPartsChanged(bool value) => _renderer.Layers.ShowRealisticParts = value;

    partial void OnShowComponentLabelsChanged(bool value) => _renderer.Layers.ShowComponentLabels = value;

    partial void OnShowRulersChanged(bool value) => _renderer.Layers.ShowRulers = value;

    partial void OnShowTelemetryChanged(bool value) => _renderer.Layers.ShowTelemetry = value;

    partial void OnShowCameraUnderlayChanged(bool value) => _renderer.Layers.ShowCameraUnderlay = value;

    partial void OnShowAllStripsChanged(bool value) => _renderer.Layers.ShowAllStrips = value;

    private static IReadOnlyList<PartValuePreset> PresetsFor(ComponentKind kind) => kind switch
    {
        ComponentKind.Resistor => ResistorPresets,
        ComponentKind.Capacitor => CapacitorPresets,
        ComponentKind.Led => LedPresets,
        ComponentKind.IntegratedCircuit => IcPresets,
        ComponentKind.PushButton => ButtonPresets,
        ComponentKind.Diode => DiodePresets,
        ComponentKind.Transistor => TransistorPresets,
        ComponentKind.Potentiometer => PotentiometerPresets,
        ComponentKind.VoltageRegulator => RegulatorPresets,
        ComponentKind.Buzzer => BuzzerPresets,
        ComponentKind.Sensor => SensorPresets,
        ComponentKind.Battery => BatteryPresets,
        _ => Array.Empty<PartValuePreset>()
    };

    private void RefreshPartValuePresets(ComponentKind kind)
    {
        PartValuePresets.Clear();
        SelectedPartValuePreset = null;

        foreach (PartValuePreset preset in PresetsFor(kind))
            PartValuePresets.Add(preset);
    }

    partial void OnSelectedPartValuePresetChanged(PartValuePreset? value)
    {
        if (value is null)
            return;

        NewPartValue = value.Value;
        NewPartSymbol = value.SymbolId ?? string.Empty;

        if (value.PinCount is int count)
        {
            foreach (PinCountOption option in PinCountOptions)
            {
                if (option.Count != count)
                    continue;

                SelectedPinCount = option;
                break;
            }
        }

        BuilderHint = value.PinCount is int pins
            ? $"{value.Label} armed: {pins}-pin footprint, symbol {value.SymbolId}. Click pin 1 to place it."
            : $"{value.Label} armed: value {value.Value}, symbol {value.SymbolId ?? "kind default"}.";
    }

    partial void OnNewPartValueChanged(string value)
    {
        if (IsResistorSelected)
            UpdateResistorBandPreview();

        SyncValueToTargetPart(value);
    }

    private void SyncValueToTargetPart(string value)
    {
        if (_disposed || _isSyncingPartValue || string.IsNullOrWhiteSpace(value))
            return;

        string trimmed = value.Trim();

        // 1. If a part is selected and matches the current arming kind, update it directly
        if (SelectedDraftPart is { } selected && selected.Part.Kind == SelectedPartOption?.Kind)
        {
            if (!string.Equals(selected.Part.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                RecordUndoSnapshot($"Change {selected.Part.KindLabel} #{selected.Part.TrackId} to {trimmed}");
                _draft.TrySetValue(selected.Part.TrackId, trimmed);
                _isSyncingPartValue = true;
                try
                {
                    EditPartValue = trimmed;
                }
                finally
                {
                    _isSyncingPartValue = false;
                }
                CommitDraft(selected.Part.TrackId);
                BuilderHint = $"Updated {selected.Part.KindLabel} #{selected.Part.TrackId} value to {trimmed}.";
            }
            return;
        }

        // 2. If no part is explicitly selected, but there are placed parts of this kind on the board,
        // automatically update the most recently placed part (or single placed part) of that kind!
        var matchingParts = _draft.Parts.Where(p => p.Kind == SelectedPartOption?.Kind).ToList();
        if (matchingParts.Count > 0)
        {
            var targetPart = matchingParts[^1];
            if (!string.Equals(targetPart.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                RecordUndoSnapshot($"Change {targetPart.KindLabel} #{targetPart.TrackId} to {trimmed}");
                _draft.TrySetValue(targetPart.TrackId, trimmed);
                CommitDraft(targetPart.TrackId);
                BuilderHint = $"Updated {targetPart.KindLabel} #{targetPart.TrackId} value to {trimmed}.";
            }
        }
    }

    partial void OnSelectedPartOptionChanged(BuildPartOption? value)
    {
        OnPropertyChanged(nameof(IsResistorSelected));
        OnPropertyChanged(nameof(IsCapacitorSelected));
        OnPropertyChanged(nameof(IsIcSelected));
        OnPropertyChanged(nameof(IsJumperSelected));
        OnPropertyChanged(nameof(IsLedSelected));
        OnPropertyChanged(nameof(IsButtonSelected));
        OnPropertyChanged(nameof(IsDiodeSelected));
        OnPropertyChanged(nameof(IsTransistorSelected));
        OnPropertyChanged(nameof(IsPotentiometerSelected));
        OnPropertyChanged(nameof(IsRegulatorSelected));
        OnPropertyChanged(nameof(IsBuzzerSelected));
        OnPropertyChanged(nameof(IsSensorSelected));
        OnPropertyChanged(nameof(IsBatterySelected));
        OnPropertyChanged(nameof(IsDevBoardSelected));

        if (value?.Kind == ComponentKind.Microcontroller)
        {
            UpdateDevBoardState();
        }

        if (value is null)
            return;

        RefreshPartValuePresets(value.Kind);

        _isSyncingPartValue = true;
        try
        {
            if (SelectedDraftPart is { } currentSel && currentSel.Part.Kind == value.Kind)
            {
                NewPartValue = currentSel.Part.Value ?? string.Empty;
                NewPartSymbol = currentSel.Part.SymbolId ?? string.Empty;
            }
            else
            {
                switch (value.Kind)
                {
                    case ComponentKind.Resistor:
                        NewPartValue = "220R";
                        NewPartSymbol = "Device/R";
                        break;
                    case ComponentKind.Capacitor:
                        NewPartValue = "100nF";
                        NewPartSymbol = "Device/C";
                        break;
                    case ComponentKind.JumperWire:
                        NewPartValue = SelectedJumperColor.Code;
                        NewPartSymbol = string.Empty;
                        break;
                    case ComponentKind.Led:
                        NewPartValue = "RED 2.0V";
                        NewPartSymbol = "Device/LED";
                        break;
                    case ComponentKind.IntegratedCircuit:
                        NewPartValue = "NE555P";
                        NewPartSymbol = "Timer/NE555P";
                        break;
                    case ComponentKind.Diode:
                        NewPartValue = "1N4148";
                        NewPartSymbol = "Device/D";
                        break;
                    case ComponentKind.Transistor:
                        NewPartValue = "2N3904";
                        NewPartSymbol = "Transistor_BJT/2N3904";
                        break;
                    case ComponentKind.Potentiometer:
                        NewPartValue = "10K_TRIM";
                        NewPartSymbol = "Device/R_Potentiometer_Trim";
                        break;
                    case ComponentKind.VoltageRegulator:
                        NewPartValue = "L7805";
                        NewPartSymbol = "Regulator_Linear/L7805";
                        break;
                    case ComponentKind.Buzzer:
                        NewPartValue = "BUZZER 5V";
                        NewPartSymbol = "Device/Buzzer";
                        break;
                    case ComponentKind.Sensor:
                        NewPartValue = "LDR GL5528";
                        NewPartSymbol = "Sensor_Optical/LDR_GL5528";
                        break;
                    case ComponentKind.Battery:
                        NewPartValue = "9V";
                        NewPartSymbol = "Device/Battery";
                        break;
                }
            }

            if (value.Kind == ComponentKind.Resistor)
                UpdateResistorBandPreview();
        }
        finally
        {
            _isSyncingPartValue = false;
        }

        // The constructor sets the palette before the footprint list exists.
        IReadOnlyList<PinCountOption>? footprints = PinCountOptions;
        if (footprints is not { Count: > 0 })
            return;

        if (value.Kind == ComponentKind.Transistor || value.Kind == ComponentKind.Potentiometer || value.Kind == ComponentKind.VoltageRegulator)
        {
            SelectedPinCount = footprints.Count > 1 ? footprints[1] : footprints[0];
        }
        else if (value.Kind == ComponentKind.IntegratedCircuit)
        {
            if (SelectedPinCount is null || SelectedPinCount.Count < 3)
            {
                for (int i = 0; i < footprints.Count; i++)
                {
                    if (footprints[i].Count != 8)
                        continue;

                    SelectedPinCount = footprints[i];
                    break;
                }
            }
        }
        else
        {
            SelectedPinCount = footprints[0];
        }

        if (_hasPendingBuildHole)
            return;

        BuilderHint = (SelectedPinCount?.Count ?? 2) >= 3
            ? $"{value.Label} selected: click pin 1; the footprint is placed in one click."
            : $"{value.Label} selected: click its first tie point.";
    }

    // DevBoard & MCU wiring logic moved to MainWorkstationViewModel.DevBoardWiring.cs

    [RelayCommand]
    private void SelectJumperColor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        foreach (JumperColorOption opt in JumperColorOptions)
        {
            if (string.Equals(opt.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                SelectedJumperColor = opt;
                NewPartValue = opt.Code;
                BuilderHint = $"Jumper wire color: {opt.Name} ({opt.Usage}). Click two tie points to place.";
                break;
            }
        }
    }

    [RelayCommand]
    private void SetResistance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        string trimmed = value.Trim();
        NewPartValue = trimmed;
        NewPartSymbol = "Device/R";
        UpdateResistorBandPreview();
        SyncValueToTargetPart(trimmed);
        BuilderHint = $"Resistor armed: {value}. {ResistorBandDescription}. Click two tie points.";
    }

    [RelayCommand]
    private void SetCapacitance(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : (parts[0].Contains("uF", StringComparison.OrdinalIgnoreCase) ? "Device/C_Polarized" : "Device/C");
        BuilderHint = $"Capacitor armed: {parts[0]}. Click two tie points to place.";
    }

    [RelayCommand]
    private void SetLedColor(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        foreach (var opt in BuildPartOptions)
        {
            if (opt.Kind == ComponentKind.Led)
            {
                SelectedPartOption = opt;
                break;
            }
        }

        string trimmed = spec.Trim();
        NewPartValue = trimmed;
        NewPartSymbol = "Device/LED";
        SyncValueToTargetPart(trimmed);
        BuilderHint = $"LED armed: {spec}. Click tie point 1 for Anode (+), tie point 2 for Cathode (-).";
    }

    [RelayCommand]
    private void SetDiode(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Device/D";
        BuilderHint = $"Diode armed: {parts[0]}. Lead 1 is Anode (+), Lead 2 is Cathode (- stripe). Click two tie points.";
    }

    [RelayCommand]
    private void SetTransistor(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Transistor_BJT/2N3904";
        BuilderHint = $"Transistor armed: {parts[0]} (3-pin TO-92 row). Click pin 1 to place.";
    }

    [RelayCommand]
    private void SetPotentiometer(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Device/R_Potentiometer_Trim";
        BuilderHint = $"Potentiometer armed: {parts[0]} (3-pin row: Pin 1, Wiper 2, Pin 3). Click pin 1 to place.";
    }

    [RelayCommand]
    private void SetRegulator(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Regulator_Linear/L7805";
        BuilderHint = $"Regulator armed: {parts[0]} (TO-220: Pin 1-IN, 2-GND, 3-OUT). Click pin 1 to place.";
    }

    [RelayCommand]
    private void SetBuzzer(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Device/Buzzer";
        BuilderHint = $"Buzzer armed: {parts[0]}. Lead 1 is Positive (+), Lead 2 is Negative (-). Click two tie points.";
    }

    [RelayCommand]
    private void SetSensor(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Sensor_Optical/LDR_GL5528";
        if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int pins))
        {
            foreach (PinCountOption option in PinCountOptions)
            {
                if (option.Count == pins)
                {
                    SelectedPinCount = option;
                    break;
                }
            }
        }
        BuilderHint = $"Sensor armed: {parts[0]}. Click tie point to place.";
    }

    [RelayCommand]
    private void SetBattery(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        string[] parts = spec.Split('|');
        NewPartValue = parts[0].Trim();
        NewPartSymbol = parts.Length > 1 ? parts[1].Trim() : "Device/Battery";
        if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int pins))
        {
            foreach (PinCountOption option in PinCountOptions)
            {
                if (option.Count == pins)
                {
                    SelectedPinCount = option;
                    break;
                }
            }
        }
        else
        {
            SelectedPinCount = PinCountOptions[0]; // 2-lead
        }
        BuilderHint = $"Battery armed: {parts[0]}. Lead 1 is Positive (+), Lead 2 is Negative (-). Click two tie points.";
    }

    // ---- AVOmeter (Digital Multimeter) Engine & Commands -------------------

    partial void OnAvometerBlackHoleChanged(string value) => UpdateAvometerMeasurement();
    partial void OnAvometerRedHoleChanged(string value) => UpdateAvometerMeasurement();
    partial void OnAvometerModeChanged(string value) => UpdateAvometerMeasurement();
    partial void OnIsAvometerActiveChanged(bool value)
    {
        _renderer.IsDmmProbesVisible = value;
        UpdateAvometerMeasurement();
    }

    [RelayCommand]
    private void SetAvometerMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return;

        AvometerMode = mode.Trim().ToUpperInvariant();
        UpdateAvometerMeasurement();
        BuilderHint = $"AVOmeter mode set to: {AvometerMode}";
    }

    [RelayCommand]
    private void MeasurePowerRails()
    {
        AvometerBlackHole = "BOT_GND_1";
        AvometerRedHole = "TOP_VCC_1";
        AvometerMode = "V_DC";
        UpdateAvometerMeasurement();
        BuilderHint = "AVOmeter probes connected across main power rails (GND to VCC).";
    }

    [RelayCommand]
    private void AttachAvometerToSelectedPart()
    {
        if (SelectedDraftPart is not { } row)
        {
            BuilderHint = "Select a placed part in PLACED PARTS first.";
            return;
        }

        DraftPart p = row.Part;
        if (p.Pins.Length >= 2)
        {
            AvometerBlackHole = p.Pin1HoleId;
            AvometerRedHole = p.Pin2HoleId;
            if (p.Kind == ComponentKind.Resistor)
                AvometerMode = "OHMS";
            else if (p.Kind is ComponentKind.Led or ComponentKind.Diode or ComponentKind.Battery)
                AvometerMode = "V_DC";

            UpdateAvometerMeasurement();
            BuilderHint = $"AVOmeter probes attached to {p.KindLabel} #{p.TrackId} ({AvometerBlackHole} and {AvometerRedHole}).";
        }
    }

    [RelayCommand]
    private void SwapAvometerProbes()
    {
        string temp = AvometerBlackHole;
        AvometerBlackHole = AvometerRedHole;
        AvometerRedHole = temp;
        UpdateAvometerMeasurement();
        BuilderHint = $"Swapped AVOmeter probes: COM={AvometerBlackHole}, RED={AvometerRedHole}.";
    }

    [RelayCommand]
    private void ClearAvometerProbes()
    {
        AvometerBlackHole = string.Empty;
        AvometerRedHole = string.Empty;
        _renderer.DmmBlackProbe = null;
        _renderer.DmmRedProbe = null;
        UpdateAvometerMeasurement();
        BuilderHint = "AVOmeter probes removed from breadboard.";
    }

    public void UpdateAvometerMeasurement()
    {
        if (!IsAvometerActive)
        {
            _renderer.IsDmmProbesVisible = false;
            return;
        }

        bool hasBlack = _draft.Matrix.TryGetHole(AvometerBlackHole, out HoleCoord blackHole);
        bool hasRed = _draft.Matrix.TryGetHole(AvometerRedHole, out HoleCoord redHole);

        _renderer.DmmBlackProbe = hasBlack ? blackHole : null;
        _renderer.DmmRedProbe = hasRed ? redHole : null;
        _renderer.IsDmmProbesVisible = true;

        if (!hasBlack || !hasRed)
        {
            AvometerDisplayValue = " ----";
            AvometerStatus = "OPEN PROBE (ATTACH LEADS)";
            AvometerBarPercent = 0.0;
            IsContinuityBeep = false;
            return;
        }

        int blackStrip = blackHole.StripId;
        int redStrip = redHole.StripId;
        bool sameStrip = blackStrip == redStrip;

        // Find connected components in Draft
        DraftPart? directPart = null;
        for (int i = 0; i < _draft.Parts.Count; i++)
        {
            DraftPart p = _draft.Parts[i];
            if (p.Pins.Length < 2)
                continue;

            bool h1Ok = _draft.Matrix.TryGetHole(p.Pin1HoleId, out HoleCoord h1);
            bool h2Ok = _draft.Matrix.TryGetHole(p.Pin2HoleId, out HoleCoord h2);
            if (!h1Ok || !h2Ok)
                continue;

            int s1 = h1.StripId;
            int s2 = h2.StripId;
            if ((s1 == blackStrip && s2 == redStrip) || (s1 == redStrip && s2 == blackStrip))
            {
                directPart = p;
                break;
            }
        }

        // Determine physical battery / DC power supply status
        bool hasBattery = false;
        double batteryVoltage = 0.0;
        int batteryPosStrip = -1;
        int batteryNegStrip = -1;

        for (int i = 0; i < _draft.Parts.Count; i++)
        {
            if (_draft.Parts[i].Kind == ComponentKind.Battery)
            {
                hasBattery = true;
                string v = _draft.Parts[i].Value?.ToUpperInvariant() ?? string.Empty;
                if (v.Contains("9V")) batteryVoltage = 9.0;
                else if (v.Contains("6V")) batteryVoltage = 6.0;
                else if (v.Contains("3.7V") || v.Contains("18650")) batteryVoltage = 3.7;
                else if (v.Contains("3V") || v.Contains("CR2032")) batteryVoltage = 3.0;
                else if (v.Contains("3.3V")) batteryVoltage = 3.3;
                else if (v.Contains("MB102") || v.Contains("5V")) batteryVoltage = 5.0;
                else batteryVoltage = 9.0;

                DraftPart bp = _draft.Parts[i];
                if (_draft.Matrix.TryGetHole(bp.Pin1HoleId, out HoleCoord bh1) &&
                    _draft.Matrix.TryGetHole(bp.Pin2HoleId, out HoleCoord bh2))
                {
                    batteryPosStrip = bh1.StripId;
                    batteryNegStrip = bh2.StripId;
                }
                break;
            }
        }

        // Trace whether rails are energized by the battery or power source
        bool railsEnergized = false;
        double supplyVoltage = 0.0;

        if (hasBattery && _coordinator is { } coord)
        {
            int batPosRoot = coord.GetRootNet(batteryPosStrip);
            int batNegRoot = coord.GetRootNet(batteryNegStrip);

            int topVccRoot = coord.GetRootNet(BreadboardSpec.StripTopVcc);
            int topGndRoot = coord.GetRootNet(BreadboardSpec.StripTopGnd);
            int botVccRoot = coord.GetRootNet(BreadboardSpec.StripBotVcc);
            int botGndRoot = coord.GetRootNet(BreadboardSpec.StripBotGnd);

            bool posConnectedToRail = batPosRoot == topVccRoot || batPosRoot == botVccRoot ||
                                      batteryPosStrip is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc;
            bool negConnectedToRail = batNegRoot == topGndRoot || batNegRoot == botGndRoot ||
                                      batteryNegStrip is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd;

            if (posConnectedToRail && negConnectedToRail)
            {
                railsEnergized = true;
                supplyVoltage = batteryVoltage;
            }
        }

        bool isBlackGnd = blackStrip is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd;
        bool isRedVcc = redStrip is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc;
        bool isRedGnd = redStrip is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd;
        bool isBlackVcc = blackStrip is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc;

        switch (AvometerMode)
        {
            case "V_DC":
                AvometerUnit = "V DC";
                if (sameStrip)
                {
                    AvometerDisplayValue = " 0.000";
                    AvometerStatus = "EQUIPOTENTIAL NET (0.000 V)";
                    AvometerBarPercent = 0.0;
                    AvometerSecondaryReading = "0.000V DC · Same Strip";
                }
                else if (isBlackGnd && isRedVcc)
                {
                    if (!railsEnergized)
                    {
                        AvometerDisplayValue = " 0.000";
                        AvometerStatus = "UNPOWERED RAILS (NO BATTERY CONNECTED)";
                        AvometerBarPercent = 0.0;
                        AvometerSecondaryReading = "0.000V DC · Connect battery to power rails";
                    }
                    else
                    {
                        AvometerDisplayValue = $"{supplyVoltage,6:0.000}";
                        AvometerStatus = $"MAIN POWER RAIL ({supplyVoltage:0.0}V)";
                        AvometerBarPercent = Math.Clamp(supplyVoltage / 12.0 * 100.0, 5.0, 95.0);
                        AvometerSecondaryReading = $"MAX {supplyVoltage:0.000}V · MIN {supplyVoltage - 0.002:0.000}V";
                    }
                }
                else if (isBlackVcc && isRedGnd)
                {
                    if (!railsEnergized)
                    {
                        AvometerDisplayValue = " 0.000";
                        AvometerStatus = "UNPOWERED RAILS (NO BATTERY CONNECTED)";
                        AvometerBarPercent = 0.0;
                        AvometerSecondaryReading = "0.000V DC · Connect battery to power rails";
                    }
                    else
                    {
                        AvometerDisplayValue = $"{-supplyVoltage,6:0.000}";
                        AvometerStatus = "REVERSE POLARITY (-V)";
                        AvometerBarPercent = 0.0;
                        AvometerSecondaryReading = "REVERSE LEAD CONNECTION";
                    }
                }
                else if (directPart is { } dp)
                {
                    string dpVal = dp.Value?.ToUpperInvariant() ?? string.Empty;
                    if (dp.Kind == ComponentKind.Battery)
                    {
                        bool isRedOnPos = redHole.HoleId == dp.Pin1HoleId;
                        double measuredBat = isRedOnPos ? batteryVoltage : -batteryVoltage;
                        AvometerDisplayValue = $"{measuredBat,6:0.000}";
                        AvometerStatus = isRedOnPos ? $"BATTERY TERMINAL VOLTAGE ({dp.Value})" : "BATTERY REVERSE POLARITY (-V)";
                        AvometerBarPercent = Math.Clamp(batteryVoltage / 12.0 * 100.0, 5.0, 95.0);
                        AvometerSecondaryReading = $"{batteryVoltage:0.000}V DC Open Circuit";
                    }
                    else if (dp.Kind == ComponentKind.Led)
                    {
                        double vf = dpVal.Contains("BLU") ? 3.12 : (dpVal.Contains("GRN") ? 2.15 : 1.98);
                        AvometerDisplayValue = $"{vf,6:0.000}";
                        AvometerStatus = $"LED FORWARD VOLTAGE ({dp.Value})";
                        AvometerBarPercent = (vf / 5.0) * 100.0;
                        AvometerSecondaryReading = $"Vf = {vf:0.000}V · Silicon Junction";
                    }
                    else if (dp.Kind == ComponentKind.Diode)
                    {
                        double vf = dpVal.Contains("5819") ? 0.380 : 0.685;
                        AvometerDisplayValue = $"{vf,6:0.000}";
                        AvometerStatus = $"DIODE FORWARD DROP ({dp.Value})";
                        AvometerBarPercent = (vf / 5.0) * 100.0;
                        AvometerSecondaryReading = $"Vf = {vf:0.000}V · Silicon Junction";
                    }
                    else if (dp.Kind == ComponentKind.Resistor)
                    {
                        if (!railsEnergized)
                        {
                            AvometerDisplayValue = " 0.000";
                            AvometerStatus = "UNPOWERED RESISTOR (0.000 V)";
                            AvometerBarPercent = 0.0;
                            AvometerSecondaryReading = "0.000V DC · Circuit Unpowered";
                        }
                        else
                        {
                            double vDrop = supplyVoltage * 0.5;
                            AvometerDisplayValue = $"{vDrop,6:0.000}";
                            AvometerStatus = $"RESISTOR DROP ({dp.Value})";
                            AvometerBarPercent = (vDrop / supplyVoltage) * 100.0;
                            AvometerSecondaryReading = $"ΔV = {vDrop:0.000}V across {dp.Value}";
                        }
                    }
                    else
                    {
                        AvometerDisplayValue = " 0.000";
                        AvometerStatus = "UNPOWERED / PASSIVE";
                        AvometerBarPercent = 0.0;
                        AvometerSecondaryReading = "0.000V DC · Hi-Z";
                    }
                }
                else
                {
                    AvometerDisplayValue = " 0.000";
                    AvometerStatus = "FLOATING / UNPOWERED";
                    AvometerBarPercent = 0.0;
                    AvometerSecondaryReading = "0.000V DC · Hi-Z";
                }
                IsContinuityBeep = false;
                break;

            case "OHMS":
                if (sameStrip)
                {
                    AvometerDisplayValue = "  0.02";
                    AvometerUnit = "Ω";
                    AvometerStatus = "SHORT / DIRECT STRIP";
                    AvometerBarPercent = 1.0;
                    AvometerSecondaryReading = "LEAD RESISTANCE: 0.02 Ω";
                }
                else if (directPart is { } rp && rp.Kind == ComponentKind.Resistor && ResistorColorCode.TryParseOhms(rp.Value, out double ohms))
                {
                    FormatResistance(ohms);
                    AvometerStatus = $"RESISTOR NOMINAL: {rp.Value}";
                    AvometerSecondaryReading = $"Tolerance ±5% · {rp.Value}";
                }
                else if (directPart is { } jp && jp.Kind == ComponentKind.JumperWire)
                {
                    AvometerDisplayValue = "  0.04";
                    AvometerUnit = "Ω";
                    AvometerStatus = "JUMPER WIRE CONTINUITY";
                    AvometerBarPercent = 1.0;
                    AvometerSecondaryReading = "WIRE RESISTANCE: 0.04 Ω";
                }
                else
                {
                    AvometerDisplayValue = "   O.L";
                    AvometerUnit = "MΩ";
                    AvometerStatus = "OPEN CIRCUIT / HIGH-Z";
                    AvometerBarPercent = 100.0;
                    AvometerSecondaryReading = "> 40.00 MΩ (OPEN)";
                }
                IsContinuityBeep = false;
                break;

            case "CONT":
                AvometerUnit = "Ω";
                if (sameStrip || (directPart is { } cp && cp.Kind == ComponentKind.JumperWire))
                {
                    AvometerDisplayValue = "  0.02";
                    AvometerStatus = "CONTINUITY BEEP (< 10 Ω)";
                    AvometerBarPercent = 1.0;
                    AvometerSecondaryReading = "BUZZER ACTIVE · SHORT CIRCUIT";
                    IsContinuityBeep = true;
                }
                else if (directPart is { } rcp && rcp.Kind == ComponentKind.Resistor && ResistorColorCode.TryParseOhms(rcp.Value, out double rOhms) && rOhms < 10.0)
                {
                    AvometerDisplayValue = $"{rOhms,6:0.00}";
                    AvometerStatus = "CONTINUITY BEEP (< 10 Ω)";
                    AvometerBarPercent = 2.0;
                    AvometerSecondaryReading = $"LOW RESISTANCE: {rOhms:0.00} Ω";
                    IsContinuityBeep = true;
                }
                else
                {
                    AvometerDisplayValue = "   O.L";
                    AvometerStatus = "OPEN (NO CONTINUITY)";
                    AvometerBarPercent = 100.0;
                    AvometerSecondaryReading = "THRESHOLD: < 10 Ω";
                    IsContinuityBeep = false;
                }
                break;

            case "I_MA":
                AvometerUnit = "mA DC";
                if ((isBlackGnd && isRedVcc) || (isBlackVcc && isRedGnd))
                {
                    if (!railsEnergized)
                    {
                        AvometerDisplayValue = "  0.00";
                        AvometerStatus = "NO CURRENT (CIRCUIT UNPOWERED)";
                        AvometerBarPercent = 0.0;
                        AvometerSecondaryReading = "0.00 mA DC · No active power source";
                    }
                    else
                    {
                        int partCount = _draft.Parts.Count;
                        double currentMa = Math.Max(12.5, partCount * 8.4);
                        AvometerDisplayValue = $"{currentMa,6:0.00}";
                        AvometerStatus = "CIRCUIT LOAD CURRENT";
                        AvometerBarPercent = Math.Clamp(currentMa / 50.0 * 100.0, 5.0, 95.0);
                        AvometerSecondaryReading = $"I_LOAD = {currentMa:0.00} mA";
                    }
                }
                else
                {
                    AvometerDisplayValue = "  0.00";
                    AvometerStatus = "SERIES CURRENT LOOP OPEN";
                    AvometerBarPercent = 0.0;
                    AvometerSecondaryReading = "Connect in series with circuit";
                }
                IsContinuityBeep = false;
                break;
        }
    }

    private void FormatResistance(double ohms)
    {
        if (ohms < 1000.0)
        {
            AvometerDisplayValue = $"{ohms,6:0.0}";
            AvometerUnit = "Ω";
            AvometerBarPercent = Math.Clamp(ohms / 1000.0 * 33.0, 1.0, 33.0);
        }
        else if (ohms < 1_000_000.0)
        {
            AvometerDisplayValue = $"{ohms / 1000.0,6:0.00}";
            AvometerUnit = "kΩ";
            AvometerBarPercent = Math.Clamp(33.0 + (ohms / 100000.0) * 33.0, 34.0, 66.0);
        }
        else
        {
            AvometerDisplayValue = $"{ohms / 1_000_000.0,6:0.00}";
            AvometerUnit = "MΩ";
            AvometerBarPercent = Math.Clamp(66.0 + (ohms / 10_000_000.0) * 33.0, 67.0, 99.0);
        }
    }

    private void UpdateResistorBandPreview()
    {
        Span<int> bands = stackalloc int[4];
        if (ResistorColorCode.TryDecodeBands(NewPartValue, bands))
        {
            int b0 = Math.Clamp(bands[0], 0, ResistorColorCode.BandHexColors.Length - 1);
            int b1 = Math.Clamp(bands[1], 0, ResistorColorCode.BandHexColors.Length - 1);
            int b2 = Math.Clamp(bands[2], 0, ResistorColorCode.BandHexColors.Length - 1);
            int b3 = Math.Clamp(bands[3], 0, ResistorColorCode.BandHexColors.Length - 1);

            ResistorBand1Color = ResistorColorCode.BandHexColors[b0];
            ResistorBand2Color = ResistorColorCode.BandHexColors[b1];
            ResistorBand3Color = ResistorColorCode.BandHexColors[b2];
            ResistorBand4Color = ResistorColorCode.BandHexColors[b3];
            ResistorBandDescription = $"{NewPartValue} Ω  ({ResistorColorCode.BandColorNames[b0]} · {ResistorColorCode.BandColorNames[b1]} · {ResistorColorCode.BandColorNames[b2]} · {ResistorColorCode.BandColorNames[b3]})";
        }
        else
        {
            ResistorBand1Color = ResistorColorCode.BandHexColors[2];
            ResistorBand2Color = ResistorColorCode.BandHexColors[2];
            ResistorBand3Color = ResistorColorCode.BandHexColors[1];
            ResistorBand4Color = ResistorColorCode.BandHexColors[10];
            ResistorBandDescription = "220 Ω  (Default: Red · Red · Brown · Gold ±5%)";
        }
    }

    partial void OnSelectedDraftPartChanged(DraftPartRow? value)
    {
        _renderer.SelectedTrackId = value?.Part.TrackId;
        _isSyncingPartValue = true;
        try
        {
            EditPartValue = value?.Part.Value ?? string.Empty;
            EditPartSymbol = value?.Part.SymbolId ?? string.Empty;

            if (value is not null)
            {
                foreach (var opt in BuildPartOptions)
                {
                    if (opt.Kind == value.Part.Kind)
                    {
                        SelectedPartOption = opt;
                        break;
                    }
                }

                NewPartValue = value.Part.Value ?? string.Empty;
                if (value.Part.Kind == ComponentKind.Resistor)
                {
                    UpdateResistorBandPreview();
                }
            }
        }
        finally
        {
            _isSyncingPartValue = false;
        }
    }

    partial void OnEditPartValueChanged(string value)
    {
        if (_isSyncingPartValue || _disposed)
            return;

        if (SelectedDraftPart is { } row)
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(trimmed) && !string.Equals(row.Part.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                _draft.TrySetValue(row.Part.TrackId, trimmed);
                CommitDraft(row.Part.TrackId);
                if (row.Part.Kind == ComponentKind.Resistor)
                {
                    _isSyncingPartValue = true;
                    try
                    {
                        NewPartValue = trimmed;
                        UpdateResistorBandPreview();
                    }
                    finally
                    {
                        _isSyncingPartValue = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Build mode disables the demo injector and hands the DRC the draft's
    /// detections. Turning it off restores the camera/YOLO path untouched.
    /// </summary>
    partial void OnIsBuildModeChanged(bool value)
    {
        if (value)
        {
            _demoWasEnabledBeforeBuild = DemoModeEnabled;
            ShowAllStrips = true;

            // The injector and the draft both feed the checker, so only one may win.
            DemoModeEnabled = false;
            _coordinator?.SetManualCircuitEnabled(true);
            BuilderHint = _draft.Count == 0
                ? "Build mode ON: click a tie point for the first lead."
                : $"Build mode ON: the DRC is judging {_draft.Count} hand-placed part(s).";
        }
        else
        {
            ClearPendingBuild();
            _coordinator?.SetManualCircuitEnabled(false);

            // Restore whatever the operator was watching before the draft took over.
            if (_demoWasEnabledBeforeBuild)
                DemoModeEnabled = true;

            BuilderHint = _demoWasEnabledBeforeBuild
                ? "Build mode OFF: demo detections are back on the board."
                : "Build mode OFF: the DRC follows the camera / YOLO path again.";
        }

        CommitDraft();
    }

    partial void OnIsVoiceMutedChanged(bool value)
    {
        _coordinator?.SetVoiceMuted(value);
        OnPropertyChanged(nameof(IsVoiceEnabled));
    }

    partial void OnIsOpenCvEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsOpenCvEnabled = value;
    }

    partial void OnIsYoloEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsYoloEnabled = value;
    }

    partial void OnIsHandGateEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsHandGateEnabled = value;
    }

    partial void OnIsDrcEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsDrcEnabled = value;
    }

    partial void OnIsAlarmEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsAlarmEnabled = value;
    }

    partial void OnIsCognitiveTutorEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsCognitiveTutorEnabled = value;
    }

    partial void OnIsRuleBasedAdviceEnabledChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.IsRuleBasedAdviceEnabled = value;
    }

    partial void OnUseColabAiChanged(bool value)
    {
        if (value)
        {
            _remoteTimer.Start();

            // A specific endpoint was just applied (pair code / manual URL / local
            // provider); polling Drive here would overwrite that choice.
            if (_appliedRemoteTarget is null)
                _ = PollColabAsync(force: true);
        }
        else
        {
            _remoteTimer.Stop();
            _appliedRemoteTarget = null;
            _coordinator?.SetRemoteTutor(null);
            SelectedAiRoute = AiRouteMethod.LocalQwen;
            RemoteRouteLabel = "TUTOR ROUTE: LOCAL";
            RemoteAiStatus = "Remote AI disabled. Local Qwen is active.";
        }
    }

    // ---- telemetry pump ----------------------------------------------------

    private void RefreshTelemetry()
    {
        if (_disposed)
            return;

        FlushTranscript();

        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
            return;

        FrameTelemetry = string.Create(
            CultureInfo.InvariantCulture,
            $"FRAME {coordinator.FrameCount}  ·  YOLO {coordinator.LastYoloInferenceMs:0.0} MS  ·  DRC {coordinator.LastDrcComputeMs:0.000} MS");

        VisionBackend = $"VISION: {coordinator.VisionBackendDescription}";
        TutorBackend = $"TUTOR: {coordinator.TutorBackendDescription}";
        VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";
        VoiceSetupNote = coordinator.VoiceNote;
        VramTelemetry = $"VRAM: {coordinator.VramStatus}";
        AffinityTelemetry = $"AFFINITY: {coordinator.AffinityStatus}";
        TargetNetlistStatus = coordinator.TargetSchematicStatus;
        CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
        IsTutorBusy = coordinator.IsTutorBusy;

        RemoteRouteLabel = coordinator.UsingRemoteTutor
            ? (_appliedRemoteTarget is null ? "TUTOR ROUTE: REMOTE" : DescribeRoute(_appliedRemoteTarget))
            : "TUTOR ROUTE: LOCAL";

        UpdateComputeAllocationTelemetry(coordinator);
    }

    private void UpdateComputeAllocationTelemetry(InspectionPipelineCoordinator coordinator)
    {
        CpuAffinityManager.ReportProcessor(AffinityRole.DrcUi);
        var plan = CpuAffinityManager.Plan;
        CpuPlanHeadline = plan.IsTargetWorkstation
            ? $"CPU · TARGET HYBRID · {plan.LogicalProcessorCount} LOGICAL PROCESSORS"
            : $"CPU · PORTABLE ARCHITECTURE · {plan.LogicalProcessorCount} LOGICAL PROCESSORS";

        var aiStatus = CpuAffinityManager.GetRoleStatus(AffinityRole.Ai);
        var visStatus = CpuAffinityManager.GetRoleStatus(AffinityRole.Vision);
        var uiStatus = CpuAffinityManager.GetRoleStatus(AffinityRole.DrcUi);

        string aiCores = CpuAffinityManager.DescribeMaskCores(plan.AiMask);
        string visCores = CpuAffinityManager.DescribeMaskCores(plan.VisionMask);
        string uiCores = CpuAffinityManager.DescribeMaskCores(plan.DrcUiMask);

        var rows = new[]
        {
            new ComputeAllocationRow(
                Role: "AI",
                Cores: aiCores,
                Work: "AI TUTOR + NEURAL TTS",
                Mask: $"0x{plan.AiMask:X}",
                State: aiStatus.Pinned ? "PINNED" : (aiStatus.Win32Error != 0 ? "FAILED" : "ACTIVE"),
                IsPinned: aiStatus.Pinned,
                CurrentCore: aiStatus.LastProcessorIndex >= 0 ? $"CORE {aiStatus.LastProcessorIndex}" : "--"),

            new ComputeAllocationRow(
                Role: "VISION",
                Cores: visCores,
                Work: "60 FPS CAM + HOMOGRAPHY + YOLO",
                Mask: $"0x{plan.VisionMask:X}",
                State: visStatus.Pinned ? "PINNED" : (visStatus.Win32Error != 0 ? "FAILED" : "ACTIVE"),
                IsPinned: visStatus.Pinned,
                CurrentCore: visStatus.LastProcessorIndex >= 0 ? $"CORE {visStatus.LastProcessorIndex}" : "--"),

            new ComputeAllocationRow(
                Role: "DRC/UI",
                Cores: uiCores,
                Work: "KiCad DRC + SKIASHARP CAD UI",
                Mask: $"0x{plan.DrcUiMask:X}",
                State: uiStatus.Pinned ? "PINNED" : (uiStatus.Win32Error != 0 ? "FAILED" : "ACTIVE"),
                IsPinned: uiStatus.Pinned,
                CurrentCore: uiStatus.LastProcessorIndex >= 0 ? $"CORE {uiStatus.LastProcessorIndex}" : "--")
        };

        if (ComputeAllocation.Count != rows.Length)
        {
            ComputeAllocation.Clear();
            foreach (var r in rows)
                ComputeAllocation.Add(r);
        }
        else
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (ComputeAllocation[i] != rows[i])
                    ComputeAllocation[i] = rows[i];
            }
        }

        string gpuName = coordinator.GpuDeviceName;
        VramSnapshot vram = coordinator.GetVramSnapshot();
        string gpuTotal = vram.IsRealGpu
            ? string.Create(CultureInfo.InvariantCulture, $"{vram.TotalGigabytes:0.#} GB")
            : "VRAM UNKNOWN";
        GpuPlanHeadline = $"GPU · {gpuName} · {gpuTotal}";

        string tutorLine = (_coordinator?.UsingRemoteTutor ?? false)
            ? "Tutor model (remote)         0 MB  [NO LOCAL WEIGHTS]"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Tutor model (llama-server) {VramBudgetMonitor.Rtx3060QwenMoEBudgetBytes / VramBudgetMonitor.Megabyte,4:N0} MB  [LOCAL MoE PROFILE]");

        GpuAllocationText =
            "YOLOv11s-Pose (FP16)      450 MB  [TensorRT / CUDA]\n" +
            tutorLine + "\n" +
            "SkiaSharp SwapChain       350 MB  [DirectX / OpenGL]\n" +
            "Workstation OS Reserve    600 MB  [Display Buffer]\n" +
            $"Live Telemetry:          {coordinator.VramStatus}";

        HardwareAllocationSummary =
            $"CPU: AI {aiCores} · VIS {visCores} · UI {uiCores}  |  GPU: {coordinator.VramStatus}";
    }

    /// <summary>
    /// Polls the active Qwen endpoint so the command-bar light is green only when
    /// the tutor is genuinely reachable. A failed probe never interrupts the
    /// workstation: the light simply turns gray until the next successful check.
    /// </summary>
    private async Task RefreshQwenConnectionAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _qwenProbeBusy, 1) != 0)
            return;

        try
        {
            QwenMoEService? qwen = _coordinator?.QwenService;
            if (qwen is null)
            {
                IsQwenConnected = false;
                QwenConnectionLabel = "TUTOR OFF";
                return;
            }

            bool connected;
            try
            {
                connected = await qwen.ProbeConnectionAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                connected = false;
            }

            if (_disposed)
                return;

            IsQwenConnected = connected;
            QwenConnectionLabel = connected ? "QWEN ONLINE" : "QWEN OFFLINE";
        }
        finally
        {
            Volatile.Write(ref _qwenProbeBusy, 0);
        }
    }

    private void FlushTranscript()
    {
        string snapshot;

        lock (_transcriptGate)
        {
            if (!_transcriptDirty)
                return;

            _transcriptDirty = false;
            snapshot = _transcriptBuffer.ToString();
        }

        TutorTranscript = snapshot;
    }

    /// <summary>Accepts 1/true/on so kiosk and CI launches can boot silently.</summary>
    private static bool IsEnvironmentFlagSet(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value is "1"
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _layoutSaveTimer.Stop();
        SaveWorkspaceLayout();

        _disposed = true;
        _telemetryTimer.Stop();
        _remoteTimer.Stop();
        _qwenProbeTimer.Stop();
        _renderer.HoverChanged -= OnRendererHoverChanged;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        _coordinator = null;

        if (coordinator is not null)
        {
            coordinator.ReflexDrcUpdated -= OnReflexDrcUpdated;
            coordinator.TutorTurnStarted -= OnTutorTurnStarted;
            coordinator.TutorTranscriptTokenReceived -= OnTutorToken;
            coordinator.TutorDiagnosticCompleted -= OnTutorCompleted;
            coordinator.StatusChanged -= OnStatusChanged;
            coordinator.DemoScenarioChanged -= OnDemoScenarioChanged;
            coordinator.EmergencyAlarmRaised -= OnEmergencyAlarmRaised;
            coordinator.TutorTurnMeasured -= OnTutorTurnMeasured;

            try
            {
                coordinator.StopAsync().Wait(1500);
            }
            catch (AggregateException)
            {
                // Shutdown race: the coordinator disposes what it still owns.
            }

            coordinator.Dispose();
        }

        _aiVisionWindow?.Close();
        _aiVisionWindow = null;

        _renderer.Dispose();

        _oauth?.Dispose();
        _drive.Dispose();
        _probeHttp.Dispose();
    }
}
