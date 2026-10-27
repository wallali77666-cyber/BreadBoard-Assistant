// ============================================================================
// FILE: src/5_BreadboardAOI.App/Orchestration/InspectionPipelineCoordinator.cs
// PURPOSE: Owns the dual-loop AOI workstation.
//          Loop 1 (E-Cores): 60 FPS capture -> homography -> hand gate -> YOLO ->
//          Union-Find DRC -> CAD frame/state publication. No LLM or TTS work.
//          Loop 2 (P-Cores): Qwen3-VL-30B-A3B / rule-based tutor -> sentence
//          splitter -> Kokoro / SAPI speech, connected only by a 1-slot channel.
//
// PORTABILITY: affinity is best-effort, the YOLO model is optional, Qwen is
// optional and the voice engine degrades to SAPI or silence, so the app runs on
// any Windows laptop.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;
using BreadboardAOI.Vision;
using SkiaSharp;

namespace BreadboardAOI.App.Orchestration;

/// <summary>Faults injected by the toolbar's Demo Mode selector.</summary>
public enum DemoScenario
{
    None = 0,
    SameStripLedShort = 1,
    VccGndShort = 2,
    FloatingLead = 3,
    Valid555Blinker = 4
}

/// <summary>Deterministic, pre-built detection sets used by Demo Mode.</summary>
public static class DemoScenarioCatalog
{
    private static readonly DetectedComponent[] SameStripLedShortDetections = BuildSameStripLedShort();
    private static readonly DetectedComponent[] VccGndShortDetections = BuildVccGndShort();
    private static readonly DetectedComponent[] FloatingLeadDetections = BuildFloatingLead();
    private static readonly DetectedComponent[] Valid555Detections = BuildValid555();

    public static IReadOnlyList<DemoScenario> All { get; } = new[]
    {
        DemoScenario.SameStripLedShort,
        DemoScenario.VccGndShort,
        DemoScenario.FloatingLead,
        DemoScenario.Valid555Blinker
    };

    public static string GetTitle(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => "Same-Strip LED Short (Row 15)",
        DemoScenario.VccGndShort => "Direct VCC-GND Jumper Short",
        DemoScenario.FloatingLead => "Floating Resistor Lead",
        DemoScenario.Valid555Blinker => "Valid 555 Astable Blinker",
        _ => "No Demo Scenario"
    };

    public static string GetDescription(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => "LED legs in A15 and D15 share metal clip 115: ERC 101 with a corrective arrow to D16.",
        DemoScenario.VccGndShort => "A jumper bridges TOP_VCC to TOP_GND: ERC 100 critical short plus the emergency reflex alarm.",
        DemoScenario.FloatingLead => "A 220R resistor sits on A30 while its return lead is grounded: ERC 102 floating lead.",
        DemoScenario.Valid555Blinker => "NE555 astable wired to the reference netlist: DRC pass, target ratsnest and tutor praise.",
        _ => "Live YOLO detections from the selected camera source."
    };

    /// <summary>Shared immutable arrays: the 60 FPS loop never rebuilds demo detections.</summary>
    public static DetectedComponent[] GetDetections(DemoScenario scenario) => scenario switch
    {
        DemoScenario.SameStripLedShort => SameStripLedShortDetections,
        DemoScenario.VccGndShort => VccGndShortDetections,
        DemoScenario.FloatingLead => FloatingLeadDetections,
        DemoScenario.Valid555Blinker => Valid555Detections,
        _ => Array.Empty<DetectedComponent>()
    };

    public static bool UsesTargetSchematic(DemoScenario scenario) => scenario == DemoScenario.Valid555Blinker;

    private static DetectedComponent[] BuildSameStripLedShort() =>
    [
        Part(1, ComponentKind.Led, Hole("A15"), Hole("D15"), 0.96f, "LED_RED_5MM")
    ];

    private static DetectedComponent[] BuildVccGndShort() =>
    [
        Part(1, ComponentKind.JumperWire, Hole("TOP_VCC_25"), Hole("TOP_GND_25"), 0.99f, null)
    ];

    private static DetectedComponent[] BuildFloatingLead() =>
    [
        Part(1, ComponentKind.Resistor, Hole("A30"), Hole("D33"), 0.94f, "220R"),
        Part(2, ComponentKind.JumperWire, Hole("D33"), Hole("TOP_GND_10"), 0.98f, null)
    ];

