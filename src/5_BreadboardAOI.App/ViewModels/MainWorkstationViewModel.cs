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

    public ViolationCode Code { get; }

    public DrcSeverity Severity { get; }

    public string ComponentId { get; }

    public int OffendingStripId { get; }

    public string BadHole { get; }

    public string TargetHole { get; }

    public string Message { get; }

    public string CodeLabel { get; }

    public string SeverityLabel { get; }

    public bool IsCritical => Severity == DrcSeverity.Critical;

    public bool HasCorrection => TargetHole != "--";
}

/// <summary>Workstation view-model: owns the pipeline lifecycle and all dock state.</summary>
public sealed partial class MainWorkstationViewModel : ObservableObject, IDisposable
{
    private readonly SkiaArOverlayRenderer _renderer = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _remoteTimer;
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly object _transcriptGate = new();
    private readonly StringBuilder _transcriptBuffer = new(4096);
    private readonly List<ViolationRow> _violationScratch = new(16);

    // X-ray builder: the draft owns the parts, the coordinator only sees snapshots.
    private readonly CircuitDraft _draft = new(PinMatrixLookup.Shared);
    private HoleCoord _pendingBuildHole;
    private bool _hasPendingBuildHole;
    private bool _demoWasEnabledBeforeBuild;

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

    private static readonly PartValuePreset[] ResistorPresets =
    [
        new("220R  (RED-RED-BROWN)", "220R", "Device/R"),
        new("330R  (LED LIMITER)", "330R", "Device/R"),
        new("1K", "1K", "Device/R"),
        new("4K7", "4K7", "Device/R"),
        new("10K  (PULL-UP)", "10K", "Device/R"),
        new("100K", "100K", "Device/R")
    ];

    private static readonly PartValuePreset[] CapacitorPresets =
    [
        new("100nF  CERAMIC", "100nF", "Device/C"),
        new("1uF  CERAMIC", "1uF", "Device/C"),
        new("10uF  ELECTROLYTIC", "10uF", "Device/C_Polarized"),
        new("100uF  ELECTROLYTIC", "100uF", "Device/C_Polarized")
    ];

    private static readonly PartValuePreset[] LedPresets =
    [
        new("5MM RED  (2.0 V)", "RED 2V", "Device/LED"),
        new("5MM GREEN  (2.1 V)", "GREEN 2.1V", "Device/LED"),
        new("5MM BLUE  (3.2 V)", "BLUE 3.2V", "Device/LED")
    ];

    private static readonly PartValuePreset[] IcPresets =
    [
        new("NE555P  TIMER  DIP-8", "NE555P", "Timer/NE555P", 8),
        new("LM358  OP-AMP  DIP-8", "LM358", "Amplifier_Operational/LM358", 8),
        new("74LS00  QUAD NAND  DIP-14", "74LS00", "74xx/74LS00", 14),
        new("CD4017  DECADE COUNTER  DIP-16", "CD4017BE", "4xxx/4017", 16),
        new("ATmega328P  MCU  DIP-28", "ATmega328P", "MCU_Microchip_ATmega/ATmega328P-P", 28)
    ];

