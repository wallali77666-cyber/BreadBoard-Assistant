// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/AiVisionInspectionWindow.xaml.cs
// PURPOSE: AI Live Studio. Dual-viewport layout (Live 60 FPS Camera + Uploaded
//          Photo), instant Qwen vision analysis on image upload, streaming
//          reasoning & results, automatic voice reflex instructions, and LM Studio
//          compatibility with green status LED & explicit hardware telemetry.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using BreadboardAOI.App.Workspace;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;
using BreadboardAOI.Vision;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace BreadboardAOI.App.Views;

public partial class AiVisionInspectionWindow : Window
{
    private const int BoardWidth = BreadboardSpec.CanvasWidth;
    private const int BoardHeight = BreadboardSpec.CanvasHeight;
    private const int MaxVisionEdge = 1568;

    private enum StudioViewMode
    {
        Dual,
        LiveOnly,
        PhotoOnly
    }

    private readonly InspectionPipelineCoordinator? _coordinator;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly byte[] _boardPixels = new byte[BoardWidth * BoardHeight * 4];

    private WriteableBitmap? _boardWriteable;
    private BitmapSource? _photoDisplay;
    private string? _imagePath;
    private string? _loadedPhotoPath;
    private byte[]? _visionPng;
    private DrcStatePacket? _livePacket;
    private DrcStatePacket? _photoPacket;
    private IReadOnlyList<RatsnestVector>? _ratsnest;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _speakCts;

    private double _liveZoom = 1.0;
    private Point _livePanStart;
    private bool _isLivePanning;

    private double _photoZoom = 1.0;
    private Point _photoPanStart;
    private bool _isPhotoPanning;

    private StudioViewMode _viewMode = StudioViewMode.Dual;
    private bool _liveMode = true;
    private bool _hasPhoto;
    private bool _isClosed;
    private bool _lastAnswerFromServer;
    private string _voiceInstruction = string.Empty;
    private int _analysisGeneration;
    private int _liveTick;
    private int _tutorProbeBusy;
    private bool _voiceSelectionSilent = true;
    private bool _autoCaptureWhenHandsFree;
    private long _lastAutoCapturedEpoch = -1;
    private bool _isAnalysisStreaming;

    /// <summary>
    /// Set by the workstation so LINK LM STUDIO uses the exact same connect,
    /// discovery and persist path as AI ROUTING. Without it the studio would
    /// swap only the coordinator and leave the rest of the app stale.
    /// </summary>
    public Func<string, Task<bool>>? LocalProviderLink { get; set; }

    public AiVisionInspectionWindow()
    {
        InitializeComponent();

        _liveTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _liveTimer.Tick += OnLiveTimerTick;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += OnStatusTimerTick;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        KeyDown += OnWindowKeyDown;
    }

    public AiVisionInspectionWindow(InspectionPipelineCoordinator coordinator, string? imagePath = null)
        : this()
    {
        _coordinator = coordinator;
        _imagePath = imagePath;

        if (_coordinator is not null)
        {
            _livePacket = _coordinator.LatestPacket;
            _ratsnest = _coordinator.LatestRatsnest;
        }
    }