    private static DetectedComponent[] BuildValid555() =>
    [
        // VCC distribution: rail -> U1 pin 8 -> R1 top -> target net holes.
        Part(1, ComponentKind.JumperWire, Hole("TOP_VCC_25"), Hole("E20"), 0.99f, null),
        Part(2, ComponentKind.JumperWire, Hole("E20"), Hole("F23"), 0.99f, null),
        Part(3, ComponentKind.JumperWire, Hole("F23"), Hole("A18"), 0.99f, null),

        // GND distribution: rail -> U1 pin 1 -> C1 return -> spare ground columns.
        Part(4, ComponentKind.JumperWire, Hole("TOP_GND_25"), Hole("F20"), 0.99f, null),
        Part(5, ComponentKind.JumperWire, Hole("F20"), Hole("J25"), 0.99f, null),
        Part(6, ComponentKind.JumperWire, Hole("J25"), Hole("J24"), 0.99f, null),
        Part(7, ComponentKind.JumperWire, Hole("J24"), Hole("J16"), 0.99f, null),

        // Discharge and trigger/threshold nets.
        Part(8, ComponentKind.JumperWire, Hole("E21"), Hole("B24"), 0.99f, null),
        Part(9, ComponentKind.JumperWire, Hole("F21"), Hole("E22"), 0.99f, null),
        Part(10, ComponentKind.JumperWire, Hole("E22"), Hole("D25"), 0.99f, null),

        // Placed parts: U1 + timing network (C2 omitted: E23 has no second terminal).
        Part(11, ComponentKind.IntegratedCircuit, Hole("E20"), Hole("F20"), 0.98f, "NE555P"),
        Part(12, ComponentKind.Resistor, Hole("A18"), Hole("B24"), 0.97f, "10K"),
        Part(13, ComponentKind.Resistor, Hole("B24"), Hole("D25"), 0.96f, "100K"),
        Part(14, ComponentKind.Capacitor, Hole("D25"), Hole("J25"), 0.95f, "10uF")
    ];

    private static DetectedComponent Part(
        int trackId,
        ComponentKind kind,
        HoleCoord pin1,
        HoleCoord pin2,
        float confidence,
        string? decodedValue)
    {
        const float padding = 12f;

        float width = MathF.Max(18f, MathF.Abs(pin2.Px - pin1.Px) + padding * 2f);
        float height = MathF.Max(18f, MathF.Abs(pin2.Py - pin1.Py) + padding * 2f);

        float left = Math.Clamp(
            MathF.Min(pin1.Px, pin2.Px) - padding,
            0f,
            BreadboardSpec.CanvasWidth - width);

        float top = Math.Clamp(
            MathF.Min(pin1.Py, pin2.Py) - padding,
            0f,
            BreadboardSpec.CanvasHeight - height);

        return new DetectedComponent(
            TrackId: trackId,
            Kind: kind,
            Confidence: confidence,
            BoxX: left,
            BoxY: top,
            BoxW: width,
            BoxH: height,
            Lead1Px: pin1.Px,
            Lead1Py: pin1.Py,
            Lead2Px: pin2.Px,
            Lead2Py: pin2.Py,
            DecodedValueLabel: decodedValue);
    }

    private static HoleCoord Hole(string holeId) =>
        PinMatrixLookup.Shared.TryGetHole(holeId, out HoleCoord hole)
            ? hole
            : new HoleCoord(holeId, 0, 0f, 0f);
}

/// <summary>Workstation boot configuration. Defaults match the Phase 5 demo launch.</summary>
public sealed class InspectionPipelineOptions
{
    public bool DemoModeEnabled { get; init; } = true;

    public DemoScenario InitialDemoScenario { get; init; } = DemoScenario.SameStripLedShort;

    public CameraSource CameraSource { get; init; } = CameraSource.Synthetic;

    /// <summary>Still image replayed when <see cref="CameraSource"/> is <see cref="CameraSource.ImageFile"/>.</summary>
    public string? ImagePath { get; init; }

    public bool EnableCognitiveTutor { get; init; } = true;

    public bool EnableVoice { get; init; } = true;

    public bool EnableAffinity { get; init; } = true;

    public bool EnableCameraPreviewBitmap { get; init; } = true;

    public double TargetFramesPerSecond { get; init; } = 60d;

    public string? TargetSchematicPath { get; init; }

    /// <summary>Colab tunnel endpoint; when set the cognitive loop streams from it first.</summary>
    public RemoteTutorTarget? RemoteTutor { get; init; }
}

/// <summary>
/// Live dual-loop coordinator consumed by the Phase 5B view-model. All public
/// properties are safe to read from the WPF dispatcher; only immutable packets
/// cross the loop boundary.
/// </summary>
public sealed class InspectionPipelineCoordinator : IDisposable
{
    private readonly InspectionPipelineOptions _options;
    private readonly PinMatrixLookup _pinMatrix;
    private readonly ElectricalRulesChecker _drc;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cameraSync = new();
    private readonly object _bitmapSync = new();

