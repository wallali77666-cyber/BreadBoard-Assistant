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
using BreadboardAOI.Core.Contracts;
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

/// <summary>Camera source option shown by the toolbar cycle button.</summary>
public sealed record CameraOption(CameraSource Source, string Label);

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
    private readonly object _transcriptGate = new();
    private readonly StringBuilder _transcriptBuffer = new(4096);
    private readonly List<ViolationRow> _violationScratch = new(16);

    // Remote AI / Colab link. All of these are touched from the dispatcher only.
    private readonly GoogleTokenStore _tokenStore = new();
    private readonly GoogleClientConfigStore _clientStore = new();
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
    private int _remotePolling;

    private InspectionPipelineCoordinator? _coordinator;
    private bool _transcriptDirty;
    private bool _suppressScenarioSync;
    private string? _pendingImagePath;
    private bool _disposed;

    public MainWorkstationViewModel()
        : this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
    {
    }

    public MainWorkstationViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

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

        _suppressScenarioSync = true;
        SelectedScenario = ScenarioItems[0];
        _suppressScenarioSync = false;

        SelectedCamera = CameraOptions[0];

        _renderer.HoverChanged += OnRendererHoverChanged;

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

        string? startupImage = Environment.GetEnvironmentVariable("BREADBOARD_IMAGE_PATH");
        if (!string.IsNullOrWhiteSpace(startupImage) && File.Exists(startupImage))
            _pendingImagePath = startupImage;
    }

    // ---- catalogue / toolbar ------------------------------------------------

    public ObservableCollection<DemoScenarioItem> ScenarioItems { get; }

    public IReadOnlyList<CameraOption> CameraOptions { get; }

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
    private bool _showComponentBoxes = true;

    [ObservableProperty]
    private bool _showComponentLabels = true;

    [ObservableProperty]
    private bool _showRulers = true;

    [ObservableProperty]
    private bool _showTelemetry = true;

    [ObservableProperty]
    private bool _showCameraUnderlay;

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

    [ObservableProperty]
    private bool _isRemoteBusy;

    [ObservableProperty]
    private string _remoteRouteLabel = "TUTOR ROUTE: LOCAL";

    // ---- telemetry ---------------------------------------------------------

    [ObservableProperty]
    private string _frameTelemetry = "FRAME 0  ·  YOLO --.- MS  ·  DRC --.--- MS";

    [ObservableProperty]
    private string _visionBackend = "VISION: INITIALIZING";

    [ObservableProperty]
    private string _voiceBackend = "VOICE: INITIALIZING";

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
        coordinator.ReflexDrcUpdated += OnReflexDrcUpdated;
        coordinator.TutorTranscriptTokenReceived += OnTutorToken;
        coordinator.TutorDiagnosticCompleted += OnTutorCompleted;
        coordinator.StatusChanged += OnStatusChanged;
        coordinator.DemoScenarioChanged += OnDemoScenarioChanged;
        coordinator.EmergencyAlarmRaised += OnEmergencyAlarmRaised;

        OnPropertyChanged(nameof(IsPipelineReady));

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

        if (UseColabAi)
            _ = PollColabAsync(force: true);

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

        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is not null)
        {
            coordinator.SetImageSource(path);
            CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
            StatusLine = $"Still image loaded: {Path.GetFileName(path)}";
            return;
        }

        StatusLine = $"Still image queued: {Path.GetFileName(path)}";
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
        GoogleAccountStatus = "NOT LINKED";
        RemoteAiStatus = "Google account unlinked. Local tutor remains active.";
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
        string? endpoint = ColabSession.NormalizeBaseUrl(ManualEndpoint);
        if (endpoint is null)
        {
            RemoteAiStatus = "Enter a tunnel URL such as https://random-words.trycloudflare.com.";
            return;
        }

        IsRemoteBusy = true;
        try
        {
            string? key = NullIfBlank(ManualApiKey);
            RemoteProbeResult probe = await RemoteEndpointProbe.ProbeAsync(_probeHttp, endpoint, key);

            if (!probe.IsHealthy)
            {
                RemoteAiStatus = $"Endpoint not serving: {probe.Detail}.";
                return;
            }

            ApplyRemoteTarget(new RemoteTutorTarget(endpoint, key, null));
            UseColabAi = true;
            RemoteAiStatus = $"Manual endpoint online ({probe.LatencyMs:0} ms). The tutor streams from it.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Manual endpoint failed: " + ex.Message;
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

            ApplyRemoteTarget(target);
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
            && string.Equals(_appliedRemoteTarget.ApiKey, target.ApiKey, StringComparison.Ordinal))
        {
            return;
        }

        _appliedRemoteTarget = target;
        _coordinator?.SetRemoteTutor(target);
        RemoteRouteLabel = $"TUTOR ROUTE: COLAB · {target.DisplayHost}";
    }

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
        });
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
            if (_transcriptBuffer.Length > 0)
                _transcriptBuffer.AppendLine().AppendLine();

            _transcriptDirty = true;
        }
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

    partial void OnSelectedScenarioChanged(DemoScenarioItem? value)
    {
        if (_suppressScenarioSync || value is null)
            return;

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

    partial void OnShowComponentLabelsChanged(bool value) => _renderer.Layers.ShowComponentLabels = value;

    partial void OnShowRulersChanged(bool value) => _renderer.Layers.ShowRulers = value;

    partial void OnShowTelemetryChanged(bool value) => _renderer.Layers.ShowTelemetry = value;

    partial void OnShowCameraUnderlayChanged(bool value) => _renderer.Layers.ShowCameraUnderlay = value;

    partial void OnIsVoiceMutedChanged(bool value) => _coordinator?.SetVoiceMuted(value);

    partial void OnUseColabAiChanged(bool value)
    {
        if (value)
        {
            _remoteTimer.Start();
            _ = PollColabAsync(force: true);
        }
        else
        {
            _remoteTimer.Stop();
            _appliedRemoteTarget = null;
            _coordinator?.SetRemoteTutor(null);
            RemoteRouteLabel = "TUTOR ROUTE: LOCAL";
            RemoteAiStatus = "Remote AI disabled. Local tutor is active.";
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
        VramTelemetry = $"VRAM: {coordinator.VramStatus}";
        AffinityTelemetry = $"AFFINITY: {coordinator.AffinityStatus}";
        TargetNetlistStatus = coordinator.TargetSchematicStatus;
        CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
        IsTutorBusy = coordinator.IsTutorBusy;

        RemoteRouteLabel = coordinator.UsingRemoteTutor
            ? $"TUTOR ROUTE: COLAB · {_appliedRemoteTarget?.DisplayHost ?? "REMOTE"}"
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

        _disposed = true;
        _telemetryTimer.Stop();
        _remoteTimer.Stop();
        _renderer.HoverChanged -= OnRendererHoverChanged;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        _coordinator = null;

        if (coordinator is not null)
        {
            coordinator.ReflexDrcUpdated -= OnReflexDrcUpdated;
            coordinator.TutorTranscriptTokenReceived -= OnTutorToken;
            coordinator.TutorDiagnosticCompleted -= OnTutorCompleted;
            coordinator.StatusChanged -= OnStatusChanged;
            coordinator.DemoScenarioChanged -= OnDemoScenarioChanged;
            coordinator.EmergencyAlarmRaised -= OnEmergencyAlarmRaised;

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