    /// <summary>
    /// Updates the active inspection with a new image path, packet, or ratsnest
    /// vectors. Called when the main workstation opens an already-running studio.
    /// </summary>
    public void UpdateInspectionData(
        string? imagePath,
        DrcStatePacket? packet,
        IReadOnlyList<RatsnestVector>? ratsnest)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateInspectionData(imagePath, packet, ratsnest)));
            return;
        }

        bool isWorkstationSnapshot = !string.IsNullOrWhiteSpace(imagePath) &&
            imagePath.EndsWith("ask_ai_breadboard.png", StringComparison.OrdinalIgnoreCase);

        _photoPacket = isWorkstationSnapshot ? packet : null;
        if (_coordinator is not null)
            _livePacket = _coordinator.LatestPacket;
        if (ratsnest is not null)
            _ratsnest = ratsnest;

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            LoadPhoto(imagePath, autoAnalyze: true);
            return;
        }
        else
        {
            _hasPhoto = false;
            _loadedPhotoPath = null;
            _photoDisplay = null;
            _photoPacket = null;
            _imagePath = null;
            PhotoFrameImage.Source = null;
            PhotoPlaceholder.Visibility = Visibility.Visible;
            PhotoMetaText.Text = "NO PHOTO LOADED";
            PhotoStatusText.Text = "READY FOR PHOTO UPLOAD";
            SetViewMode(StudioViewMode.Dual);
            RefreshLiveFrame(force: true);
        }

        UpdateHardwareTelemetry();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        InitializeVoiceControls();
        UpdateTutorBadge();
        UpdateVoiceBadge();
        UpdateHardwareTelemetry();
        UpdateLiveCamSourceButtonText();
        _statusTimer.Start();
        _liveTimer.Start();
        _ = ProbeTutorAsync();

        RefreshLiveFrame(force: true);

        if (!string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath))
        {
            LoadPhoto(_imagePath, autoAnalyze: true);
            return;
        }

        _hasPhoto = false;
        SetViewMode(StudioViewMode.Dual);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _liveTimer.Stop();
        _statusTimer.Stop();
        _analysisCts?.Cancel();
        _speakCts?.Cancel();

        if (VoiceEngineComboBox is not null)
            VoiceEngineComboBox.SelectionChanged -= OnVoiceEngineSelectionChanged;
        if (ElevenLabsVoiceComboBox is not null)
            ElevenLabsVoiceComboBox.SelectionChanged -= OnElevenLabsVoiceSelectionChanged;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            OnAnalyzeAgainClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_isClosed)
            return;

        UpdateTutorBadge();
        UpdateVoiceBadge();
        UpdateHardwareTelemetry();
        UpdateLiveCamSourceButtonText();
        _ = ProbeTutorAsync();
    }

    /// <summary>
    /// Polls the endpoint from the studio's status timer. Keeps the tutor light
    /// honest even when the workstation is idle, and never blocks the UI thread.
    /// </summary>
    private async Task ProbeTutorAsync()
    {
        if (_isClosed || Interlocked.Exchange(ref _tutorProbeBusy, 1) != 0)
            return;

        try
        {
            QwenMoEService? qwen = _coordinator?.QwenService;
            if (qwen is not null)
                await qwen.ProbeConnectionAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Studio closed
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline
        }
        finally
        {
            Volatile.Write(ref _tutorProbeBusy, 0);
            if (!_isClosed)
                UpdateTutorBadge();
        }
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        if (_isClosed || !_liveMode)
            return;

        _liveTick++;
        RefreshLiveFrame(force: _liveTick % 3 == 0);
        UpdateHandGateAndAutoCapture();
    }

    private void OnManualCaptureModeClick(object sender, RoutedEventArgs e)
    {
        _autoCaptureWhenHandsFree = false;
        ManualCaptureModeButton.IsChecked = true;
        AutoHandsFreeModeButton.IsChecked = false;
        BottomStatusText.Text = "Manual capture mode active · click CAPTURE & ANALYZE NOW to inspect live frame.";
    }

    private void OnAutoHandsFreeModeClick(object sender, RoutedEventArgs e)
    {
        _autoCaptureWhenHandsFree = true;
        _lastAutoCapturedEpoch = -1;
        ManualCaptureModeButton.IsChecked = false;
        AutoHandsFreeModeButton.IsChecked = true;
        BottomStatusText.Text = "Auto hands-free mode active · waiting for hands off board & motion settle.";
        UpdateHandGateAndAutoCapture();
    }

    private void OnCaptureAndAnalyzeLiveClick(object sender, RoutedEventArgs e)
    {
        if (!CaptureLiveSnapshotIntoPreview("LIVE SNAPSHOT", ensurePhotoVisible: true))
        {
            BottomStatusText.Text = "No live camera frame available to capture yet.";
            return;
        }

        _ = RunAnalysisAsync(photo: true, autoSpeak: true);
    }

    private bool CaptureLiveSnapshotIntoPreview(string statusLabel, bool ensurePhotoVisible)
    {
        if (_coordinator is null)
            return false;

        byte[]? png = _coordinator.CaptureLiveWarpedPng() ?? _coordinator.CaptureLiveRawPng();
        if (png is not { Length: > 0 })
            return false;

        if (ensurePhotoVisible && _viewMode == StudioViewMode.LiveOnly)
            SetViewMode(StudioViewMode.Dual);

        UpdateSnapshotPreviewFromBytes(png, statusLabel);
        return true;
    }

    private void UpdateSnapshotPreviewFromBytes(byte[] png, string statusLabel)
    {
        BitmapImage snapshot = CreateBitmapImage(png);
        _photoDisplay = snapshot;
        _visionPng = png;
        _hasPhoto = true;
        _loadedPhotoPath = statusLabel;
        _photoPacket = _coordinator?.LatestPacket;

        PhotoFrameImage.Source = snapshot;
        PhotoPlaceholder.Visibility = Visibility.Collapsed;
        PhotoMetaText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.PixelWidth} × {snapshot.PixelHeight} · {statusLabel}");
        PhotoStatusText.Text = statusLabel;
        PhotoActionBadge.Text = "QWEN ANALYZING...";
        PhotoActionBadge.Foreground = (Brush)FindResource("LedOnlineBrush");
        PhotoViewportBorder.BorderBrush = (Brush)FindResource("LedOnlineBrush");

        SetVisionInput(png, snapshot.PixelWidth, snapshot.PixelHeight);
        RenderOverlays();
    }

    private void UpdateHandGateAndAutoCapture()
    {
        if (_isClosed || _coordinator is null)
            return;

        if (_coordinator.HasHandInFrame)
        {
            HandGateStatusText.Text = "🖐 HANDS ON BOARD · WAITING";
            var warnBrush = (Brush)FindResource("DrcWarningBrush");
            HandGateDot.Fill = warnBrush;
            HandGateBadgeBorder.BorderBrush = warnBrush;
            HandSettleProgressBar.Value = 0;
            HandSettleProgressBar.Foreground = warnBrush;
            return;
        }

        if (!_coordinator.IsHandFreeAndStable)
        {
            double pct = Math.Clamp(
                (_coordinator.StableElapsedMs / HandMotionGate.DefaultStableThresholdMs) * 100.0,
                0d,
                99d);
            HandGateStatusText.Text = "⏳ MOTION SETTLING...";
            var accentBrush = (Brush)FindResource("AccentBlueBrush");
            HandGateDot.Fill = accentBrush;
            HandGateBadgeBorder.BorderBrush = accentBrush;
            HandSettleProgressBar.Value = pct;
            HandSettleProgressBar.Foreground = accentBrush;
            return;
        }

        HandGateStatusText.Text = "✓ NO HANDS · BOARD STABLE";
        var okBrush = (Brush)FindResource("LedOnlineBrush");
        HandGateDot.Fill = okBrush;
        HandGateBadgeBorder.BorderBrush = okBrush;
        HandSettleProgressBar.Value = 100;
        HandSettleProgressBar.Foreground = okBrush;

        if (_autoCaptureWhenHandsFree && !_isAnalysisStreaming)
        {
            long epoch = _coordinator.SettledEpoch;
            if (epoch > 0 && epoch != _lastAutoCapturedEpoch)
            {
                _lastAutoCapturedEpoch = epoch;
                if (CaptureLiveSnapshotIntoPreview("AUTO CAPTURED · NO HANDS", ensurePhotoVisible: false))
                {
                    _ = RunAnalysisAsync(photo: true, autoSpeak: true);
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Layout Mode Switching
    // ---------------------------------------------------------------------

    private void SetViewMode(StudioViewMode mode)
    {
        _viewMode = mode;
        DualViewModeButton.IsChecked = mode == StudioViewMode.Dual;
        LiveOnlyModeButton.IsChecked = mode == StudioViewMode.LiveOnly;
        PhotoOnlyModeButton.IsChecked = mode == StudioViewMode.PhotoOnly;

        switch (mode)
        {
            case StudioViewMode.Dual:
                LiveColDef.Width = new GridLength(1, GridUnitType.Star);
                SplitterColDef.Width = new GridLength(6);
                PhotoColDef.Width = new GridLength(1, GridUnitType.Star);
                LiveViewportBorder.Visibility = Visibility.Visible;
                ViewportSplitter.Visibility = Visibility.Visible;
                PhotoViewportBorder.Visibility = Visibility.Visible;
                ViewportSummaryText.Text = "DUAL VIEW ACTIVE · LIVE CAMERA & UPLOADED PHOTO";
                _liveMode = true;
                _liveTimer.IsEnabled = true;
                break;

            case StudioViewMode.LiveOnly:
                LiveColDef.Width = new GridLength(1, GridUnitType.Star);
                SplitterColDef.Width = new GridLength(0);
                PhotoColDef.Width = new GridLength(0);
                LiveViewportBorder.Visibility = Visibility.Visible;
                ViewportSplitter.Visibility = Visibility.Collapsed;
                PhotoViewportBorder.Visibility = Visibility.Collapsed;
                ViewportSummaryText.Text = "LIVE CAMERA FOCUSED · 60 FPS STREAM";
                _liveMode = true;
                _liveTimer.IsEnabled = true;
                break;

            case StudioViewMode.PhotoOnly:
                LiveColDef.Width = new GridLength(0);
                SplitterColDef.Width = new GridLength(0);
                PhotoColDef.Width = new GridLength(1, GridUnitType.Star);
                LiveViewportBorder.Visibility = Visibility.Collapsed;
                ViewportSplitter.Visibility = Visibility.Collapsed;
                PhotoViewportBorder.Visibility = Visibility.Visible;
                ViewportSummaryText.Text = "UPLOADED PHOTO FOCUSED · INSPECTION VIEW";
                _liveMode = false;
                break;
        }

        if (_liveMode)
            RefreshLiveFrame(force: true);
    }

    private void OnDualViewClick(object sender, RoutedEventArgs e) => SetViewMode(StudioViewMode.Dual);
    private void OnLiveOnlyClick(object sender, RoutedEventArgs e) => SetViewMode(StudioViewMode.LiveOnly);
    private void OnPhotoOnlyClick(object sender, RoutedEventArgs e) => SetViewMode(StudioViewMode.PhotoOnly);

    // ---------------------------------------------------------------------
    // Frame Sources & Refresh
    // ---------------------------------------------------------------------

    private void RefreshLiveFrame(bool force)
    {
        if (_isClosed || !_liveMode)
            return;

        LiveOverlayCanvas.Visibility = Visibility.Visible;

        if (_coordinator is null)
        {
            LivePlaceholder.Visibility = Visibility.Visible;
            LiveStatusText.Text = "NO PIPELINE";
            return;
        }

        if (!_coordinator.CopyBoardBitmap(_boardPixels, out int stride))
        {
            byte[]? fallbackPng = _coordinator.CaptureLiveWarpedPng() ?? _coordinator.CaptureLiveRawPng();
            if (fallbackPng is { Length: > 0 })
            {
                LiveFrameImage.Source = CreateBitmapImage(fallbackPng);
                LiveFrameImage.Stretch = Stretch.Uniform;
                LivePlaceholder.Visibility = Visibility.Collapsed;
                LiveMetaText.Text = "LIVE STREAM · FALLBACK";
                LiveStatusText.Text = $"LIVE · {_coordinator.CameraStatus}";

                if (force)
                {
                    _livePacket = _coordinator.LatestPacket;
                    _ratsnest = _coordinator.LatestRatsnest;
                    RenderOverlays();
                }

                return;
            }

            LiveStatusText.Text = "WAITING FOR CAMERA FRAME";
            return;
        }

        if (_boardWriteable is null
            || _boardWriteable.PixelWidth != BoardWidth
            || _boardWriteable.PixelHeight != BoardHeight)
        {
            _boardWriteable = new WriteableBitmap(
                BoardWidth,
                BoardHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            LiveFrameImage.Source = _boardWriteable;
        }
        else if (!ReferenceEquals(LiveFrameImage.Source, _boardWriteable))
        {
            LiveFrameImage.Source = _boardWriteable;
        }

        _boardWriteable.WritePixels(
            new Int32Rect(0, 0, BoardWidth, BoardHeight),
            _boardPixels,
            stride,
            0);

        LiveFrameImage.Stretch = Stretch.Fill;
        LivePlaceholder.Visibility = Visibility.Collapsed;
        LiveMetaText.Text = "1000 × 500 · 60 FPS";
        LiveStatusText.Text = $"LIVE · {_coordinator.CameraStatus}";

        if (!force)
            return;

        _livePacket = _coordinator.LatestPacket;
        _ratsnest = _coordinator.LatestRatsnest;
        RenderOverlays();
    }

    public bool LoadPhoto(string path, bool autoAnalyze)
    {
        try
        {
            bool isWorkstationSnapshot = path.EndsWith("ask_ai_breadboard.png", StringComparison.OrdinalIgnoreCase);
            if (isWorkstationSnapshot)
            {
                _photoPacket = _coordinator?.LatestPacket;
                _ratsnest = _coordinator?.LatestRatsnest;
            }
            else
            {
                // External photos have their own visual layout; never draw workstation's demo circuit overlays
                _photoPacket = null;
            }

            BitmapSource display = CreateBitmapImageFromFile(path);
            _photoDisplay = display;
            _loadedPhotoPath = path;
            _imagePath = path;
            _hasPhoto = true;

            // Reset photo zoom to 100% (Fit) when a new photo is loaded
            SetPhotoZoom(1.0);

            // Clean slate for photo overlay (do not inherit stale live/demo components)
            RenderOverlays();

            // Immediately display the loaded photo in the inspection viewport
            PhotoFrameImage.Source = display;
            PhotoPlaceholder.Visibility = Visibility.Collapsed;
            PhotoMetaText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{display.PixelWidth} × {display.PixelHeight} · {System.IO.Path.GetFileName(path)}");
            PhotoStatusText.Text = $"PHOTO LOADED · {System.IO.Path.GetFileName(path)}";
            PhotoActionBadge.Text = "QWEN ANALYZING...";
            PhotoActionBadge.Foreground = (Brush)FindResource("LedOnlineBrush");
            PhotoViewportBorder.BorderBrush = (Brush)FindResource("LedOnlineBrush");

            // Ensure photo is visible
            if (_viewMode == StudioViewMode.LiveOnly)
                SetViewMode(StudioViewMode.Dual);

            _visionPng = BuildVisionPng(path, out int visionWidth, out int visionHeight);
            if (_visionPng is null || _visionPng.Length == 0)
            {
                ClearVisionInput("The photo could not be decoded for Qwen.");
                PhotoStatusText.Text = "PHOTO DECODE FAILED";
                return false;
            }

            SetVisionInput(_visionPng, visionWidth, visionHeight);

            if (autoAnalyze)
                _ = RunAnalysisAsync(photo: true, autoSpeak: true);

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BottomStatusText.Text = $"Photo load failed: {ex.Message}";
            PhotoStatusText.Text = $"PHOTO LOAD FAILED · {ex.Message}";
            return false;
        }
    }

    private void ShowSnapshot()
    {
        if (_photoDisplay is null)
            return;

        PhotoFrameImage.Source = _photoDisplay;
        PhotoPlaceholder.Visibility = Visibility.Collapsed;
        PhotoMetaText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_photoDisplay.PixelWidth} × {_photoDisplay.PixelHeight} · PHOTO");
        PhotoStatusText.Text = $"SNAPSHOT · {System.IO.Path.GetFileName(_loadedPhotoPath)}";
    }

    private void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Upload a breadboard photo for Qwen vision inspection",
            Filter = "Breadboard images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        LoadPhoto(dialog.FileName, autoAnalyze: true);
    }

    private void OnPhotoDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPhotoDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is { Length: > 0 } && File.Exists(files[0]))
        {
            LoadPhoto(files[0], autoAnalyze: true);
            e.Handled = true;
        }
    }

    private async void OnLinkLmStudioClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null)
            return;

        LinkLmStudioButton.IsEnabled = false;
        BottomStatusText.Text = "Linking to LM Studio at http://127.0.0.1:1234 ...";

        try
        {
            bool linked;
            if (LocalProviderLink is not null)
            {
                // Full path: probe, adopt the loaded model, remember the endpoint
                // and refresh MODEL LINK, exactly like AI ROUTING does.
                linked = await LocalProviderLink("lmstudio").ConfigureAwait(true);
            }
            else
            {
                // Base URL only; the coordinator's model discovery chooses the
                // loaded model. A full /v1/chat/completions URL is unnecessary.
                _coordinator.SetRemoteTutor(
                    new RemoteTutorTarget(LocalAiProviders.All[0].BaseUrl, null, null));
                linked = true;
            }

            BottomStatusText.Text = linked
                ? "LM Studio linked. Pin any exact model in AI ROUTING > MODEL LINK."
                : "LM Studio did not answer. Start its server, load a model, then link again.";

            await ProbeTutorAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BottomStatusText.Text = "LM Studio link failed: " + ex.Message;
        }
        finally
        {
            if (!_isClosed)
                LinkLmStudioButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------------
    // Qwen Analysis & Voice Synthesis
    // ---------------------------------------------------------------------

    private async Task RunAnalysisAsync(bool photo, bool autoSpeak)
    {
        if (_isClosed)
            return;

        QwenMoEService? qwen = _coordinator?.QwenService;
        if (qwen is null)
        {
            ResultTextBox.Text =
                "[Qwen is not connected] The cognitive tutor is disabled or has no endpoint. " +
                "Click 'LINK LM STUDIO' or open AI ROUTING > LOCAL AI, then press ANALYZE AGAIN.";
            ThinkingTextBox.Text = "No vision service is attached to this workstation.";
            ThinkingStatusText.Text = "OFFLINE";
            ThinkingDot.Fill = (Brush)FindResource("DrcWarningBrush");
            ThinkingDotGlow.Opacity = 0;
            AnalysisStatusText.Text = "NO TUTOR";
            SpeakResultButton.IsEnabled = false;
            _lastAnswerFromServer = false;
            _voiceInstruction = string.Empty;
            ResultStatusText.Text = "NO TUTOR";
            InstructionTextBox.Text =
                "No voice instruction: connect LM Studio or Ollama, or launch local llama-server.";
            return;
        }

        if (photo)
        {
            if (_visionPng is null || _visionPng.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(_loadedPhotoPath) || !File.Exists(_loadedPhotoPath))
                {
                    BottomStatusText.Text = "Upload a photo first, then press ANALYZE AGAIN.";
                    return;
                }

                _visionPng = BuildVisionPng(_loadedPhotoPath, out int width, out int height);
                if (_visionPng is null)
                {
                    BottomStatusText.Text = "The uploaded photo could not be decoded.";
                    return;
                }

                SetVisionInput(_visionPng, width, height);
            }
        }
        else
        {
            _livePacket = _coordinator?.LatestPacket;
        }

        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        CancellationToken token = cts.Token;
        int generation = ++_analysisGeneration;

        _lastAnswerFromServer = false;
        _voiceInstruction = string.Empty;
        AnalyzeButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SpeakResultButton.IsEnabled = false;
        ThinkingTextBox.Clear();
        ResultTextBox.Clear();
        InstructionTextBox.Text = "Waiting for the complete vision answer ...";
        ResultStatusText.Text = "STREAMING";
        ThinkingStatusText.Text = "CONNECTING...";
        ThinkingDot.Fill = (Brush)FindResource("LedOnlineBrush");
        ThinkingDotGlow.Opacity = 0.95;
        AnalysisStatusText.Text = photo ? "PHOTO ANALYSIS" : "LIVE ANALYSIS";
        BottomStatusText.Text = photo
            ? $"Sending uploaded photo to {qwen.EndpointSummary} ..."
            : $"Sending live frame to {qwen.EndpointSummary} ...";
        UpdateTutorBadge();

        string prompt;
        byte[]? visionBytes = null;

        if (photo)
        {
            // Direct Qwen-VL inspection: the uploaded photo is the only input.
            prompt = DiagnosticPromptBuilder.BuildPhotoInspection();

            visionBytes = _visionPng;
            if (visionBytes is { Length: > 0 })
                SetVisionInput(visionBytes, 0, 0);
        }
        else
        {
            // Direct Qwen-VL inspection: the raw live camera frame is the only input.
            // No OpenCV homography or warp, no YOLO integration.
            prompt = DiagnosticPromptBuilder.BuildLiveVisionInspection();

            byte[]? liveCamPng = _coordinator?.CaptureLiveRawPng();
            if (liveCamPng is { Length: > 0 })
            {
                visionBytes = liveCamPng;
                UpdateSnapshotPreviewFromBytes(visionBytes, "LIVE CAPTURE");
            }
            else
            {
                ClearVisionInput("Capturing live camera feed...");
            }
        }

        if (visionBytes is not { Length: > 0 })
        {
            ResultTextBox.Text =
                "[No image available] Start the live camera or upload a photo, then press ANALYZE AGAIN.";
            ThinkingTextBox.Text = "The vision model needs an image; no YOLO/DRC text was sent.";
            ThinkingStatusText.Text = "NO IMAGE";
            ThinkingDot.Fill = (Brush)FindResource("DrcWarningBrush");
            ThinkingDotGlow.Opacity = 0;
            AnalysisStatusText.Text = "NO IMAGE";
            BottomStatusText.Text = "No live frame or uploaded photo is available.";
            InstructionTextBox.Text = "No voice instruction: no image was available for the vision model.";
            ResultStatusText.Text = "NO IMAGE";
            SpeakResultButton.IsEnabled = false;
            AnalyzeButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            return;
        }

        var answer = new StringBuilder();
        Stopwatch clock = Stopwatch.StartNew();
        int streamChunks = 0;

        ThinkingTextBox.Text = "Direct vision request: the prompt and the raw frame are the only inputs.";
        ThinkingStatusText.Text = "ANSWERING...";

        _isAnalysisStreaming = true;
        try
        {
            await foreach (string chunk in qwen
                               .StreamImageAsync(visionBytes!, prompt, token)
                               .WithCancellation(token))
            {
                streamChunks++;
                if (_isClosed || generation != _analysisGeneration)
                    return;

                answer.Append(chunk);
                ResultTextBox.Text = answer.ToString();
                ResultTextBox.ScrollToEnd();
                InstructionTextBox.Text = "Generating humanized instructions (max 5 sentences)...";
            }

            clock.Stop();
            if (_isClosed || generation != _analysisGeneration)
                return;

            bool serverAnswer = qwen.LastAnswerUsable;
            _lastAnswerFromServer = serverAnswer;
            UpdateTutorBadge();

            if (serverAnswer)
            {
                ThinkingDot.Fill = (Brush)FindResource("LedOnlineBrush");
                ThinkingDotGlow.Opacity = 0.85;
                int words = answer.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                int estTokens = Math.Max(streamChunks, (int)Math.Ceiling(words * 1.33));
                if (estTokens == 0 && answer.Length > 0)
                    estTokens = Math.Max(1, (int)Math.Ceiling(answer.Length / 4.0));
                double elapsed = clock.Elapsed.TotalSeconds;
                double tokSec = elapsed > 0.05 ? estTokens / elapsed : 0.0;

                ThinkingStatusText.Text = $"COMPLETE · {elapsed:0.0}s · {estTokens} tok";
                AnalysisStatusText.Text = $"{elapsed:0.0}s · {estTokens} tok ({tokSec:0.1} tok/s) · {qwen.EndpointProvider}";
                BottomStatusText.Text = $"Vision answer received from {qwen.EndpointSummary} in {elapsed:0.0}s ({estTokens} tokens, {tokSec:0.1} tok/s).";
                PhotoActionBadge.Text = "ANALYSIS COMPLETE";
                PhotoActionBadge.Foreground = (Brush)FindResource("LedOnlineBrush");

                ThinkingTextBox.Text = "Model answered directly; no reasoning channel is used.";

                string fullInstruction = CapSentences(answer.ToString().Trim(), 5);
                _voiceInstruction = BuildVoiceInstruction(fullInstruction);
                bool hasInstruction = fullInstruction.Length > 0;
                InstructionTextBox.Text = hasInstruction
                    ? fullInstruction
                    : "No instructions returned.";
                ResultStatusText.Text = hasInstruction ? "INSTRUCTIONS READY" : "NO ANSWER";
                SpeakResultButton.IsEnabled = _voiceInstruction.Length > 0;
            }
            else
            {
                ThinkingDot.Fill = (Brush)FindResource("DrcWarningBrush");
                ThinkingDotGlow.Opacity = 0;
                ThinkingStatusText.Text = "OFFLINE";
                AnalysisStatusText.Text = "OFFLINE";
                BottomStatusText.Text = qwen.LastError
                    ?? "The vision model did not answer. Check the endpoint under AI ROUTING.";

                ThinkingTextBox.Text = "No answer. The status bar shows the endpoint error.";

                _voiceInstruction = string.Empty;
                InstructionTextBox.Text =
                    "No voice instruction: the vision model is offline.";
                ResultStatusText.Text = "OFFLINE";
                SpeakResultButton.IsEnabled = false;
            }

            // Automatic Voice Reflex Playback: the model's answer is the only voice source.
            bool allowVoice = serverAnswer;
            if (autoSpeak
                && allowVoice
                && AutoSpeakCheckBox.IsChecked == true
                && _voiceInstruction.Length > 0)
            {
                await SpeakResultAsync(_voiceInstruction, generation).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed && generation == _analysisGeneration)
            {
                ThinkingStatusText.Text = "STOPPED";
                ThinkingDot.Fill = (Brush)FindResource("TextMutedBrush");
                ThinkingDotGlow.Opacity = 0;
                AnalysisStatusText.Text = "STOPPED";
                BottomStatusText.Text = "Qwen analysis stopped.";
                _voiceInstruction = string.Empty;
                SpeakResultButton.IsEnabled = false;
                ResultStatusText.Text = "STOPPED";
                InstructionTextBox.Text = "Analysis stopped before a complete instruction arrived.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_isClosed && generation == _analysisGeneration)
            {
                ThinkingStatusText.Text = "ERROR";
                ThinkingDot.Fill = (Brush)FindResource("DrcCriticalBrush");
                ThinkingDotGlow.Opacity = 0;
                AnalysisStatusText.Text = "ERROR";
                BottomStatusText.Text = $"Qwen error: {ex.Message}";
                if (answer.Length == 0)
                    ResultTextBox.Text = $"[Qwen stream error] {ex.Message}";
                _voiceInstruction = string.Empty;
                SpeakResultButton.IsEnabled = false;
            }
        }
        finally
        {
            if (generation == _analysisGeneration)
                _isAnalysisStreaming = false;

            if (!_isClosed && generation == _analysisGeneration)
            {
                AnalyzeButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
    }

    private async Task SpeakResultAsync(string text, int generation, bool force = false)
    {
        // Only a real model answer may be synthesized; UI and DRC text stay silent.
        bool allowVoice = force || _lastAnswerFromServer;
        if (_coordinator is null || !allowVoice || string.IsNullOrWhiteSpace(text))
            return;

        _speakCts?.Cancel();
        var cts = new CancellationTokenSource();
        _speakCts = cts;

        try
        {
            BottomStatusText.Text = $"Synthesizing voice via {_coordinator.VoiceBackendDescription} ...";
            bool played = await _coordinator.SpeakTextAsync(text, cts.Token).ConfigureAwait(true);

            if (!_isClosed && generation == _analysisGeneration)
            {
                BottomStatusText.Text = played
                    ? $"Voice playback complete · {_coordinator.VoiceBackendDescription}."
                    : $"Voice backend unavailable ({_coordinator.VoiceBackendDescription}).";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed && generation == _analysisGeneration)
                BottomStatusText.Text = "Voice stopped.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_isClosed && generation == _analysisGeneration)
                BottomStatusText.Text = $"Voice playback error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_speakCts, cts))
                _speakCts = null;

            cts.Dispose();
        }
    }

    private void OnAnalyzeAgainClick(object sender, RoutedEventArgs e)
    {
        bool photo = _hasPhoto && _viewMode != StudioViewMode.LiveOnly;
        _ = RunAnalysisAsync(photo, autoSpeak: true);
    }

    private void OnAnalyzeVideoClick(object sender, RoutedEventArgs e)
    {
        _ = RunAnalysisAsync(photo: false, autoSpeak: true);
    }

    private void OnAnalyzePhotoClick(object sender, RoutedEventArgs e)
    {
        _ = RunAnalysisAsync(photo: true, autoSpeak: true);
    }

    private void OnLiveCamSourceMenuClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null) return;

        var menu = new ContextMenu();
        var currentSource = _coordinator.ActiveCameraSource;

        var syntheticItem = new MenuItem
        {
            Header = "📷 Synthetic Test Pattern",
            IsChecked = currentSource == CameraSource.Synthetic
        };
        syntheticItem.Click += (_, _) => SelectCameraSource(CameraSource.Synthetic);
        menu.Items.Add(syntheticItem);

        menu.Items.Add(new Separator());

        var available = CameraCaptureService.ProbeAvailableCameras();
        var cameraSources = new[]
        {
            (CameraSource.Camera0, "Camera 0 (Integrated / Primary Webcam)"),
            (CameraSource.Camera1, "Camera 1 (Secondary / USB Camera)"),
            (CameraSource.Camera2, "Camera 2 (USB Microscope / Cam)"),
            (CameraSource.Camera3, "Camera 3 (External USB Cam)")
        };

        foreach (var (source, label) in cameraSources)
        {
            bool isDetected = available.Contains(source);
            var item = new MenuItem
            {
                Header = $"📹 {label}" + (isDetected ? "  [Connected]" : ""),
                IsChecked = currentSource == source
            };
            item.Click += (_, _) => SelectCameraSource(source);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var loadVideoItem = new MenuItem { Header = "🎬 Load Breadboard Video File (.mp4, .avi, .mov, .mkv)..." };
        loadVideoItem.Click += (_, _) => PromptLoadVideoFile();
        menu.Items.Add(loadVideoItem);

        var loadImageItem = new MenuItem { Header = "🖼️ Load Breadboard Still Photo (.png, .jpg, .webp)..." };
        loadImageItem.Click += (_, _) => OnUploadClick(this, new RoutedEventArgs());
        menu.Items.Add(loadImageItem);

        menu.PlacementTarget = LiveCamSourceButton;
        menu.IsOpen = true;
    }

    private void OnLoadLiveVideoClick(object sender, RoutedEventArgs e) => PromptLoadVideoFile();

    private void PromptLoadVideoFile()
    {
        if (_coordinator is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Breadboard Inspection Video",
            Filter = "Video files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            _livePacket = null;
            LiveOverlayCanvas.Children.Clear();
            _coordinator.SetVideoSource(dialog.FileName);
            UpdateLiveCamSourceButtonText();
            RenderOverlays();
        }
    }

    private void SelectCameraSource(CameraSource source)
    {
        if (_coordinator is null) return;
        _livePacket = null;
        LiveOverlayCanvas.Children.Clear();
        _coordinator.SetCameraSource(source);
        UpdateLiveCamSourceButtonText();
        RenderOverlays();
    }

    private void OnCycleLiveCamSourceClick(object sender, RoutedEventArgs e) => OnLiveCamSourceMenuClick(sender, e);

    private void UpdateLiveCamSourceButtonText()
    {
        if (_coordinator is null) return;
        CameraSource active = _coordinator.ActiveCameraSource;
        if (LiveCamSourceButton is not null)
        {
            LiveCamSourceButton.Content = active switch
            {
                CameraSource.Camera0 => "CAM: WEBCAM 0 ▾",
                CameraSource.Camera1 => "CAM: WEBCAM 1 ▾",
                CameraSource.Camera2 => "CAM: WEBCAM 2 ▾",
                CameraSource.Camera3 => "CAM: WEBCAM 3 ▾",
                CameraSource.Synthetic => "CAM: SYNTHETIC ▾",
                CameraSource.ImageFile => "CAM: STILL PHOTO ▾",
                CameraSource.VideoFile => "CAM: VIDEO REPLAY ▾",
                _ => "CAM: SELECT ▾"
            };
        }

        if (LiveStatusText is not null)
        {
            LiveStatusText.Text = active switch
            {
                CameraSource.Camera0 => "LIVE WEBCAM 0 (ACTIVE)",
                CameraSource.Camera1 => "LIVE WEBCAM 1 (ACTIVE)",
                CameraSource.Camera2 => "LIVE WEBCAM 2 (ACTIVE)",
                CameraSource.Camera3 => "LIVE WEBCAM 3 (ACTIVE)",
                CameraSource.Synthetic => "SYNTHETIC TEST PATTERN",
                CameraSource.ImageFile => "REPLAYING STILL IMAGE FILE",
                CameraSource.VideoFile => "REPLAYING INSPECTION VIDEO (60 FPS)",
                _ => "CAMERA STREAM READY"
            };
        }
    }

    // ---------------------------------------------------------------------
    // Zoom & Pan Tooling (Live Camera & Uploaded Photo Viewports)
    // ---------------------------------------------------------------------

    private void SetLiveZoom(double zoom)
    {
        _liveZoom = Math.Clamp(zoom, 0.5, 5.0);
        LiveScaleTransform.ScaleX = _liveZoom;
        LiveScaleTransform.ScaleY = _liveZoom;
        LiveZoomText.Text = $"{(_liveZoom * 100):0}%";
        if (Math.Abs(_liveZoom - 1.0) < 0.01)
        {
            LiveTranslateTransform.X = 0;
            LiveTranslateTransform.Y = 0;
        }
    }

    private void OnLiveZoomInClick(object sender, RoutedEventArgs e) => SetLiveZoom(_liveZoom * 1.25);
    private void OnLiveZoomOutClick(object sender, RoutedEventArgs e) => SetLiveZoom(_liveZoom / 1.25);
    private void OnLiveZoomFitClick(object sender, RoutedEventArgs e) => SetLiveZoom(1.0);

    private void OnLiveViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 0.85;
        SetLiveZoom(_liveZoom * factor);
        e.Handled = true;
    }

    private void OnLiveViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
        {
            _isLivePanning = true;
            _livePanStart = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse();
        }
    }

    private void OnLiveViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isLivePanning)
            return;
        Point current = e.GetPosition(this);
        LiveTranslateTransform.X += current.X - _livePanStart.X;
        LiveTranslateTransform.Y += current.Y - _livePanStart.Y;
        _livePanStart = current;
    }

    private void OnLiveViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isLivePanning = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void SetPhotoZoom(double zoom)
    {
        _photoZoom = Math.Clamp(zoom, 0.5, 5.0);
        PhotoScaleTransform.ScaleX = _photoZoom;
        PhotoScaleTransform.ScaleY = _photoZoom;
        PhotoZoomText.Text = $"{(_photoZoom * 100):0}%";
        if (Math.Abs(_photoZoom - 1.0) < 0.01)
        {
            PhotoTranslateTransform.X = 0;
            PhotoTranslateTransform.Y = 0;
        }
    }

    private void OnPhotoZoomInClick(object sender, RoutedEventArgs e) => SetPhotoZoom(_photoZoom * 1.25);
    private void OnPhotoZoomOutClick(object sender, RoutedEventArgs e) => SetPhotoZoom(_photoZoom / 1.25);
    private void OnPhotoZoomFitClick(object sender, RoutedEventArgs e) => SetPhotoZoom(1.0);

    private void OnPhotoViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 0.85;
        SetPhotoZoom(_photoZoom * factor);
        e.Handled = true;
    }

    private void OnPhotoViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
        {
            _isPhotoPanning = true;
            _photoPanStart = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse();
        }
    }

    private void OnPhotoViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPhotoPanning)
            return;
        Point current = e.GetPosition(this);
        PhotoTranslateTransform.X += current.X - _photoPanStart.X;
        PhotoTranslateTransform.Y += current.Y - _photoPanStart.Y;
        _photoPanStart = current;
    }

    private void OnPhotoViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPhotoPanning = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnSpeakResultClick(object sender, RoutedEventArgs e)
    {
        if (_voiceInstruction.Length == 0)
            return;

        _ = SpeakResultAsync(_voiceInstruction, _analysisGeneration);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _analysisCts?.Cancel();
        _speakCts?.Cancel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOverlayLayerToggled(object sender, RoutedEventArgs e) => RenderOverlays();

    // ---------------------------------------------------------------------
    // Status Badges & Hardware Telemetry
    // ---------------------------------------------------------------------

    private void UpdateTutorBadge()
    {
        if (TutorBadgeText is null) return;
        QwenMoEService? qwen = _coordinator?.QwenService;
        if (qwen is null)
        {
            TutorBadgeText.Text = "TUTOR: COGNITIVE LOOP DISABLED";
            TutorBadgeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            TutorBadgeText.FontWeight = FontWeights.Normal;
            if (TutorBadgeBorder is not null) TutorBadgeBorder.BorderBrush = (Brush)FindResource("DrcWarningBrush");
            if (TutorStatusLight is not null) TutorStatusLight.Fill = (Brush)FindResource("TextMutedBrush");
            if (TutorStatusGlow is not null) TutorStatusGlow.Opacity = 0;
            return;
        }

        bool online = qwen.IsConnected;
        TutorBadgeText.Text = $"TUTOR: {qwen.EndpointSummary} · {(online ? "ONLINE" : "OFFLINE")}".ToUpperInvariant();

        if (online)
        {
            var greenBrush = (Brush)FindResource("LedOnlineBrush");
            if (TutorBadgeBorder is not null) TutorBadgeBorder.BorderBrush = greenBrush;
            if (TutorStatusLight is not null) TutorStatusLight.Fill = greenBrush;
            if (TutorStatusGlow is not null) TutorStatusGlow.Opacity = 0.95;
            TutorBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xF3, 0xD0));
            TutorBadgeText.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            if (TutorBadgeBorder is not null) TutorBadgeBorder.BorderBrush = (Brush)FindResource("BorderBrush");
            if (TutorStatusLight is not null) TutorStatusLight.Fill = (Brush)FindResource("TextMutedBrush");
            if (TutorStatusGlow is not null) TutorStatusGlow.Opacity = 0;
            TutorBadgeText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            TutorBadgeText.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateVoiceBadge()
    {
        string voice = _coordinator?.VoiceBackendDescription ?? "VOICE SILENT";
        if (VoiceBackendText is not null)
            VoiceBackendText.Text = $"VOICE: {voice}".ToUpperInvariant();
    }

    private void UpdateHardwareTelemetry()
    {
        var plan = CpuAffinityManager.Plan;
        string aiCores = CpuAffinityManager.DescribeMaskCores(plan.AiMask);
        string visCores = CpuAffinityManager.DescribeMaskCores(plan.VisionMask);
        string uiCores = CpuAffinityManager.DescribeMaskCores(plan.DrcUiMask);
        string eCores = CpuAffinityManager.DescribeMaskCores(plan.ECoresMask);

        VramSnapshot snapshot = _coordinator?.GetVramSnapshot() ?? default;
        string vram = _coordinator?.VramStatus ?? "VRAM: --";
        string gpu = _coordinator?.GpuDeviceName ?? "System GPU";
        string vramTotal = snapshot.IsRealGpu
            ? string.Create(CultureInfo.InvariantCulture, $"{snapshot.TotalGigabytes:0.#} GB GDDR")
            : (_coordinator?.SystemVramBytes > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{_coordinator.SystemVramBytes / 1024d / 1024d / 1024d:0.#} GB VRAM")
                : "DirectX / iGPU");
        string cpuKind = plan.IsTargetWorkstation
            ? "TARGET HYBRID (P+E)"
            : "PORTABLE";

        if (HardwareAllocationText is not null)
        {
            HardwareAllocationText.Text =
                $"CPU: {CpuAffinityManager.CpuModelName}  |  GPU: {gpu} ({vramTotal})";
        }

        if (CpuAllocationDetailText is not null)
        {
            CpuAllocationDetailText.Text =
                $"CPU: {CpuAffinityManager.CpuModelName} · {plan.LogicalProcessorCount} LOGICAL · " +
                $"AI/QWEN {aiCores} [0x{plan.AiMask:X}] · " +
                $"VISION {visCores} [0x{plan.VisionMask:X}] · " +
                $"DRC/UI {uiCores} [0x{plan.DrcUiMask:X}] · " +
                $"VISION+DRC {eCores} [0x{plan.ECoresMask:X}]";
        }

        if (GpuAllocationDetailText is not null)
        {
            GpuAllocationDetailText.Text =
                $"GPU: {gpu} · {vramTotal} · YOLO11-POSE FP16 · SKIASHARP DIRECTX";
        }
    }

    // ---------------------------------------------------------------------
    // Exact Vision-Input Preview
    // ---------------------------------------------------------------------

    private void SetVisionInput(byte[] png, int width, int height)
    {
        try
        {
            VisionInputImage.Source = CreateBitmapImage(png);
            VisionInputPlaceholder.Visibility = Visibility.Collapsed;
            VisionInputMetaText.Text = width > 0 && height > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{width} × {height} · {FormatBytes(png.Length)}")
                : $"PNG · {FormatBytes(png.Length)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ClearVisionInput($"Vision preview failed: {ex.Message}");
        }
    }

    private void ClearVisionInput(string message)
    {
        VisionInputImage.Source = null;
        VisionInputPlaceholder.Text = message;
        VisionInputPlaceholder.Visibility = Visibility.Visible;
        VisionInputMetaText.Text = "NONE";
    }

    private static string FormatBytes(int bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576d:0.0} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes / 1024)} KB");

    private static BitmapImage CreateBitmapImage(byte[] bytes)
    {
        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }

    private static BitmapSource CreateBitmapImageFromFile(string path)
    {
        // 1. Try decoding via SkiaSharp first (natively supports WEBP, PNG, JPG, BMP, GIF, ICO)
        try
        {
            using var stream = File.OpenRead(path);
            using var skBitmap = SKBitmap.Decode(stream);
            if (skBitmap != null && skBitmap.Width > 0 && skBitmap.Height > 0)
            {
                var writeable = skBitmap.ToWriteableBitmap();
                writeable.Freeze();
                return writeable;
            }
        }
        catch { }

        // 2. Fallback to WPF BitmapImage
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { }



        throw new InvalidOperationException($"The file '{System.IO.Path.GetFileName(path)}' could not be decoded as an image.");
    }

    private static byte[]? BuildVisionPng(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using SKBitmap? source = SKBitmap.Decode(path);
            if (source is not null && source.Width > 0 && source.Height > 0)
            {
                double scale = Math.Min(1d, MaxVisionEdge / (double)Math.Max(source.Width, source.Height));
                width = Math.Max(1, (int)Math.Round(source.Width * scale));
                height = Math.Max(1, (int)Math.Round(source.Height * scale));

                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var resized = new SKBitmap(info);
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.Clear(SKColors.Black);
                    using SKImage sourceImage = SKImage.FromBitmap(source);
                    using var paint = new SKPaint
                    {
                        FilterQuality = SKFilterQuality.High,
                        IsAntialias = true
                    };
                    canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), paint);
                }

                using SKImage output = SKImage.FromBitmap(resized);
                using SKData? data = output.Encode(SKEncodedImageFormat.Png, 90);
                if (data is not null && data.Size > 0)
                    return data.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }



        // Final fallback: raw file bytes if valid file
        try
        {
            if (System.IO.File.Exists(path))
            {
                byte[] raw = System.IO.File.ReadAllBytes(path);
                if (raw.Length > 16)
                {
                    width = 1000;
                    height = 500;
                    return raw;
                }
            }
        }
        catch { }

        return null;
    }

    // ---------------------------------------------------------------------
    // Overlay Rendering (Live & Photo Overlays)
    // ---------------------------------------------------------------------

    private void RenderOverlays()
    {
        LiveOverlayCanvas.Children.Clear();
        PhotoOverlayCanvas.Children.Clear();

        bool showBoxes = ShowBoxesCheckBox.IsChecked == true;
        bool showPins = ShowPinsCheckBox.IsChecked == true;
        bool showFaults = ShowFaultsCheckBox.IsChecked == true;

        // Only the known synthetic board or a homography-locked real board may
        // carry board geometry. An uncalibrated camera frame is just an image.
        bool isPhysicalOrMedia = _coordinator is not null && _coordinator.ActiveCameraSource != CameraSource.Synthetic;
        bool isBoardCalibrated = _coordinator?.IsBoardCalibrated == true || _coordinator?.IsMarkerlessCalibrationActive == true;

        bool liveOverlayAllowed = _coordinator?.SupportsLiveOverlay == true;
        if (isPhysicalOrMedia && !isBoardCalibrated)
        {
            liveOverlayAllowed = false;
        }
        UpdateLiveOverlayState(liveOverlayAllowed);

        if (liveOverlayAllowed && _livePacket is not null && _viewMode != StudioViewMode.PhotoOnly)
        {
            if (showBoxes)
                DrawComponentBoxes(_livePacket, LiveOverlayCanvas);
            if (showPins)
                DrawPinDots(_livePacket, LiveOverlayCanvas);
            if (showFaults)
                DrawFaults(_livePacket, LiveOverlayCanvas);
        }

        if (_photoPacket is not null && _viewMode != StudioViewMode.LiveOnly)
        {
            if (showBoxes)
                DrawComponentBoxes(_photoPacket, PhotoOverlayCanvas);
            if (showPins)
                DrawPinDots(_photoPacket, PhotoOverlayCanvas);
            if (showFaults)
                DrawFaults(_photoPacket, PhotoOverlayCanvas);
        }
    }

    private void UpdateLiveOverlayState(bool allowed)
    {
        LiveOverlayStateText.Text = "AI DIRECT VISION";
        LiveOverlayStateText.Foreground = (Brush)FindResource("AccentBlueBrush");
        LiveOverlayNotice.Visibility = Visibility.Collapsed;
    }

    private void DrawComponentBoxes(DrcStatePacket packet, Canvas canvas)
    {
        int count = Math.Min(packet.Components.Count, 40);
        for (int i = 0; i < count; i++)
        {
            SnappedComponent component = packet.Components[i];
            DetectedComponent raw = component.RawDetection;

            double x = raw.BoxX;
            double y = raw.BoxY;
            double w = raw.BoxW;
            double h = raw.BoxH;

            if (w <= 0 || h <= 0)
            {
                x = Math.Min(component.Pin1Hole.Px, component.Pin2Hole.Px) - 14;
                y = Math.Min(component.Pin1Hole.Py, component.Pin2Hole.Py) - 12;
                w = Math.Abs(component.Pin1Hole.Px - component.Pin2Hole.Px) + 28;
                h = Math.Abs(component.Pin1Hole.Py - component.Pin2Hole.Py) + 24;
            }

            bool faulty = IsFaulty(component, packet);
            Brush stroke = faulty
                ? (Brush)FindResource("DrcCriticalBrush")
                : (Brush)FindResource("AccentBlueBrush");

            var box = new Rectangle
            {
                Width = Math.Max(10, w),
                Height = Math.Max(10, h),
                Stroke = stroke,
                StrokeThickness = 1.5,
                // Only a fault gets a wash; a healthy part stays an open bracket
                // so the camera image is never hidden behind a dark slab.
                Fill = faulty
                    ? new SolidColorBrush(Color.FromArgb(0x24, 0xD7, 0x37, 0x3F))
                    : Brushes.Transparent,
                RadiusX = 0,
                RadiusY = 0
            };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            canvas.Children.Add(box);

            string label = string.IsNullOrWhiteSpace(raw.DecodedValueLabel)
                ? $"{raw.Kind} #{raw.TrackId}"
                : raw.DecodedValueLabel!;

            var badge = new Border
            {
                Background = (Brush)FindResource("PanelBackgroundBrush"),
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = $"{label}  {component.Pin1Hole.HoleId}-{component.Pin2Hole.HoleId}",
                    FontSize = 8.5,
                    FontFamily = (FontFamily)FindResource("MonoFontFamily"),
                    Foreground = stroke
                }
            };
            Canvas.SetLeft(badge, Math.Clamp(x, 0, Math.Max(0, canvas.Width - 120)));
            Canvas.SetTop(badge, Math.Max(0, y - 18));
            canvas.Children.Add(badge);
        }
    }

    private void DrawPinDots(DrcStatePacket packet, Canvas canvas)
    {
        int count = Math.Min(packet.Components.Count, 40);
        for (int i = 0; i < count; i++)
        {
            SnappedComponent component = packet.Components[i];
            DrawPinDot(component.Pin1Hole.Px, component.Pin1Hole.Py, component.Pin1Hole.HoleId, canvas);
            DrawPinDot(component.Pin2Hole.Px, component.Pin2Hole.Py, component.Pin2Hole.HoleId, canvas);

            if (!component.IsMultiPin)
                continue;

            for (int p = 0; p < component.ExtraPins.Length; p++)
            {
                HoleCoord hole = component.ExtraPins.Span[p].Hole;
                DrawPinDot(hole.Px, hole.Py, hole.HoleId, canvas);
            }
        }
    }

    private void DrawPinDot(double px, double py, string holeId, Canvas canvas)
    {
        const double size = 6;
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = (Brush)FindResource("DrcPassBrush"),
            Stroke = (Brush)FindResource("TextInverseBrush"),
            StrokeThickness = 1
        };
        Canvas.SetLeft(dot, px - size / 2);
        Canvas.SetTop(dot, py - size / 2);
        canvas.Children.Add(dot);

        var label = new TextBlock
        {
            Text = holeId,
            FontSize = 7.5,
            FontFamily = (FontFamily)FindResource("MonoFontFamily"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Background = (Brush)FindResource("PanelBackgroundBrush")
        };
        Canvas.SetLeft(label, px + 5);
        Canvas.SetTop(label, py - 6);
        canvas.Children.Add(label);
    }

    private void DrawFaults(DrcStatePacket packet, Canvas canvas)
    {
        int count = Math.Min(packet.Violations.Count, 20);
        for (int i = 0; i < count; i++)
        {
            DrcViolation violation = packet.Violations[i];
            double badX = violation.BadHole.Px;
            double badY = violation.BadHole.Py;

            if (badX <= 0 && badY <= 0)
                continue;

            DrawFaultCrosshair(badX, badY, canvas);

            if (!string.IsNullOrWhiteSpace(violation.SuggestedTargetHole.HoleId))
            {
                DrawRatsnestArrow(
                    badX,
                    badY,
                    violation.SuggestedTargetHole.Px,
                    violation.SuggestedTargetHole.Py,
                    violation.SuggestedTargetHole.HoleId,
                    canvas);
            }
        }
    }

    private void DrawFaultCrosshair(double px, double py, Canvas canvas)
    {
        const double radius = 11;
        Brush critical = (Brush)FindResource("DrcCriticalBrush");

        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = critical,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xD7, 0x37, 0x3F))
        };
        Canvas.SetLeft(ring, px - radius);
        Canvas.SetTop(ring, py - radius);
        canvas.Children.Add(ring);

        canvas.Children.Add(new Line
        {
            X1 = px - radius - 4,
            Y1 = py,
            X2 = px + radius + 4,
            Y2 = py,
            Stroke = critical,
            StrokeThickness = 1.5
        });

        canvas.Children.Add(new Line
        {
            X1 = px,
            Y1 = py - radius - 4,
            X2 = px,
            Y2 = py + radius + 4,
            Stroke = critical,
            StrokeThickness = 1.5
        });
    }

    private void DrawRatsnestArrow(double x1, double y1, double x2, double y2, string targetHole, Canvas canvas)
    {
        Brush pass = (Brush)FindResource("DrcPassBrush");

        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 2)
            return;

        double ux = dx / length;
        double uy = dy / length;

        // Keep the dashed run clear of the fault crosshair and the target ring.
        const double startGap = 16;
        const double endGap = 9;
        double sx = x1 + (ux * startGap);
        double sy = y1 + (uy * startGap);
        double ex = x2 - (ux * endGap);
        double ey = y2 - (uy * endGap);

        canvas.Children.Add(new Line
        {
            X1 = sx,
            Y1 = sy,
            X2 = ex,
            Y2 = ey,
            Stroke = pass,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        });

        // Arrowhead pointing at the socket the part must move to.
        double angle = Math.Atan2(dy, dx);
        const double headLength = 9;
        const double headSpread = 0.42;
        for (int side = -1; side <= 1; side += 2)
        {
            double a = angle + Math.PI + (side * headSpread);
            canvas.Children.Add(new Line
            {
                X1 = ex,
                Y1 = ey,
                X2 = ex + (headLength * Math.Cos(a)),
                Y2 = ey + (headLength * Math.Sin(a)),
                Stroke = pass,
                StrokeThickness = 2
            });
        }

        var target = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = pass,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x2D, 0x9D, 0x78))
        };
        Canvas.SetLeft(target, x2 - 6);
        Canvas.SetTop(target, y2 - 6);
        canvas.Children.Add(target);

        // Label rides beside the midpoint of the run, flipped when needed and
        // clamped so it can never leave the 1000 x 500 board canvas.
        string text = $"MOVE {targetHole}";
        const double fontSize = 8.5;
        double labelW = (text.Length * fontSize * 0.62) + 12;
        double labelH = 17;
        double midX = (sx + ex) / 2;
        double midY = (sy + ey) / 2;
        double sideX = -uy;
        double sideY = ux;

        double lx = midX + (sideX * 18) - (labelW / 2);
        double ly = midY + (sideY * 18) - (labelH / 2);

        if (lx < 2 || lx + labelW > canvas.Width - 2)
            lx = midX - (sideX * 18) - (labelW / 2);

        lx = Math.Clamp(lx, 2, Math.Max(2, canvas.Width - labelW - 2));
        ly = Math.Clamp(ly, 2, Math.Max(2, canvas.Height - labelH - 2));

        var label = new Border
        {
            Background = (Brush)FindResource("PanelBackgroundBrush"),
            BorderBrush = pass,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontFamily = (FontFamily)FindResource("MonoFontFamily"),
                Foreground = pass
            }
        };
        Canvas.SetLeft(label, lx);
        Canvas.SetTop(label, ly);
        canvas.Children.Add(label);
    }

    private static bool IsFaulty(SnappedComponent component, DrcStatePacket packet)
    {
        string label = component.RawDetection.DecodedValueLabel ?? string.Empty;
        foreach (DrcViolation violation in packet.Violations)
        {
            if ((label.Length > 0
                 && string.Equals(violation.ComponentId, label, StringComparison.OrdinalIgnoreCase))
                || violation.OffendingStripId == component.Pin1Hole.StripId
                || violation.OffendingStripId == component.Pin2Hole.StripId)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildVoiceInstruction(string answer)
    {
        string cleaned = answer.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return CapSentences(cleaned, 5);
    }

    /// <summary>
    /// Keeps the first <paramref name="maxSentences"/> sentences. Decimals such as
    /// 3.3V are not treated as sentence ends, so resistor and voltage values stay
    /// intact when the instruction is spoken.
    /// </summary>
    private static string CapSentences(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text) || maxSentences <= 0)
            return string.Empty;

        string trimmed = text.Trim();
        int sentences = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c != '.' && c != '!' && c != '?')
                continue;

            if (c == '.' && i > 0 && i + 1 < trimmed.Length
                && char.IsDigit(trimmed[i - 1]) && char.IsDigit(trimmed[i + 1]))
            {
                continue;
            }

            sentences++;
            if (sentences < maxSentences)
                continue;

            int end = i + 1;
            while (end < trimmed.Length && "\"')]".IndexOf(trimmed[end]) >= 0)
                end++;

            return trimmed[..end].Trim();
        }

        return trimmed;
    }

    // ---------------------------------------------------------------------
    // AI Studio Voice & ElevenLabs Management
    // ---------------------------------------------------------------------

    private void InitializeVoiceControls()
    {
        _voiceSelectionSilent = true;
        try
        {
            ElevenLabsSettings saved = VoiceSettingsStore.LoadElevenLabs();
            var voices = new List<ElevenLabsVoice>(ElevenLabsVoiceClient.DefaultVoices);

            if (saved.HasVoice && !voices.Any(v => v.VoiceId == saved.VoiceId))
            {
                voices.Insert(0, new ElevenLabsVoice(
                    saved.VoiceId,
                    saved.VoiceName.Length > 0 ? saved.VoiceName : saved.VoiceId,
                    "account",
                    string.Empty));
            }

            if (ElevenLabsVoiceComboBox is not null)
            {
                ElevenLabsVoiceComboBox.ItemsSource = voices;
                ElevenLabsVoice? selected = voices.FirstOrDefault(v => v.VoiceId == saved.VoiceId) ?? voices.FirstOrDefault();
                ElevenLabsVoiceComboBox.SelectedItem = selected;
            }

            VoiceEnginePreference currentPref = VoiceSettingsStore.Load();
            string engineKey = VoiceEnginePreferences.ToKey(currentPref);

            if (VoiceEngineComboBox is not null)
            {
                bool matched = false;
                foreach (ComboBoxItem item in VoiceEngineComboBox.Items)
                {
                    if (string.Equals(item.Tag as string, engineKey, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsSelected = true;
                        matched = true;
                        break;
                    }
                }

                if (!matched && VoiceEngineComboBox.Items.Count > 0)
                {
                    VoiceEngineComboBox.SelectedIndex = 0;
                }
            }

            if (ElevenLabsConfigRow is not null)
            {
                ElevenLabsConfigRow.Visibility = (currentPref == VoiceEnginePreference.ElevenLabs || currentPref == VoiceEnginePreference.Auto)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (ElevenLabsApiKeyBox is not null && saved.HasKey)
            {
                ElevenLabsApiKeyBox.Password = saved.ApiKey;
            }
            if (ElevenLabsKeyHintText is not null)
            {
                ElevenLabsKeyHintText.Visibility = saved.HasKey ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ignore voice initialization faults so studio always opens cleanly.
        }
        finally
        {
            _voiceSelectionSilent = false;
            if (VoiceEngineComboBox is not null)
            {
                VoiceEngineComboBox.SelectionChanged -= OnVoiceEngineSelectionChanged;
                VoiceEngineComboBox.SelectionChanged += OnVoiceEngineSelectionChanged;
            }
            if (ElevenLabsVoiceComboBox is not null)
            {
                ElevenLabsVoiceComboBox.SelectionChanged -= OnElevenLabsVoiceSelectionChanged;
                ElevenLabsVoiceComboBox.SelectionChanged += OnElevenLabsVoiceSelectionChanged;
            }
        }
    }

    private void OnVoiceEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _voiceSelectionSilent || VoiceEngineComboBox?.SelectedItem is not ComboBoxItem selectedItem)
            return;

        string? key = selectedItem.Tag as string;
        VoiceEnginePreference pref = VoiceEnginePreferences.Parse(key);

        if (ElevenLabsConfigRow is not null)
        {
            ElevenLabsConfigRow.Visibility = (pref == VoiceEnginePreference.ElevenLabs || pref == VoiceEnginePreference.Auto)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        VoiceSettingsStore.Save(pref);
        _ = _coordinator?.SetVoiceBackendAsync(pref);
        UpdateVoiceBadge();
    }

    private void OnElevenLabsVoiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _voiceSelectionSilent || ElevenLabsVoiceComboBox?.SelectedItem is not ElevenLabsVoice voice)
            return;

        ElevenLabsSettings current = VoiceSettingsStore.LoadElevenLabs();
        var updated = new ElevenLabsSettings(current.ApiKey, voice.VoiceId, voice.Name, current.ModelId);
        VoiceSettingsStore.SaveElevenLabs(updated);

        _ = _coordinator?.ApplyElevenLabsVoiceAsync(updated.ApiKey, voice.VoiceId, updated.ModelId, voice.Name);
        UpdateVoiceBadge();
    }

    private async void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (ElevenLabsApiKeyBox is null)
            return;

        string key = ElevenLabsApiKeyBox.Password?.Trim() ?? string.Empty;
        if (!ElevenLabsVoiceClient.HasApiKey(key))
        {
            if (ElevenLabsKeyHintText is not null)
            {
                ElevenLabsKeyHintText.Text = "Please enter a valid ElevenLabs API key (at least 8 characters).";
                ElevenLabsKeyHintText.Visibility = Visibility.Visible;
            }
            return;
        }

        ElevenLabsSettings current = VoiceSettingsStore.LoadElevenLabs();
        var updated = new ElevenLabsSettings(key, current.VoiceId, current.VoiceName, current.ModelId);
        VoiceSettingsStore.SaveElevenLabs(updated);

        if (ElevenLabsKeyHintText is not null)
            ElevenLabsKeyHintText.Visibility = Visibility.Collapsed;

        BottomStatusText.Text = "ElevenLabs API key saved! Fetching account voices...";
        if (_coordinator is not null)
        {
            try
            {
                IReadOnlyList<ElevenLabsVoice> voices = await _coordinator.ListElevenLabsVoicesAsync(key);
                if (voices.Count > 0)
                {
                    _voiceSelectionSilent = true;
                    if (ElevenLabsVoiceComboBox is not null)
                    {
                        ElevenLabsVoiceComboBox.ItemsSource = voices;
                        ElevenLabsVoice? selected = voices.FirstOrDefault(v => v.VoiceId == current.VoiceId) ?? voices[0];
                        ElevenLabsVoiceComboBox.SelectedItem = selected;
                    }
                    _voiceSelectionSilent = false;
                    _ = _coordinator.ApplyElevenLabsVoiceAsync(key, voices[0].VoiceId, current.ModelId, voices[0].Name);
                    BottomStatusText.Text = $"Loaded {voices.Count} ElevenLabs voices. Active: {voices[0].Name}";
                }
            }
            catch (Exception ex)
            {
                BottomStatusText.Text = $"Failed to fetch custom voices: {ex.Message}";
            }
        }
    }

    private async void OnRefreshVoicesClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null)
            return;

        ElevenLabsSettings saved = VoiceSettingsStore.LoadElevenLabs();
        if (!saved.HasKey)
        {
            BottomStatusText.Text = "No ElevenLabs API key found. Enter key in Main Workstation or set ELEVENLABS_API_KEY.";
            return;
        }

        RefreshVoicesButton.IsEnabled = false;
        try
        {
            BottomStatusText.Text = "Fetching custom voices from ElevenLabs account...";
            IReadOnlyList<ElevenLabsVoice> voices = await _coordinator.ListElevenLabsVoicesAsync(saved.ApiKey);
            if (voices.Count > 0)
            {
                _voiceSelectionSilent = true;
                if (ElevenLabsVoiceComboBox is not null)
                {
                    ElevenLabsVoiceComboBox.ItemsSource = voices;
                    ElevenLabsVoiceComboBox.SelectedItem = voices.FirstOrDefault(v => v.VoiceId == saved.VoiceId) ?? voices[0];
                }
                _voiceSelectionSilent = false;
                BottomStatusText.Text = $"Loaded {voices.Count} ElevenLabs voices.";
            }
            else
            {
                BottomStatusText.Text = "No custom voices returned from ElevenLabs; keeping standard voices.";
            }
        }
        catch (Exception ex)
        {
            BottomStatusText.Text = $"Failed to refresh ElevenLabs voices: {ex.Message}";
        }
        finally
        {
            RefreshVoicesButton.IsEnabled = true;
        }
    }

    private void OnTestVoiceClick(object sender, RoutedEventArgs e)
    {
        _ = SpeakResultAsync("Antigravity AI tutor ready. ElevenLabs voice test successful.", _analysisGeneration, force: true);
    }
}