    private readonly Channel<DrcStatePacket> _cognitiveChannel =
        Channel.CreateBounded<DrcStatePacket>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private readonly HomographyCalibrator _calibrator = new();
    private readonly HandMotionGate _motionGate = new();
    private readonly MicroCropExtractor _cropExtractor = new();
    private readonly VramBudgetMonitor _vramMonitor = new();
    private readonly List<DetectedComponent> _yoloDetections = new(32);
    private readonly OpenCvSharp.Mat _cameraFrame = new();
    private readonly OpenCvSharp.Mat _warpedFrame = new();
    private readonly SKBitmap _boardBitmap;

    private CameraCaptureService? _camera;
    private YoloPoseInferenceEngine? _yolo;
    private QwenMoEService? _qwen;
    private VoiceSynthesisEngine? _voice;
    private TargetSchematic? _targetSchematic;

    /// <summary>Guards live tutor swaps; retired services are disposed with the coordinator.</summary>
    private readonly object _qwenSync = new();
    private readonly List<QwenMoEService> _retiredQwen = new(4);
    private RemoteTutorTarget? _remoteTarget;
    private int _remoteActive;

    private Task? _visionTask;
    private Task? _cognitiveTask;
    private bool _started;
    private bool _disposed;

    private int _demoEnabled;
    private int _scenario;
    private int _activeCameraSource;
    private int _lastStateHash = int.MinValue;
    private string _cameraNote = string.Empty;
    private string _cameraImagePath = string.Empty;
    private string _targetNote = string.Empty;
    private string _yoloNote = string.Empty;
    private string _voiceNote = string.Empty;
    private DrcStatePacket? _latestPacket;
    private IReadOnlyList<RatsnestVector> _latestRatsnest = Array.Empty<RatsnestVector>();
    private double _lastDrcMs;
    private double _lastYoloMs;
    private long _frameCount;
    private int _tutorBusy;
    private int _voiceMuted;

    public InspectionPipelineCoordinator(InspectionPipelineOptions? options = null)
    {
        _options = options ?? new InspectionPipelineOptions();
        _pinMatrix = PinMatrixLookup.Shared;
        _drc = new ElectricalRulesChecker(_pinMatrix);

        _demoEnabled = _options.DemoModeEnabled ? 1 : 0;
        _scenario = (int)_options.InitialDemoScenario;

        _boardBitmap = new SKBitmap(
            BreadboardSpec.CanvasWidth,
            BreadboardSpec.CanvasHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque);
        _boardBitmap.Erase(new SKColor(0xE9, 0xE7, 0xE2));

        TryCreateCamera(_options.CameraSource);

        try
        {
            _yolo = new YoloPoseInferenceEngine();
            _yoloNote = _yolo.LastError ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _yolo = null;
            _yoloNote = $"YOLO engine unavailable: {ex.Message}";
        }

        try
        {
            _vramMonitor.TryInitialize();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // NVML is optional telemetry; the UI shows NO NVIDIA GPU instead.
        }

        LoadTargetSchematic();

        if (_options.EnableCognitiveTutor)
        {
            try
            {
                _remoteTarget = _options.RemoteTutor;
                _remoteActive = _remoteTarget is null ? 0 : 1;
                _qwen = CreateQwenService(_remoteTarget);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _qwen = null;
            }
        }

        if (_options.EnableVoice)
        {
            try
            {
                _voice = new VoiceSynthesisEngine();
                _voiceNote = _voice.LastError ?? string.Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _voice = null;
                _voiceNote = $"Voice engine unavailable: {ex.Message}";
            }
        }
    }

    /// <summary>60 FPS reflex packet published whenever the topology changes.</summary>
    public event Action<DrcStatePacket>? ReflexDrcUpdated;

    /// <summary>Streamed tutor tokens for the right-hand transcript dock.</summary>
    public event Action<string>? TutorTranscriptTokenReceived;

    /// <summary>One complete spoken sentence emitted by the sentence splitter.</summary>
    public event Action<string>? TutorSentenceCompleted;

    /// <summary>Full tutor answer once the stream closes.</summary>
    public event Action<string>? TutorDiagnosticCompleted;

    /// <summary>Operator-facing status line (camera/voice/demo changes and failures).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the toolbar selects another demo scenario.</summary>
    public event Action<DemoScenario>? DemoScenarioChanged;

    /// <summary>Critical short: the UI may flash the viewport in addition to the WAV alarm.</summary>
    public event Action<string>? EmergencyAlarmRaised;

    public InspectionPipelineOptions Options => _options;

    public PinMatrixLookup PinMatrix => _pinMatrix;

    public DrcStatePacket? LatestPacket => Volatile.Read(ref _latestPacket);

    public IReadOnlyList<RatsnestVector> LatestRatsnest => Volatile.Read(ref _latestRatsnest);

    /// <summary>Camera frame in 1000 x 500 BGRA for the optional preview pane.</summary>
    public SKBitmap BoardBitmap => _boardBitmap;