    private static readonly PartValuePreset[] ButtonPresets =
    [
        new("6MM TACT SWITCH", "TACT 6MM", "Switch/SW_Push"),
        new("SPST SLIDE SWITCH", "SPST", "Switch/SW_SPST")
    ];

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
            new CameraOption(CameraSource.Camera0, "CAMERA 0 / USB"),
            new CameraOption(CameraSource.Camera1, "CAMERA 1 / USB")
        });

        Violations = new ObservableCollection<ViolationRow>();
        DraftParts = new ObservableCollection<DraftPartRow>();

        BuildPartOptions = new ReadOnlyCollection<BuildPartOption>(new[]
        {
            new BuildPartOption(ComponentKind.JumperWire, "JUMPER", "Plain wire between two tie points"),
            new BuildPartOption(ComponentKind.Resistor, "RESISTOR", "220R current limiter / 10K pull-up"),
            new BuildPartOption(ComponentKind.Led, "LED", "5 mm indicator, anode to the first hole"),
            new BuildPartOption(ComponentKind.Capacitor, "CAPACITOR", "10 uF timing or decoupling cap"),
            new BuildPartOption(ComponentKind.IntegratedCircuit, "IC", "NE555P timer across the trench"),
            new BuildPartOption(ComponentKind.PushButton, "BUTTON", "6 mm tact switch")
        });

        PinCountOptions = new ReadOnlyCollection<PinCountOption>(new[]
        {
            new PinCountOption(2, "2-LEAD (R, C, LED)"),
            new PinCountOption(3, "3-PIN ROW (TO-92 / 7805)"),
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
    private bool _speakRuleBasedAdvice = true;

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
    /// workstation fell back (missing Piper model, missing Kokoro files, ...).
    /// </summary>
    [ObservableProperty]
    private string _voiceSetupNote = string.Empty;

    [ObservableProperty]
    private string _vramTelemetry = "VRAM: --";

    [ObservableProperty]
    private string _affinityTelemetry = "AFFINITY: --";

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
            coordinator = new InspectionPipelineCoordinator(new InspectionPipelineOptions
            {
                DemoModeEnabled = true,
                InitialDemoScenario = SelectedScenario?.Scenario ?? DemoScenario.SameStripLedShort,
                CameraSource = _pendingImagePath is not null ? CameraSource.ImageFile : SelectedCamera?.Source ?? CameraSource.Synthetic,
                ImagePath = _pendingImagePath,
                EnableCognitiveTutor = true,
                EnableVoice = true,
                VoicePreference = VoiceSettingsStore.Load(),
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
        coordinator.SpeakRuleBasedAdvice = SpeakRuleBasedAdvice;
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
        if (_pendingImagePath is not null)
            LoadedImagePath = _pendingImagePath;

        _telemetryTimer.Start();
        RefreshTelemetry();

        // The vector board is the CAD drawing; the photo underlay is only helpful
        // when a real camera or a still image is the frame source.
        ShowCameraUnderlay = coordinator.ActiveCameraSource != CameraSource.Synthetic;

        if (_savedEndpoint is not null)
        {
            RemoteTutorTarget saved = _savedEndpoint;
            _savedEndpoint = null;
            SelectedAiRoute = saved.IsLoopback ? AiRouteMethod.LocalProvider : AiRouteMethod.ColabTunnel;
            _ = ConnectEndpointAsync(saved, persist: false);
        }
        else if (UseColabAi)
        {
            _ = PollColabAsync(force: true);
        }

        DrcStatePacket? packet = coordinator.LatestPacket;
        if (packet is not null)
            OnReflexDrcUpdated(packet);
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

    // ---- X-ray builder interactions ---------------------------------------

    /// <summary>
    /// Left click places the two leads of the selected part; right click removes
    /// the last part touching that tie point. Snapping is delegated to the CAD
    /// renderer so a click and a hover always agree on the same hole.
    /// </summary>
    public void HandleViewportClick(double viewportX, double viewportY, bool secondary)
    {
        if (_disposed || !IsBuildMode)
            return;

        UpdateHover(viewportX, viewportY);

        if (_renderer.HoveredPin is not { } pin)
        {
            BuilderHint = "Aim at a tie point inside the board; the click missed the 830-point matrix.";
            return;
        }

        HoleCoord hole = pin.Hole;

        if (secondary)
        {
            if (_draft.RemoveLastTouching(hole.HoleId))
            {
                ClearPendingBuild();
                CommitDraft();
                BuilderHint = $"Removed the last part touching {hole.HoleId}.";
            }
            else
            {
                BuilderHint = $"No placed part touches {hole.HoleId}.";
            }

            return;
        }

        if (SelectedPartOption is null)
        {
            BuilderHint = "Choose a part kind in the BUILDER dock first.";
            return;
        }

        int footprintPins = SelectedPinCount?.Count ?? 2;

        // A DIP or a 3-pin module is placed whole from its first pin: waiting for
        // a second click would leave the operator guessing which holes it takes.
        if (SelectedPartOption.Kind == ComponentKind.IntegratedCircuit && footprintPins >= 3)
        {
            TryPlaceFootprint(SelectedPartOption.Kind, hole.HoleId, footprintPins);
            return;
        }

        if (!_hasPendingBuildHole)
        {
            _pendingBuildHole = hole;
            _hasPendingBuildHole = true;
            _renderer.SetPendingLead(hole);
            BuildPin1Text = hole.HoleId;
            BuilderHint = $"Lead 1 = {hole.HoleId}. Click or type the second tie point.";
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

        if (pinCount == 3)
        {
            if (!FootprintPlacer.TryPlaceSingleRow(_draft.Matrix, anchorHoleId, pinCount, out pins, out error))
            {
                BuilderHint = error ?? "A 3-pin part needs three adjacent columns.";
                return false;
            }
        }
        else if (pinCount >= 4)
        {
            if (!FootprintPlacer.TryPlaceDualRow(_draft.Matrix, anchorHoleId, pinCount, out pins, out error))
            {
                BuilderHint = error ?? "That DIP does not fit there.";
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

        if (!_draft.TryAddFootprint(kind, pins, value, symbol, out DraftPart part, out error))
        {
            BuilderHint = error ?? "That part could not be placed.";
            return false;
        }

        ClearPendingBuild();
        BuildPin1Text = string.Empty;
        BuildPin2Text = string.Empty;
        CommitDraft();

        BuilderHint = pinCount == 3
            ? $"Placed {part.KindLabel} #{part.TrackId}: pins {string.Join('/', pins)} ({value})."
            : $"Placed {part.KindLabel} #{part.TrackId}: {pinCount} pins across the trench, {pins[0]} to {pins[^1]} ({value}).";

        return true;
    }

    /// <summary>Places a part from the typed lead labels. Returns false with a hint on error.</summary>
    public bool TryPlacePart(ComponentKind kind, string? pin1HoleId, string? pin2HoleId)
    {
        if (!_draft.TryAdd(
                kind,
                pin1HoleId,
                pin2HoleId,
                ResolveValue(kind, 2),
                out DraftPart part,
                out string? error))
        {
            BuilderHint = error ?? "That part could not be placed.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(NewPartSymbol))
            _draft.TrySetSymbol(part.TrackId, NewPartSymbol);

        ClearPendingBuild();
        BuildPin1Text = string.Empty;
        BuildPin2Text = string.Empty;
        CommitDraft();
        BuilderHint = $"Placed {part.KindLabel} #{part.TrackId}: {part.Pin1HoleId} \u2192 {part.Pin2HoleId}.";
        return true;
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
    private void UndoCircuitPart()
    {
        if (!_draft.RemoveLast())
        {
            BuilderHint = "Nothing to undo; the board is empty.";
            return;
        }

        ClearPendingBuild();
        CommitDraft();
        BuilderHint = "Removed the last placed part.";
    }

    [RelayCommand]
    private void ClearCircuit()
    {
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

        int trackId = row.Part.TrackId;
        string value = EditPartValue?.Trim() ?? string.Empty;
        string symbol = EditPartSymbol?.Trim() ?? string.Empty;

        if (!_draft.TrySetValue(trackId, value) || !_draft.TrySetSymbol(trackId, symbol))
        {
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

    private void CommitDraft()
    {
        int? selectedTrack = SelectedDraftPart?.Part.TrackId;

        DraftParts.Clear();
        for (int i = 0; i < _draft.Parts.Count; i++)
            DraftParts.Add(new DraftPartRow(_draft.Parts[i]));

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

        CircuitSummary = _draft.Describe();
        _coordinator?.SetManualDetections(_draft.BuildDetections());
        RefreshNetlist(_coordinator?.LatestPacket);
    }

    private void ClearPendingBuild()
    {
        _hasPendingBuildHole = false;
        _pendingBuildHole = default;
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

    /// <summary>
    /// Switches the live tutor voice. The view-model persists the choice, but
    /// BREADBOARD_VOICE_BACKEND still wins on the next launch.
    /// </summary>
    [RelayCommand]
    private async Task SelectVoiceEngineAsync(string? preference)
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "VOICE: the pipeline is not ready yet.";
            return;
        }

        if (!VoiceEnginePreferences.TryParse(preference, out VoiceEnginePreference requested))
            return;

        IsVoiceEngineBusy = true;
        try
        {
            StatusLine = $"VOICE: switching to {VoiceEnginePreferences.ToDisplayName(requested)}...";
            bool switched = await coordinator.SetVoiceBackendAsync(requested);

            VoiceSettingsStore.Save(requested);
            VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";

            BreadboardAOI.Intelligence.VoiceBackend active = coordinator.ActiveVoiceBackend;
            bool fellBack = requested switch
            {
                VoiceEnginePreference.Piper => active != BreadboardAOI.Intelligence.VoiceBackend.Piper,
                VoiceEnginePreference.Kokoro => active != BreadboardAOI.Intelligence.VoiceBackend.Kokoro,
                VoiceEnginePreference.Sapi => active != BreadboardAOI.Intelligence.VoiceBackend.Sapi,
                _ => false
            };

            StatusLine = switched
                ? fellBack
                    ? $"VOICE: {coordinator.VoiceBackendDescription} active (fallback)."
                    : $"VOICE: {coordinator.VoiceBackendDescription} active."
                : $"VOICE: no engine could speak. {coordinator.VoiceNote}".TrimEnd();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"VOICE: switch failed - {ex.Message}";
        }
        finally
        {
            IsVoiceEngineBusy = false;
        }
    }

    /// <summary>Auditions the active engine with a short natural line.</summary>
    [RelayCommand]
    private async Task TestVoiceAsync()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "VOICE: the pipeline is not ready yet.";
            return;
        }

        IsVoiceEngineBusy = true;
        try
        {
            StatusLine = $"VOICE: speaking with {coordinator.VoiceBackendDescription}...";
            bool spoken = await coordinator.SpeakTestLineAsync();

            VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";
            StatusLine = spoken
                ? $"VOICE: test line played by {coordinator.VoiceBackendDescription}."
                : $"VOICE: test failed. {coordinator.VoiceNote}".TrimEnd();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"VOICE: test failed - {ex.Message}";
        }
        finally
        {
            IsVoiceEngineBusy = false;
        }
    }

    [RelayCommand]
    private void CycleCamera()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
            return;

        CameraSource current = coordinator.ActiveCameraSource;
        CameraSource next = current switch
        {
            CameraSource.Synthetic => CameraSource.Camera0,
            CameraSource.Camera0 => CameraSource.Camera1,
            _ => CameraSource.Synthetic
        };

        for (int i = 0; i < CameraOptions.Count; i++)
        {
            if (CameraOptions[i].Source != next)
                continue;

            SelectedCamera = CameraOptions[i];
            break;
        }

        // The synthetic frame exists only to exercise homography; its ArUco corner
        // markers would read as grey blocks under the vector board.
        ShowCameraUnderlay = next != CameraSource.Synthetic;
    }

    /// <summary>
    /// Loads a still photo and replays it as the camera frame source. Used by the
    /// toolbar dialog, viewport drag &amp; drop and <c>BREADBOARD_IMAGE_PATH</c>.
    /// </summary>
    public void LoadImage(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return;

        LoadedImagePath = path;
        _pendingImagePath = path;
        ShowCameraUnderlay = true;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is not null)
        {
            coordinator.SetImageSource(path);
            CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
            StatusLine = $"Still image loaded: {Path.GetFileName(path)} (photo underlay, no part extraction).";
            return;
        }

        StatusLine = $"Still image queued: {Path.GetFileName(path)} (photo underlay, no part extraction).";
    }

    [RelayCommand]
    private void LoadImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open breadboard photo",
            Filter = "Breadboard images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            LoadImage(dialog.FileName);
    }

    // ---- remote AI / colab commands ---------------------------------------

    /// <summary>Public GitHub-hosted notebook the user opens in their own Colab.</summary>
    public string NotebookUrl =>
        "https://colab.research.google.com/github/wallali77666-cyber/BreadBoard-Assistant/blob/main/assets/colab/BreadboardAOI_Qwen.ipynb";

    [RelayCommand]
    private async Task LinkGoogleAsync()
    {
        if (IsRemoteBusy)
            return;

        string clientId = GoogleClientId.Trim();
        if (clientId.Length == 0)
            clientId = GoogleOAuthOptions.FromEnvironment().ClientId;

        if (clientId.Length == 0)
        {
            RemoteAiStatus = "Paste the OAuth client ID (Desktop app) from Google Cloud Console, then press LINK GOOGLE again.";
            return;
        }

        IsRemoteBusy = true;
        try
        {
            string? secret = NullIfBlank(GoogleClientSecret);
            _clientStore.Save(new GoogleClientConfig(clientId, secret));

            _oauth?.Dispose();
            _oauth = new GoogleOAuthClient(
                GoogleOAuthOptions.FromEnvironment().WithClient(clientId, secret));

            RemoteAiStatus = "Opening the Google consent page in your browser...";

            GoogleTokenSet tokens = await _oauth.AuthorizeAsync(url =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    RemoteAiStatus = "Open this URL manually: " + url;
                }

                return Task.CompletedTask;
            });

            _googleTokens = tokens;
            _tokenStore.Save(tokens);
            GoogleAccountStatus = "LINKED · " + tokens.DescribeAccount();
            RemoteAiStatus = $"Signed in as {tokens.DescribeAccount()}. Press PUBLISH SESSION to hand a key to Colab.";
            _remoteTimer.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Link failed: " + ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlinkGoogleAsync()
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            GoogleTokenSet? tokens = _googleTokens;
            GoogleOAuthClient? oauth = _oauth;

            if (tokens is not null && oauth is not null)
                await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Revocation is best-effort; the local token is cleared regardless.
        }
        finally
        {
            IsRemoteBusy = false;
        }

        _googleTokens = null;
        _colabSession = null;
        _appliedRemoteTarget = null;
        _tokenStore.Clear();
        _remoteTimer.Stop();
        UseColabAi = false;
        _coordinator?.SetRemoteTutor(null);
        SelectedAiRoute = AiRouteMethod.LocalQwen;
        GoogleAccountStatus = "NOT LINKED";
        RemoteAiStatus = "Google account unlinked. Local Qwen remains active.";
    }

    [RelayCommand]
    private async Task PublishColabSessionAsync()
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            string? accessToken = await EnsureAccessTokenAsync(CancellationToken.None);
            if (accessToken is null)
            {
                RemoteAiStatus = "Link a Google account first; the session key is published through Drive.";
                return;
            }

            ColabSession session = await _drive.RequestSessionAsync(accessToken, CreateSessionKey(), NotebookUrl);
            _colabSession = session;
            UseColabAi = true;
            _remoteTimer.Start();
            RemoteAiStatus = "Session key published to Drive. Open the Colab notebook in the same account and press Run all.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Publish failed: " + ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private void OpenColabNotebook()
    {
        try
        {
            Process.Start(new ProcessStartInfo(NotebookUrl) { UseShellExecute = true })?.Dispose();
            RemoteAiStatus = "Opened the BreadboardAOI Colab notebook in your browser.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RemoteAiStatus = "Could not open the browser. Notebook URL: " + NotebookUrl;
        }
    }

    [RelayCommand]
    private async Task RefreshColabAsync()
    {
        await PollColabAsync(force: true);
    }

    [RelayCommand]
    private async Task ApplyManualEndpointAsync()
    {
        SelectedAiRoute = AiRouteMethod.ManualEndpoint;

        string? endpoint = ColabSession.NormalizeBaseUrl(ManualEndpoint);
        if (endpoint is null)
        {
            RemoteAiStatus = "Enter a tunnel URL such as https://random-words.trycloudflare.com or a local server such as http://127.0.0.1:1234.";
            return;
        }

        if (!ColabEndpoints.IsSecureEndpoint(endpoint))
        {
            RemoteAiStatus =
                "Refused: a remote tutor URL must use https so frames are not sent in cleartext. " +
                "Plain http is only accepted for 127.0.0.1 / localhost.";
            return;
        }

        if (!ColabEndpoints.IsPublicHost(endpoint))
        {
            RemoteAiStatus =
                "Refused: that URL points at a private or link-local address. Use the public https tunnel " +
                "printed by Colab, or 127.0.0.1 for a provider on this PC.";
            return;
        }

        string? key = NullIfBlank(ManualApiKey);

        // Model discovery happens inside ConnectResolvedAsync for every loopback
        // target, so the preset buttons, pair codes and this URL share one path.
        await ConnectEndpointAsync(
            new RemoteTutorTarget(endpoint, key, null),
            persist: true,
            confirmCustomHost: true).ConfigureAwait(true);
    }

    /// <summary>Connects from the single line printed by the Colab notebook.</summary>
    [RelayCommand]
    private async Task ConnectPairCodeAsync()
    {
        if (IsRemoteBusy)
            return;

        SelectedAiRoute = AiRouteMethod.ColabTunnel;

        if (!ColabPairCode.TryParse(PairCode, out RemoteTutorTarget target, out string? rejection))
        {
            RemoteAiStatus = rejection
                ?? "Paste the whole line printed by Colab: BBAOI1|https://....trycloudflare.com|key";
            return;
        }

        await ConnectEndpointAsync(target, persist: true, confirmCustomHost: true).ConfigureAwait(true);
    }

    /// <summary>Returns the cognitive loop to llama-server on this PC.</summary>
    [RelayCommand]
    private void UseLocalTutor()
    {
        _savedEndpoint = null;
        _appliedRemoteTarget = null;
        _endpointStore.Clear();
        _remoteTimer.Stop();
        _coordinator?.SetRemoteTutor(null);

        UseColabAi = false;
        SelectedAiRoute = AiRouteMethod.LocalQwen;
        RemoteRouteLabel = "TUTOR ROUTE: LOCAL";
        RemoteAiStatus = "Local Qwen on this PC is active. Remote routing is off.";
    }

    /// <summary>Drops the pair code and returns the cognitive loop to this PC.</summary>
    [RelayCommand]
    private void DisconnectPairCode()
    {
        PairCode = string.Empty;
        UseLocalTutor();
        RemoteAiStatus = "Pair code cleared. Local Qwen is active again.";
    }

    /// <summary>Probes a target, then swaps the tutor and remembers it for the next start.</summary>
    private async Task ConnectEndpointAsync(RemoteTutorTarget target, bool persist, bool confirmCustomHost = false)
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            await ConnectResolvedAsync(target, persist, providerLabel: null, confirmCustomHost).ConfigureAwait(true);
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    /// <summary>
    /// Shared connect path. The caller owns <see cref="IsRemoteBusy"/> so a preset
    /// probe can reuse it without tripping the re-entrancy guard.
    /// </summary>
    private async Task ConnectResolvedAsync(
        RemoteTutorTarget target,
        bool persist,
        string? providerLabel,
        bool confirmCustomHost = false)
    {
        if (!ColabEndpoints.IsSecureEndpoint(target.EndpointUrl))
        {
            RemoteAiStatus =
                $"Refused {target.DisplayHost}: the tutor endpoint must use https (only loopback may use http).";
            return;
        }

        if (!ColabEndpoints.IsPublicHost(target.EndpointUrl))
        {
            RemoteAiStatus =
                $"Refused {target.DisplayHost}: the endpoint resolves to a private or link-local network.";
            return;
        }

        if (confirmCustomHost
            && !target.IsLoopback
            && !ColabEndpoints.IsKnownTunnelHost(target.EndpointUrl))
        {
            MessageBoxResult answer = MessageBox.Show(
                $"This will send camera crops and DRC reports to:\n\n{target.DisplayHost}\n\n" +
                "Only continue if you recognise and trust this domain.",
                "Custom tutor host",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                RemoteAiStatus = "Connection cancelled: the custom tutor host was not confirmed.";
                return;
            }
        }

        RemoteAiStatus = providerLabel is null
            ? $"Probing {target.DisplayHost}..."
            : $"Probing {providerLabel} on {target.DisplayHost}...";

        try
        {
            // A loopback server names its own models. Resolve the alias here so a
            // preset, a pair code and a typed URL all send a model the server has.
            if (target.IsLoopback)
            {
                IReadOnlyList<string> models = await LocalAiProviders
                    .ListModelsAsync(_probeHttp, target.EndpointUrl, target.ApiKey)
                    .ConfigureAwait(true);

                if (models.Count == 0)
                {
                    RemoteAiStatus =
                        $"No model answered on {target.EndpointUrl}. Start the provider, load a model, then connect again. Nothing was installed by this app.";
                    LocalProviderStatus = "LOCAL AI: NOT ANSWERING";
                    return;
                }

                string? resolved = LocalAiProviders.ChooseModel(models, target.ModelAlias);
                target = target with { ModelAlias = resolved };
                LocalProviderStatus = $"LOCAL AI: {target.DisplayHost} \u00b7 {resolved}";
            }

            RemoteProbeResult probe = await RemoteEndpointProbe
                .ProbeAsync(_probeHttp, target.EndpointUrl, target.ApiKey)
                .ConfigureAwait(true);

            if (!probe.IsHealthy)
            {
                RemoteAiStatus =
                    $"{providerLabel ?? "The tutor"} is not answering yet ({probe.Detail}). Start it, then connect again.";
                return;
            }

            // UseColabAi means "prefer the Colab tunnel"; a loopback provider must
            // not arm the Drive poller or it would replace the local model later.
            if (target.IsLoopback)
            {
                if (UseColabAi)
                    UseColabAi = false;

                ApplyRemoteTarget(target);
            }
            else
            {
                ApplyRemoteTarget(target);

                if (!UseColabAi)
                    UseColabAi = true;
            }

            if (persist)
            {
                _savedEndpoint = target;
                _endpointStore.Save(target);
                PairCode = ColabPairCode.Format(target);
            }

            RemoteAiStatus = providerLabel is null
                ? $"Connected to {target.DisplayHost} ({probe.LatencyMs:0} ms). Diagnoses stream from {(target.IsLoopback ? "the local model" : "Colab")}.{CustomHostNote(target)}"
                : $"{providerLabel} connected on {target.DisplayHost} ({probe.LatencyMs:0} ms){ModelSuffix(target)}.{CustomHostNote(target)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Connect failed: " + ex.Message;
        }
    }

    private static string ModelSuffix(RemoteTutorTarget target) =>
        string.IsNullOrWhiteSpace(target.ModelAlias) ? string.Empty : " · " + target.ModelAlias;

    /// <summary>
    /// A custom public domain is accepted because the operator pasted it, but the
    /// status line says so: only tunnel providers and loopback are recognised.
    /// </summary>
    private static string CustomHostNote(RemoteTutorTarget target) =>
        target.IsLoopback || ColabEndpoints.IsKnownTunnelHost(target.EndpointUrl)
            ? string.Empty
            : " Custom host: confirm this domain is yours before sending frames.";

    /// <summary>
    /// Probes LM Studio / Ollama that the operator already runs on this PC.
    /// Nothing is installed or launched: an offline provider just leaves the
    /// local llama.cpp / rule-based tutor in charge.
    /// </summary>
    [RelayCommand]
    private async Task ConnectLocalProviderAsync(string? presetId)
    {
        LocalAiPreset? preset = LocalAiProviders.Find(presetId);
        if (preset is null)
        {
            LocalProviderStatus = "LOCAL AI: UNKNOWN PROVIDER";
            return;
        }

        if (IsRemoteBusy)
            return;

        SelectedAiRoute = AiRouteMethod.LocalProvider;
        IsRemoteBusy = true;
        LocalProviderStatus = $"LOCAL AI: PROBING {preset.BaseUrl} ...";

        try
        {
            // ConnectResolvedAsync discovers the loaded model and reports a clear
            // "not answering" status when the provider is not running.
            await ConnectResolvedAsync(
                new RemoteTutorTarget(preset.BaseUrl, preset.ApiKey, null),
                persist: false,
                providerLabel: preset.Label).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LocalProviderStatus = $"LOCAL AI: {preset.Label} PROBE FAILED";
            RemoteAiStatus = "Local provider probe failed: " + ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    // ---- remote AI helpers -------------------------------------------------

    private void LoadGoogleSettings()
    {
        GoogleClientConfig? config = _clientStore.Load();
        GoogleOAuthOptions environment = GoogleOAuthOptions.FromEnvironment();

        GoogleClientId = !string.IsNullOrWhiteSpace(config?.ClientId) ? config!.ClientId : environment.ClientId;
        GoogleClientSecret = NullIfBlank(config?.ClientSecret) ?? NullIfBlank(environment.ClientSecret) ?? string.Empty;

        _googleTokens = _tokenStore.Load();
        if (_googleTokens is not null)
        {
            GoogleAccountStatus = "LINKED · " + _googleTokens.DescribeAccount();
            RemoteAiStatus = "Cached Google grant loaded. Press PUBLISH SESSION or refresh the Colab endpoint.";
        }

        string? manual = Environment.GetEnvironmentVariable("BREADBOARD_COLAB_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(manual))
        {
            ManualEndpoint = manual.Trim();
            ManualApiKey = Environment.GetEnvironmentVariable("BREADBOARD_COLAB_KEY") ?? string.Empty;
        }

        RemoteTutorTarget? saved = _endpointStore.Load();
        if (saved is not null)
        {
            _savedEndpoint = saved;
            PairCode = ColabPairCode.Format(saved);
            RemoteAiStatus = "Saved Colab endpoint found. Probing it now...";
        }
    }

    private async Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        GoogleTokenSet? tokens = _googleTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return null;

        if (!tokens.IsExpired(DateTimeOffset.UtcNow))
            return tokens.AccessToken;

        if (!tokens.HasRefreshToken)
            return null;

        _oauth ??= new GoogleOAuthClient(
            GoogleOAuthOptions.FromEnvironment().WithClient(GoogleClientId, NullIfBlank(GoogleClientSecret)));

        GoogleTokenSet refreshed = await _oauth.RefreshAsync(tokens.RefreshToken!, cancellationToken);
        _googleTokens = refreshed;
        _tokenStore.Save(refreshed);
        return refreshed.AccessToken;
    }

    /// <summary>Reads the shared Drive session, probes the tunnel and swaps the tutor if healthy.</summary>
    private async Task PollColabAsync(bool force)
    {
        if (Interlocked.CompareExchange(ref _remotePolling, 1, 0) != 0)
            return;

        try
        {
            if (!UseColabAi && !force)
                return;

            if (_googleTokens is null)
            {
                if (force)
                    RemoteAiStatus = "No Google account linked. Press LINK GOOGLE first.";
                return;
            }

            string? accessToken = await EnsureAccessTokenAsync(CancellationToken.None);
            if (accessToken is null)
            {
                RemoteAiStatus = "Google session expired. Link the account again.";
                return;
            }

            ColabSession? session = await _drive.ReadSessionAsync(accessToken);
            _colabSession = session;

            if (session is null)
            {
                RemoteAiStatus = "No Colab session file in Drive yet. Press PUBLISH SESSION.";
                return;
            }

            if (!session.IsServing)
            {
                RemoteAiStatus = string.IsNullOrWhiteSpace(session.Detail)
                    ? $"Colab session is '{session.Status}'."
                    : $"Colab session is '{session.Status}': {session.Detail}";
                return;
            }

            RemoteProbeResult probe = await RemoteEndpointProbe.ProbeAsync(
                _probeHttp,
                session.EndpointUrl,
                session.ApiKey);

            if (!probe.IsHealthy)
            {
                RemoteAiStatus = $"Colab published a tunnel but it is not answering: {probe.Detail}.";
                return;
            }

            RemoteTutorTarget? target = RemoteTutorTarget.FromSession(session);
            if (target is null)
            {
                RemoteAiStatus = "Colab session file has no usable chat endpoint.";
                return;
            }

            if (!ColabEndpoints.IsSecureEndpoint(target.EndpointUrl))
            {
                RemoteAiStatus =
                    $"Refused the Drive session: {target.DisplayHost} is plain http. Re-run the notebook so it publishes an https tunnel.";
                return;
            }

            ApplyRemoteTarget(target);
            SelectedAiRoute = AiRouteMethod.ColabTunnel;
            PairCode = ColabPairCode.Format(target);
            RemoteAiStatus =
                $"Serving {session.Model ?? "Qwen"} on {target.DisplayHost} · {probe.LatencyMs:0} ms{(string.IsNullOrWhiteSpace(session.Gpu) ? string.Empty : " · " + session.Gpu)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Colab refresh failed: " + ex.Message;
        }
        finally
        {
            Volatile.Write(ref _remotePolling, 0);
        }
    }

    private void ApplyRemoteTarget(RemoteTutorTarget target)
    {
        if (_appliedRemoteTarget is not null
            && string.Equals(_appliedRemoteTarget.EndpointUrl, target.EndpointUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_appliedRemoteTarget.ApiKey, target.ApiKey, StringComparison.Ordinal)
            && string.Equals(_appliedRemoteTarget.ModelAlias, target.ModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appliedRemoteTarget = target;
        _coordinator?.SetRemoteTutor(target);
        RemoteRouteLabel = DescribeRoute(target);
    }

    /// <summary>Loopback providers are labelled LOCAL AI so nobody looks for a Colab tunnel.</summary>
    private static string DescribeRoute(RemoteTutorTarget target) =>
        target.IsLoopback
            ? $"TUTOR ROUTE: LOCAL AI \u00b7 {target.DisplayHost}"
            : $"TUTOR ROUTE: COLAB \u00b7 {target.DisplayHost}";

    private static string CreateSessionKey()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "bbaoi_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        if (value.Source != CameraSource.ImageFile)
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

    partial void OnSelectedPartOptionChanged(BuildPartOption? value)
    {
        if (value is null)
            return;

        RefreshPartValuePresets(value.Kind);

        // The constructor sets the palette before the footprint list exists.
        IReadOnlyList<PinCountOption>? footprints = PinCountOptions;
        if (footprints is not { Count: > 0 })
            return;

        // Only the IC palette entry supports DIP/module footprints, so a resistor
        // selection must not leave a 28-pin DIP armed.
        if (value.Kind != ComponentKind.IntegratedCircuit)
        {
            SelectedPinCount = footprints[0];
        }
        else if (SelectedPinCount is null || SelectedPinCount.Count < 3)
        {
            for (int i = 0; i < footprints.Count; i++)
            {
                if (footprints[i].Count != 8)
                    continue;

                SelectedPinCount = footprints[i];
                break;
            }
        }

        if (_hasPendingBuildHole)
            return;

        BuilderHint = value.Kind == ComponentKind.IntegratedCircuit && (SelectedPinCount?.Count ?? 2) >= 3
            ? $"{value.Label} selected: click pin 1; the footprint is placed in one click."
            : $"{value.Label} selected: click its first tie point.";
    }

    partial void OnSelectedDraftPartChanged(DraftPartRow? value)
    {
        EditPartValue = value?.Part.Value ?? string.Empty;
        EditPartSymbol = value?.Part.SymbolId ?? string.Empty;
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

    partial void OnSpeakRuleBasedAdviceChanged(bool value)
    {
        if (_coordinator is not null)
            _coordinator.SpeakRuleBasedAdvice = value;
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

        _renderer.Dispose();

        _oauth?.Dispose();
        _drive.Dispose();
        _probeHttp.Dispose();
    }
}