    public bool DemoModeEnabled => Volatile.Read(ref _demoEnabled) != 0;

    public DemoScenario ActiveScenario => (DemoScenario)Volatile.Read(ref _scenario);

    public bool IsRunning => _started;

    /// <summary>True while the cognitive loop is streaming or speaking a diagnostic.</summary>
    public bool IsTutorBusy => Volatile.Read(ref _tutorBusy) != 0;

    /// <summary>When muted the tutor still streams text, but no speech is synthesized.</summary>
    public bool VoiceMuted => Volatile.Read(ref _voiceMuted) != 0;

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public double LastDrcComputeMs => _lastDrcMs;

    public double LastYoloInferenceMs => _lastYoloMs;

    public CameraSource ActiveCameraSource => (CameraSource)Volatile.Read(ref _activeCameraSource);

    public string CameraStatus
    {
        get
        {
            string label = ActiveCameraSource switch
            {
                CameraSource.Synthetic => "SYNTHETIC BOARD",
                CameraSource.Camera0 => "CAMERA 0",
                CameraSource.Camera1 => "CAMERA 1",
                CameraSource.ImageFile => _cameraImagePath.Length == 0
                    ? "STILL IMAGE"
                    : $"IMAGE / {Path.GetFileName(_cameraImagePath).ToUpperInvariant()}",
                _ => "NO CAMERA"
            };

            return _cameraNote.Length == 0 ? label : $"{label} ({_cameraNote})";
        }
    }

    public string VisionBackendDescription
    {
        get
        {
            if (DemoModeEnabled)
                return "DEMO OVERRIDE";

            if (_yolo is null || !_yolo.IsAvailable)
                return "YOLO MODEL MISSING";

            return _yolo.UsingCuda ? "YOLO11-POSE / CUDA FP16" : "YOLO11-POSE / CPU";
        }
    }

    public string TutorBackendDescription
    {
        get
        {
            QwenMoEService? qwen = _qwen;
            if (qwen is null)
                return "TUTOR DISABLED";

            string route = UsingRemoteTutor
                ? $"COLAB / {_remoteTarget?.DisplayHost ?? "REMOTE"}"
                : "LOCAL";

            return qwen.LastMode == QwenResponseMode.Server
                ? $"QWEN3-VL / {route}"
                : $"RULE-BASED TUTOR / OFFLINE ({route})";
        }
    }

    /// <summary>True when the cognitive loop is configured to stream from Colab.</summary>
    public bool UsingRemoteTutor => Volatile.Read(ref _remoteActive) != 0;

    /// <summary>
    /// Swaps the tutor to a Colab tunnel (or back to local/offline with null).
    /// Safe to call while the reflex loop runs: the cognitive loop picks the new
    /// service up on its next packet and the old one is retired, not disposed,
    /// so an in-flight stream can finish.
    /// </summary>
    public void SetRemoteTutor(RemoteTutorTarget? target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool changed;
        lock (_qwenSync)
        {
            changed = !SameTarget(_remoteTarget, target);
            if (!changed)
                return;

            if (_qwen is not null)
                _retiredQwen.Add(_qwen);

            _remoteTarget = target;
            Volatile.Write(ref _remoteActive, target is null ? 0 : 1);

            if (!_options.EnableCognitiveTutor)
            {
                _qwen = null;
            }
            else
            {
                try
                {
                    _qwen = CreateQwenService(target);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _qwen = null;
                }
            }
        }

        RaiseStatus(target is null
            ? "Tutor backend: local llama-server / offline fallback."
            : $"Tutor backend: Colab tunnel at {target.DisplayHost}.");
    }

    private static bool SameTarget(RemoteTutorTarget? left, RemoteTutorTarget? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return string.Equals(left.EndpointUrl, right.EndpointUrl, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ApiKey, right.ApiKey, StringComparison.Ordinal)
               && string.Equals(left.ModelAlias, right.ModelAlias, StringComparison.OrdinalIgnoreCase);
    }

    private static QwenMoEService CreateQwenService(RemoteTutorTarget? target)
    {
        QwenMoEOptions baseOptions = QwenMoEOptions.FromEnvironment();
        if (target is null)
            return new QwenMoEService(baseOptions);

        return new QwenMoEService(baseOptions.WithRemoteEndpoint(
            target.ChatEndpoint,
            target.ApiKey,
            target.ModelAlias));
    }

    public string VoiceBackendDescription => _voice?.Backend switch
    {
        VoiceBackend.Kokoro => "KOKORO 82M / SHERPA-ONNX",
        VoiceBackend.Sapi => "WINDOWS SAPI FALLBACK",
        _ => "VOICE SILENT"
    };

    public string VramStatus
    {
        get
        {
            try
            {
                VramSnapshot snapshot = _vramMonitor.GetSnapshot();
                if (!snapshot.IsAvailable)
                    return "NO NVIDIA GPU";

                string suffix = snapshot.IsMocked ? " MOCK" : string.Empty;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{snapshot.UsedGigabytes:0.0} / {snapshot.TotalGigabytes:0.0} GB{suffix}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return "VRAM N/A";
            }
        }
    }

    public string AffinityStatus => CpuAffinityManager.Plan.Description;

    public string TargetSchematicStatus => _targetSchematic is null
        ? _targetNote.Length == 0 ? "TARGET NETLIST: NONE" : $"TARGET NETLIST: {_targetNote}"
        : string.Create(CultureInfo.InvariantCulture, $"TARGET NETLIST: {_targetSchematic.Name}");

    public string VisionNote => _yoloNote.Length == 0 ? string.Empty : _yoloNote;

    public string VoiceNote => _voiceNote.Length == 0 ? string.Empty : _voiceNote;

    /// <summary>Starts the reflex and cognitive loops. Safe to call twice.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        _started = true;

        _visionTask = Task.Factory.StartNew(
            () => RunReflexLoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        if (_qwen is not null && _options.EnableCognitiveTutor)
        {
            _cognitiveTask = Task.Factory.StartNew(
                () => RunCognitiveLoopAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        RaiseStatus($"Pipeline started. {CameraStatus} - {VisionBackendDescription}.");
    }

    public async Task StopAsync()
    {
        if (!_started)
            return;

        _started = false;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by the window closing.
        }

        await AwaitTaskAsync(_visionTask).ConfigureAwait(false);
        await AwaitTaskAsync(_cognitiveTask).ConfigureAwait(false);

        _visionTask = null;
        _cognitiveTask = null;
    }

    private static async Task AwaitTaskAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing loop must never block window shutdown.
        }
    }

    /// <summary>Selects a demo fault (or None for live YOLO) and enables demo mode.</summary>
    public void SetDemoScenario(DemoScenario scenario)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Volatile.Write(ref _demoEnabled, scenario == DemoScenario.None ? 0 : 1);
        ApplyScenario(scenario);
        RaiseStatus($"Demo scenario: {DemoScenarioCatalog.GetTitle(scenario)}");
    }

    /// <summary>Turns Demo Mode on/off without losing the last selected fault.</summary>
    public void SetDemoMode(bool enabled, DemoScenario? scenario = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Volatile.Write(ref _demoEnabled, enabled ? 1 : 0);

        if (scenario.HasValue)
            ApplyScenario(scenario.Value);
        else
            ResetTopology();

        RaiseStatus(enabled
            ? $"Demo mode ON: {DemoScenarioCatalog.GetTitle(ActiveScenario)}"
            : "Demo mode OFF: live YOLO detection");
    }

    private void ApplyScenario(DemoScenario scenario)
    {
        Volatile.Write(ref _scenario, (int)scenario);
        ResetTopology();
        DemoScenarioChanged?.Invoke(scenario);
    }

    private void ResetTopology()
    {
        _lastStateHash = int.MinValue;
        _motionGate.Reset();
        Volatile.Write(ref _latestRatsnest, Array.Empty<RatsnestVector>());
    }

    /// <summary>Hot-swaps the camera (or synthetic source) without stopping the loops.</summary>
    public void SetCameraSource(CameraSource source, string? imagePath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Re-selecting a still image always reloads it (the file on disk may have
        // changed); device and synthetic sources are idempotent.
        if (source == ActiveCameraSource && source != CameraSource.ImageFile && _camera is not null)
        {
            RaiseStatus($"Camera already active: {CameraStatus}");
            return;
        }

        CameraCaptureService replacement;
        try
        {
            replacement = new CameraCaptureService(source, imagePath: imagePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RaiseStatus($"Camera switch failed: {ex.Message}");
            return;
        }

        SwapCamera(replacement, imagePath);
    }

    /// <summary>Replays an uploaded still photo (letterboxed to the 1280 x 720 capture frame).</summary>
    public void SetImageSource(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        CameraCaptureService replacement;
        try
        {
            replacement = new CameraCaptureService(CameraSource.ImageFile, imagePath: imagePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RaiseStatus($"Image load failed: {ex.Message}");
            return;
        }

        SwapCamera(replacement, imagePath);
    }

    private void SwapCamera(CameraCaptureService replacement, string? imagePath)
    {
        CameraCaptureService? previous;
        lock (_cameraSync)
        {
            previous = _camera;
            _camera = replacement;
        }

        previous?.Dispose();

        Volatile.Write(ref _activeCameraSource, (int)replacement.ActiveSource);
        _cameraNote = replacement.LastError ?? string.Empty;
        _cameraImagePath = replacement.ActiveSource == CameraSource.ImageFile ? imagePath ?? string.Empty : string.Empty;

        _calibrator.ResetCalibration();
        _motionGate.Reset();
        ResetTopology();

        RaiseStatus($"Camera source: {CameraStatus}");
    }

    private void TryCreateCamera(CameraSource source)
    {
        try
        {
            _camera = new CameraCaptureService(source, imagePath: _options.ImagePath);
            Volatile.Write(ref _activeCameraSource, (int)_camera.ActiveSource);
            _cameraNote = _camera.LastError ?? string.Empty;
            _cameraImagePath = _camera.ActiveSource == CameraSource.ImageFile ? _options.ImagePath ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _camera = null;
            _cameraNote = $"Camera unavailable: {ex.Message}";
        }
    }

    private void LoadTargetSchematic()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.TargetSchematicPath))
            {
                if (TargetSchematicLoader.TryLoadFromFile(
                        _options.TargetSchematicPath!,
                        out TargetSchematic? schematic,
                        out string? error))
                {
                    _targetSchematic = schematic;
                }
                else
                {
                    _targetNote = error ?? "unknown error";
                }

                return;
            }

            _targetSchematic = TargetSchematicLoader.TryLoadDefault(out string? defaultError);
            if (_targetSchematic is null)
                _targetNote = defaultError ?? "not found";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _targetSchematic = null;
            _targetNote = ex.Message;
        }
    }

    private async Task RunReflexLoopAsync(CancellationToken cancellationToken)
    {
        if (_options.EnableAffinity)
            CpuAffinityManager.TryPinCurrentThread(AffinityRole.Vision, out _);

        double targetFrameMs = 1000d / Math.Clamp(_options.TargetFramesPerSecond, 1d, 240d);
        int calibrationCooldown = 0;
        long frame = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
            frame++;

            if (!TryCaptureWarpedFrame(ref calibrationCooldown))
            {
                await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool stable = _motionGate.Update(_warpedFrame);

            if (_options.EnableCameraPreviewBitmap && (frame & 1) == 0)
                UpdateBoardBitmap();

            if (!stable && !DemoModeEnabled)
            {
                await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            EvaluateFrame(frame);

            await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Captures + calibrates + warps under the camera lock so hot-swaps are safe.</summary>
    private bool TryCaptureWarpedFrame(ref int calibrationCooldown)
    {
        lock (_cameraSync)
        {
            if (_camera is null)
                return false;

            if (!_camera.TryGrabFrame(_cameraFrame))
                return false;

            if (!_calibrator.IsCalibrated && calibrationCooldown <= 0)
            {
                _calibrator.TryCalibrate(_cameraFrame, out _);
                calibrationCooldown = 120;
            }
            else if (calibrationCooldown > 0)
            {
                calibrationCooldown--;
            }

            return _calibrator.Warp(_cameraFrame, _warpedFrame) && !_warpedFrame.Empty();
        }
    }

    private static async Task DelayRemainingAsync(
        long frameStartTimestamp,
        double targetFrameMs,
        CancellationToken cancellationToken)
    {
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(frameStartTimestamp).TotalMilliseconds;
        double remainingMs = targetFrameMs - elapsedMs;
        if (remainingMs < 1d)
            remainingMs = 1d;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop condition observes the cancellation next.
        }
    }

    private void EvaluateFrame(long frame)
    {
        bool demo = DemoModeEnabled;
        DemoScenario scenario = ActiveScenario;

        ReadOnlySpan<DetectedComponent> detections;
        double yoloMs;

        if (demo)
        {
            detections = DemoScenarioCatalog.GetDetections(scenario);
            yoloMs = 0d;
        }
        else if (_yolo is not null)
        {
            _yolo.Infer(_warpedFrame, _yoloDetections, 1);
            detections = CollectionsMarshal.AsSpan(_yoloDetections);
            yoloMs = _yolo.LastInferenceMs;
        }
        else
        {
            _yoloDetections.Clear();
            detections = CollectionsMarshal.AsSpan(_yoloDetections);
            yoloMs = 0d;
        }

        DrcStatePacket packet = _drc.EvaluateBoard(frame, yoloMs, detections, _targetSchematic);

        Interlocked.Exchange(ref _frameCount, frame);
        _lastDrcMs = packet.DrcComputeMs;
        _lastYoloMs = yoloMs;

        if (packet.StateHash == _lastStateHash)
        {
            Volatile.Write(ref _latestPacket, packet);
            return;
        }

        _lastStateHash = packet.StateHash;
        DrcStatePacket published = AttachMicroCrop(packet);

        IReadOnlyList<RatsnestVector> ratsnest = Array.Empty<RatsnestVector>();
        if (demo && DemoScenarioCatalog.UsesTargetSchematic(scenario) && _targetSchematic is not null)
            ratsnest = TargetSchematicLoader.ComputeRatsnest(_targetSchematic, _pinMatrix, _drc);

        Volatile.Write(ref _latestRatsnest, ratsnest);
        Volatile.Write(ref _latestPacket, published);

        ReflexDrcUpdated?.Invoke(published);

        if (published.HasCriticalShort)
            RaiseEmergency(published);

        if (published.Components.Count > 0 || published.Violations.Count > 0)
            _cognitiveChannel.Writer.TryWrite(published);
    }

    /// <summary>Adds the 256 x 256 PNG crop for Qwen only when the topology changed.</summary>
    private DrcStatePacket AttachMicroCrop(DrcStatePacket packet)
    {
        if (packet.Violations.Count == 0 || packet.Components.Count == 0 || _warpedFrame.Empty())
            return packet;

        DetectedComponent target = packet.Components[0].RawDetection;
        bool found = false;

        for (int v = 0; v < packet.Violations.Count && !found; v++)
        {
            DrcViolation violation = packet.Violations[v];
            for (int c = 0; c < packet.Components.Count; c++)
            {
                SnappedComponent component = packet.Components[c];
                if (component.Pin1Hole.StripId == violation.OffendingStripId
                    || component.Pin2Hole.StripId == violation.OffendingStripId)
                {
                    target = component.RawDetection;
                    found = true;
                    break;
                }
            }
        }

        if (!_cropExtractor.TryExtractPng(_warpedFrame, target, out byte[] png) || png.Length == 0)
            return packet;

        return new DrcStatePacket
        {
            FrameNumber = packet.FrameNumber,
            YoloInferenceMs = packet.YoloInferenceMs,
            DrcComputeMs = packet.DrcComputeMs,
            StateHash = packet.StateHash,
            Components = packet.Components,
            Violations = packet.Violations,
            MicroCropPngBytes = png
        };
    }

    private void RaiseEmergency(DrcStatePacket packet)
    {
        string reason = packet.Violations.Count > 0
            ? packet.Violations[0].TechnicalMessage
            : "Critical DRC violation";

        EmergencyAlarmRaised?.Invoke(reason);

        VoiceSynthesisEngine? voice = _voice;
        if (voice is null)
            return;

        // The reflex alarm must never stall the 60 FPS loop on audio init.
        _ = Task.Run(() =>
        {
            try
            {
                voice.PlayEmergencyReflexAlarm();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A missing audio endpoint must not kill the vision loop.
            }
        });
    }

    private unsafe void UpdateBoardBitmap()
    {
        if (_warpedFrame.Empty()
            || _warpedFrame.Width != BreadboardSpec.CanvasWidth
            || _warpedFrame.Height != BreadboardSpec.CanvasHeight
            || _warpedFrame.Channels() != 3)
        {
            return;
        }

        lock (_bitmapSync)
        {
            IntPtr destination = _boardBitmap.GetPixels();
            if (destination == IntPtr.Zero)
                return;

            byte* sourceBase = (byte*)_warpedFrame.Data;
            byte* destinationBase = (byte*)destination;
            long sourceStep = _warpedFrame.Step();
            int width = _warpedFrame.Width;
            int height = _warpedFrame.Height;
            int destinationStride = _boardBitmap.RowBytes;

            for (int y = 0; y < height; y++)
            {
                byte* sourceRow = sourceBase + y * sourceStep;
                byte* destinationRow = destinationBase + y * destinationStride;

                for (int x = 0; x < width; x++)
                {
                    int sourceIndex = x * 3;
                    int destinationIndex = x * 4;
                    destinationRow[destinationIndex] = sourceRow[sourceIndex];
                    destinationRow[destinationIndex + 1] = sourceRow[sourceIndex + 1];
                    destinationRow[destinationIndex + 2] = sourceRow[sourceIndex + 2];
                    destinationRow[destinationIndex + 3] = 255;
                }
            }
        }
    }

    private async Task RunCognitiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_options.EnableAffinity)
            CpuAffinityManager.TryPinCurrentThread(AffinityRole.Ai, out _);

        try
        {
            await foreach (DrcStatePacket packet in _cognitiveChannel.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (packet.Violations.Count == 0 && packet.Components.Count == 0)
                    continue;

                Interlocked.Exchange(ref _tutorBusy, 1);
                try
                {
                    await RunTutorTurnAsync(packet, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RaiseStatus($"Tutor loop error: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _tutorBusy, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch (ChannelClosedException)
        {
            // Coordinator disposing.
        }
    }

    private async Task RunTutorTurnAsync(DrcStatePacket packet, CancellationToken cancellationToken)
    {
        QwenMoEService? qwen = _qwen;
        if (qwen is null)
            return;

        var splitter = new SentenceStreamSplitter();
        var transcript = new StringBuilder(320);

        await foreach (string token in qwen
                           .StreamDiagnosticAsync(packet, prompt: null, cancellationToken)
                           .ConfigureAwait(false))
        {
            transcript.Append(token);
            TutorTranscriptTokenReceived?.Invoke(token);

            foreach (string sentence in splitter.Push(token))
            {
                TutorSentenceCompleted?.Invoke(sentence);

                if (_voice is not null && !VoiceMuted)
                    await _voice.SpeakSentenceAsync(sentence, cancellationToken).ConfigureAwait(false);
            }
        }

        string? tail = splitter.Flush();
        if (tail is not null)
        {
            TutorSentenceCompleted?.Invoke(tail);

            if (_voice is not null && !VoiceMuted)
                await _voice.SpeakSentenceAsync(tail, cancellationToken).ConfigureAwait(false);
        }

        if (transcript.Length > 0)
            TutorDiagnosticCompleted?.Invoke(transcript.ToString());
    }

    /// <summary>Net name for the pin inspector without touching the Union-Find buffers.</summary>
    public string? ResolveNetName(int stripId)
    {
        if (stripId is BreadboardSpec.StripTopVcc or BreadboardSpec.StripBotVcc)
            return "NET_VCC";

        if (stripId is BreadboardSpec.StripTopGnd or BreadboardSpec.StripBotGnd)
            return "NET_GND";

        DrcStatePacket? packet = LatestPacket;
        if (packet is null)
            return null;

        foreach (SnappedComponent component in packet.Components)
        {
            if (component.Pin1Hole.StripId == stripId && component.Net1Name.Length > 0)
                return component.Net1Name;

            if (component.Pin2Hole.StripId == stripId && component.Net2Name.Length > 0)
                return component.Net2Name;
        }

        return null;
    }

    /// <summary>Mutes/unmutes neural TTS while leaving the streamed transcript intact.</summary>
    public void SetVoiceMuted(bool muted)
    {
        Volatile.Write(ref _voiceMuted, muted ? 1 : 0);
        RaiseStatus(muted ? "Voice muted: transcript only." : $"Voice unmuted: {VoiceBackendDescription}.");
    }

    /// <summary>Re-queues the current packet so the tutor speaks the latest fault again.</summary>
    public void ReplayLatestDiagnostic()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DrcStatePacket? packet = LatestPacket;
        if (packet is null)
        {
            RaiseStatus("Replay unavailable: no DRC packet yet.");
            return;
        }

        if (_qwen is null || _cognitiveTask is null)
        {
            RaiseStatus("Replay unavailable: cognitive tutor is disabled.");
            return;
        }

        if (!_cognitiveChannel.Writer.TryWrite(packet))
        {
            RaiseStatus("Replay skipped: tutor is still streaming.");
            return;
        }

        RaiseStatus("Replaying latest diagnostic...");
    }

    /// <summary>Toolbar self-test for the sub-10 ms emergency WAV reflex.</summary>
    public void TestEmergencyAlarm()
    {
        VoiceSynthesisEngine? voice = _voice;
        if (voice is null)
        {
            RaiseStatus("Alarm test failed: voice engine unavailable.");
            return;
        }

        try
        {
            bool played = voice.PlayEmergencyReflexAlarm();
            RaiseStatus(played
                ? "Emergency reflex alarm played."
                : "Alarm test: no pre-loaded WAV clip on disk.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RaiseStatus($"Alarm test failed: {ex.Message}");
        }
    }

    private void RaiseStatus(string message) => StatusChanged?.Invoke(message);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled by StopAsync.
        }

        try
        {
            _visionTask?.Wait(2000);
        }
        catch (AggregateException)
        {
            // Cancellation/fault is expected during teardown.
        }

        try
        {
            _cognitiveTask?.Wait(3000);
        }
        catch (AggregateException)
        {
            // Cancellation/fault is expected during teardown.
        }

        _cognitiveChannel.Writer.TryComplete();
        _cts.Dispose();

        CameraCaptureService? camera;
        lock (_cameraSync)
        {
            camera = _camera;
            _camera = null;
        }

        camera?.Dispose();
        _calibrator.Dispose();
        _motionGate.Dispose();
        _yolo?.Dispose();
        _cropExtractor.Dispose();

        lock (_qwenSync)
        {
            _qwen?.Dispose();
            _qwen = null;

            for (int i = 0; i < _retiredQwen.Count; i++)
                _retiredQwen[i].Dispose();

            _retiredQwen.Clear();
        }

        _voice?.Dispose();
        _vramMonitor.Dispose();
        _cameraFrame.Dispose();
        _warpedFrame.Dispose();
        _boardBitmap.Dispose();
    }
}
