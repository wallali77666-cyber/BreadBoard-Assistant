// ============================================================================
// FILE: src/5_BreadboardAOI.App/Orchestration/InspectionPipelineCoordinator.cs
// PURPOSE: Owns the dual-loop AOI workstation.
//          Loop 1 (E-Cores): 60 FPS capture -> homography -> hand gate -> YOLO ->
//          Union-Find DRC -> CAD frame/state publication. No LLM or TTS work.
//          Loop 2 (P-Cores): Qwen3-VL-30B-A3B / rule-based tutor -> sentence
//          splitter -> natural voice for Qwen answers only. The rule-based
//          fallback stays text-only. Connected only by a 1-slot channel.
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
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Core.KiCad;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;
using BreadboardAOI.Vision;
using SkiaSharp;

namespace BreadboardAOI.App.Orchestration;

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

    /// <summary>Requested speech engine; the engine falls back if its files are absent.</summary>
    public VoiceEnginePreference VoicePreference { get; init; } = VoiceEnginePreference.Auto;

    /// <summary>ElevenLabs key; when set (or supplied later in AI VOICE) it is the primary voice.</summary>
    public string? ElevenLabsApiKey { get; init; }

    public string? ElevenLabsVoiceId { get; init; }

    public string? ElevenLabsVoiceName { get; init; }

    public string? ElevenLabsModelId { get; init; }

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
    private readonly FastImageToKicadEngine _kicadEngine;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cameraSync = new();
    private readonly object _bitmapSync = new();

    private readonly Channel<DrcStatePacket> _cognitiveChannel =
        Channel.CreateBounded<DrcStatePacket>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly HomographyCalibrator _calibrator = new();
    private readonly HandMotionGate _motionGate = new();
    private readonly WireInsertionTracker _wireTracker = new();
    private PaddleIcPartNumberOcr? _icOcr;
    private readonly MicroCropExtractor _cropExtractor = new();
    private readonly VramBudgetMonitor _vramMonitor = new();
    private readonly List<DetectedComponent> _yoloDetections = new(32);
    private readonly List<DetectedComponent> _reconciledDetections = new(32);
    private readonly OpenCvSharp.Mat _cameraFrame = new();
    private readonly OpenCvSharp.Mat _warpedFrame = new();
    private readonly SKBitmap _boardBitmap;

    private CameraCaptureService? _camera;
    private YoloPoseInferenceEngine? _yolo;
    private QwenMoEService? _qwen;
    private VoiceSynthesisEngine? _voice;
    private TargetSchematic? _targetSchematic;

    /// <summary>Last distinct worker-loop fault, so a repeating error is reported once.</summary>
    private string? _lastLoopFault;

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
    private int _manualCircuit;
    private DetectedComponent[] _manualDetections = Array.Empty<DetectedComponent>();
    private int _activeCameraSource;
    private int _lastStateHash = int.MinValue;
    private string _cameraNote = string.Empty;
    private string _cameraImagePath = string.Empty;
    private string _targetNote = string.Empty;
    private string _yoloNote = string.Empty;
    private string _voiceNote = string.Empty;
    private DrcStatePacket? _latestPacket;
    private KicadSchematicResult? _latestSchematicResult;
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
        _kicadEngine = new FastImageToKicadEngine(_pinMatrix);

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
                VoiceOptions voiceOptions = VoiceOptions.FromEnvironment();
                if (voiceOptions.PreferredBackend == VoiceEnginePreference.Auto)
                    voiceOptions = voiceOptions.WithPreferredBackend(_options.VoicePreference);

                if (!string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceId)
                    || !string.IsNullOrWhiteSpace(_options.ElevenLabsApiKey))
                {
                    voiceOptions = voiceOptions.WithElevenLabs(
                        _options.ElevenLabsApiKey ?? voiceOptions.ElevenLabsApiKey,
                        _options.ElevenLabsVoiceId ?? voiceOptions.ElevenLabsVoiceId,
                        _options.ElevenLabsModelId ?? voiceOptions.ElevenLabsModelId,
                        _options.ElevenLabsVoiceName ?? voiceOptions.ElevenLabsVoiceName);
                }

                _voice = new VoiceSynthesisEngine(voiceOptions);
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

    /// <summary>Fired at the start of a fresh tutor diagnostic turn so callers can clear previous text.</summary>
    public event Action? TutorTurnStarted;

    /// <summary>Latency of the last tutor turn (time to first token and total).</summary>
    public event Action<TutorTurnMetrics>? TutorTurnMeasured;

    /// <summary>Operator-facing status line (camera/voice/demo changes and failures).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the toolbar selects another demo scenario.</summary>
    public event Action<DemoScenario>? DemoScenarioChanged;

    /// <summary>Critical short: the UI may flash the viewport in addition to the WAV alarm.</summary>
    public event Action<string>? EmergencyAlarmRaised;

    private int _openCvEnabled = 1;
    private int _yoloEnabled = 1;
    private int _handGateEnabled = 1;
    private int _drcEnabled = 1;
    private int _alarmEnabled = 1;
    private int _cognitiveTutorToggled = 1;

    public bool IsOpenCvEnabled
    {
        get => Volatile.Read(ref _openCvEnabled) != 0;
        set => Volatile.Write(ref _openCvEnabled, value ? 1 : 0);
    }

    public bool IsYoloEnabled
    {
        get => Volatile.Read(ref _yoloEnabled) != 0;
        set => Volatile.Write(ref _yoloEnabled, value ? 1 : 0);
    }

    private int _aiVisionModeActive;

    /// <summary>When true (e.g. in AI Live Studio), YOLO and OpenCV homography/warp are completely bypassed.</summary>
    public bool IsAiVisionModeActive
    {
        get => Volatile.Read(ref _aiVisionModeActive) != 0;
        set => Volatile.Write(ref _aiVisionModeActive, value ? 1 : 0);
    }

    public bool IsHandGateEnabled
    {
        get => Volatile.Read(ref _handGateEnabled) != 0;
        set => Volatile.Write(ref _handGateEnabled, value ? 1 : 0);
    }

    public bool IsDrcEnabled
    {
        get => Volatile.Read(ref _drcEnabled) != 0;
        set => Volatile.Write(ref _drcEnabled, value ? 1 : 0);
    }

    public bool IsAlarmEnabled
    {
        get => Volatile.Read(ref _alarmEnabled) != 0;
        set => Volatile.Write(ref _alarmEnabled, value ? 1 : 0);
    }

    public bool IsCognitiveTutorEnabled
    {
        get => Volatile.Read(ref _cognitiveTutorToggled) != 0;
        set => Volatile.Write(ref _cognitiveTutorToggled, value ? 1 : 0);
    }

    public bool IsRuleBasedAdviceEnabled
    {
        get => _qwen?.AllowRuleBasedFallback ?? false;
        set
        {
            if (_qwen is not null)
                _qwen.AllowRuleBasedFallback = value;
        }
    }

    public InspectionPipelineOptions Options => _options;

    public PinMatrixLookup PinMatrix => _pinMatrix;
    public ElectricalRulesChecker DrcEngine => _drc;
    public int GetRootNet(int stripId) => _drc.GetRootNet(stripId);

    public DrcStatePacket? LatestPacket => Volatile.Read(ref _latestPacket);

    public IReadOnlyList<RatsnestVector> LatestRatsnest => Volatile.Read(ref _latestRatsnest);

    public KicadSchematicResult? LatestSchematicResult => Volatile.Read(ref _latestSchematicResult);

    public event Action<KicadSchematicResult>? SchematicCompiled;

    public FastImageToKicadEngine KicadEngine => _kicadEngine;

    /// <summary>Exports the cached or compiled schematic to a standalone .kicad_sch file.</summary>
    public bool ExportKicadSch(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var result = LatestSchematicResult;
        if (result is null)
        {
            var packet = LatestPacket;
            if (packet is not null)
                result = _kicadEngine.CompileFromState(packet);
        }

        if (result is null || string.IsNullOrEmpty(result.KicadSchContent))
            return false;

        File.WriteAllText(filePath, result.KicadSchContent, Encoding.UTF8);
        return true;
    }

    /// <summary>Exports the cached or compiled schematic to a 2D interactive SVG diagram.</summary>
    public bool ExportSvg(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var result = LatestSchematicResult;
        if (result is null)
        {
            var packet = LatestPacket;
            if (packet is not null)
                result = _kicadEngine.CompileFromState(packet);
        }

        if (result is null || string.IsNullOrEmpty(result.SvgContent))
            return false;

        File.WriteAllText(filePath, result.SvgContent, Encoding.UTF8);
        return true;
    }

    private int _wireTrackerEnabled = 1;
    private int _icOcrEnabled = 1;
    private long _lastReconciledEpoch = -1;
    private string? _latestOcrResult;
    private IReadOnlyList<IcPartNumberResult> _latestIcOcrResults = Array.Empty<IcPartNumberResult>();
    private IReadOnlyList<TrackedWirePair> _activeTrackedWires = Array.Empty<TrackedWirePair>();

    /// <summary>Status of the WireInsertionTracker temporal + HSV wire pairing module.</summary>
    public bool IsWireTrackerAvailable => true;

    /// <summary>Status of the PaddleOCR DIP IC part-number recognizer.</summary>
    public bool IsOcrAvailable => true;

    public bool IsWireTrackerEnabled
    {
        get => Volatile.Read(ref _wireTrackerEnabled) != 0;
        set => Volatile.Write(ref _wireTrackerEnabled, value ? 1 : 0);
    }

    public bool IsIcOcrEnabled
    {
        get => Volatile.Read(ref _icOcrEnabled) != 0;
        set => Volatile.Write(ref _icOcrEnabled, value ? 1 : 0);
    }

    public void SetWireTrackerEnabled(bool enabled)
    {
        IsWireTrackerEnabled = enabled;
        if (!enabled)
        {
            _wireTracker.Reset();
            _reconciledDetections.Clear();
            _lastReconciledEpoch = -1;
            Volatile.Write(ref _activeTrackedWires, Array.Empty<TrackedWirePair>());
        }
    }

    public void SetIcOcrEnabled(bool enabled)
    {
        IsIcOcrEnabled = enabled;
        if (!enabled)
        {
            _icOcr?.ClearCache();
            Volatile.Write(ref _latestOcrResult, null);
            Volatile.Write(ref _latestIcOcrResults, Array.Empty<IcPartNumberResult>());
        }
    }

    public bool HasHandInFrame => _motionGate.HasHandInFrame;

    public bool IsHandFreeAndStable => _motionGate.IsHandFreeAndStable;

    public double HandCoverageRatio => _motionGate.HandCoverageRatio;

    public double StableElapsedMs => _motionGate.StableElapsedMs;

    public long SettledEpoch => _motionGate.SettledEpoch;

    public bool IsMarkerlessCalibrationActive => _calibrator.IsMarkerlessFallbackActive;

    public IReadOnlyList<TrackedWirePair> ActiveTrackedWires => Volatile.Read(ref _activeTrackedWires);

    public IReadOnlyList<IcPartNumberResult> LatestIcOcrResults => Volatile.Read(ref _latestIcOcrResults);

    public string? LatestOcrResult => Volatile.Read(ref _latestOcrResult);

    public string? RunOcr(byte[]? imageCrop = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsIcOcrEnabled)
            return null;

        if (imageCrop is { Length: > 0 })
        {
            using OpenCvSharp.Mat decoded = OpenCvSharp.Cv2.ImDecode(imageCrop, OpenCvSharp.ImreadModes.Color);
            if (decoded.Empty())
                return null;

            var cropComponent = new DetectedComponent(
                TrackId: 0,
                Kind: ComponentKind.IntegratedCircuit,
                Confidence: 1.0f,
                BoxX: 0,
                BoxY: 0,
                BoxW: decoded.Width,
                BoxH: decoded.Height,
                Lead1Px: 0,
                Lead1Py: 0,
                Lead2Px: decoded.Width,
                Lead2Py: decoded.Height);

            if (GetOrCreateIcOcr().TryReadPartNumber(decoded, in cropComponent, isPin1Left: true, out IcPartNumberResult result)
                && !string.IsNullOrWhiteSpace(result.CanonicalPartNumber))
            {
                Volatile.Write(ref _latestOcrResult, result.CanonicalPartNumber);
                Volatile.Write(ref _latestIcOcrResults, [result]);
                return result.CanonicalPartNumber;
            }

            return null;
        }

        return LatestOcrResult;
    }

    /// <summary>Camera frame in 1000 x 500 BGRA for the optional preview pane.</summary>
    public SKBitmap BoardBitmap => _boardBitmap;

    /// <summary>
    /// Copies the published 1000 x 500 BGRA frame into a caller-owned buffer while
    /// holding the same lock the vision thread uses. The AI Live Studio uses this
    /// instead of reading the SKBitmap directly, so a frame swap cannot tear.
    /// </summary>
    public bool CopyBoardBitmap(byte[] destination, out int stride)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_bitmapSync)
        {
            stride = _boardBitmap.RowBytes;
            int required = stride * _boardBitmap.Height;
            if (destination.Length < required)
                return false;

            Marshal.Copy(_boardBitmap.GetPixels(), destination, 0, required);
            return true;
        }
    }

    public bool DemoModeEnabled => Volatile.Read(ref _demoEnabled) != 0;

    /// <summary>
    /// True while the X-ray builder owns the netlist. Vision and demo detections
    /// are ignored so the operator's hand-placed circuit is what the DRC judges.
    /// </summary>
    public bool ManualCircuitEnabled => Volatile.Read(ref _manualCircuit) != 0;

    public int ManualPartCount => Volatile.Read(ref _manualDetections).Length;

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

    /// <summary>
    /// True once the ArUco homography has locked onto the four board markers.
    /// A real camera frame that is not calibrated must never carry demo overlays.
    /// </summary>
    public bool IsBoardCalibrated => _calibrator.IsCalibrated;

    /// <summary>
    /// Overlays are only meaningful on the known synthetic board or on a real
    /// camera frame whose perspective has been solved. A replayed still image is
    /// arbitrary and never receives board geometry.
    /// </summary>
    public bool SupportsLiveOverlay
    {
        get
        {
            return ActiveCameraSource switch
            {
                CameraSource.Synthetic => true,
                CameraSource.Camera0 or CameraSource.Camera1 or CameraSource.Camera2 or CameraSource.Camera3 or CameraSource.VideoFile
                    => _calibrator.IsCalibrated || _calibrator.IsMarkerlessFallbackActive,
                _ => false
            };
        }
    }

    /// <summary>Short operator-facing explanation for the live overlay state.</summary>
    public string LiveOverlayStatus
    {
        get
        {
            return ActiveCameraSource switch
            {
                CameraSource.Synthetic => "AR OVERLAYS ON · SYNTHETIC BOARD",
                CameraSource.Camera0 or CameraSource.Camera1 or CameraSource.Camera2 or CameraSource.Camera3 or CameraSource.VideoFile when _calibrator.IsCalibrated
                    => "AR OVERLAYS ON · BOARD LOCKED (ARUCO)",
                CameraSource.Camera0 or CameraSource.Camera1 or CameraSource.Camera2 or CameraSource.Camera3 or CameraSource.VideoFile when _calibrator.IsMarkerlessFallbackActive
                    => "AR OVERLAYS ON · MARKERLESS AUTO-RECTIFIED",
                CameraSource.Camera0 or CameraSource.Camera1 or CameraSource.Camera2 or CameraSource.Camera3 or CameraSource.VideoFile
                    => "AR OVERLAYS HELD · PLACE BREADBOARD CLEARLY IN FRAME",
                CameraSource.ImageFile => "AR OVERLAYS OFF · STILL IMAGE REPLAY",
                _ => "AR OVERLAYS OFF · NO BOARD LOCK"
            };
        }
    }

    public string CameraStatus
    {
        get
        {
            string label = ActiveCameraSource switch
            {
                CameraSource.Synthetic => "SYNTHETIC BOARD",
                CameraSource.Camera0 => "CAMERA 0 (WEBCAM)",
                CameraSource.Camera1 => "CAMERA 1 (SECONDARY)",
                CameraSource.Camera2 => "CAMERA 2 (USB)",
                CameraSource.Camera3 => "CAMERA 3 (USB)",
                CameraSource.ImageFile => _cameraImagePath.Length == 0
                    ? "STILL IMAGE"
                    : $"IMAGE / {Path.GetFileName(_cameraImagePath).ToUpperInvariant()}",
                CameraSource.VideoFile => _cameraImagePath.Length == 0
                    ? "VIDEO REPLAY"
                    : $"VIDEO / {Path.GetFileName(_cameraImagePath).ToUpperInvariant()}",
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
                ? _remoteTarget is { IsLoopback: true }
                    ? $"LOCAL AI / {_remoteTarget.DisplayHost}"
                    : $"COLAB / {_remoteTarget?.DisplayHost ?? "REMOTE"}"
                : "LOCAL";

            return qwen.IsConnected || qwen.LastMode == QwenResponseMode.Server
                ? $"AI TUTOR / {qwen.ActiveModelAlias} / {route}"
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
    public void SetRemoteTutor(RemoteTutorTarget? target, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool changed;
        lock (_qwenSync)
        {
            changed = force || !SameTarget(_remoteTarget, target);
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
            : target.IsLoopback
                ? $"Tutor backend: local AI at {target.DisplayHost}."
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

    public QwenMoEService? QwenService => _qwen;

    public VoiceSynthesisEngine? VoiceEngine => _voice;

    /// <summary>
    /// Speaks the given text guidance using the active voice backend.
    /// </summary>
    public async Task<bool> SpeakTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_voice is null || string.IsNullOrWhiteSpace(text))
            return false;

        CpuAffinityManager.ReportProcessor(AffinityRole.Ai);
        return await _voice.SpeakParagraphAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
    }

    public string VoiceBackendDescription => _voice?.VoiceProfileName ?? "VOICE SILENT";

    /// <summary>Last voice-catalog failure from ElevenLabs, for the settings dock.</summary>
    public string? VoiceCatalogError => _voice?.VoiceCatalogError;

    /// <summary>Lists the ElevenLabs voices available to the supplied key.</summary>
    public Task<IReadOnlyList<ElevenLabsVoice>> ListElevenLabsVoicesAsync(
        string apiKey,
        CancellationToken cancellationToken = default) =>
        _voice is null
            ? Task.FromResult<IReadOnlyList<ElevenLabsVoice>>(Array.Empty<ElevenLabsVoice>())
            : _voice.ListVoicesAsync(apiKey, cancellationToken);

    /// <summary>
    /// Applies the operator's ElevenLabs key, voice and model, then makes
    /// ElevenLabs the active engine. Returns false when nothing can speak.
    /// </summary>
    public async Task<bool> ApplyElevenLabsVoiceAsync(
        string apiKey,
        string voiceId,
        string modelId,
        string voiceName,
        CancellationToken cancellationToken = default)
    {
        VoiceSynthesisEngine? voice = _voice;
        if (voice is null)
            return false;

        bool ok = await voice
            .ApplyElevenLabsSettingsAsync(apiKey, voiceId, modelId, voiceName, cancellationToken)
            .ConfigureAwait(false);

        _voiceNote = voice.LastError ?? string.Empty;
        RaiseStatus(ok
            ? $"Voice: {voice.VoiceProfileName} active."
            : $"Voice: ElevenLabs could not start. {voice.LastError}".TrimEnd());

        return ok;
    }

    /// <summary>The engine that actually loaded, after any fallback.</summary>
    public VoiceBackend ActiveVoiceBackend => _voice?.Backend ?? VoiceBackend.None;

    /// <summary>True when piper.exe and the configured Piper model are present.</summary>
    public bool HasPiperVoice => _voice?.HasPiperVoice ?? false;

    /// <summary>
    /// Switches the live speech engine from the VIEW panel. The old engine is
    /// released only after any in-flight utterance has finished.
    /// </summary>
    public async Task<bool> SetVoiceBackendAsync(
        VoiceEnginePreference preference,
        CancellationToken cancellationToken = default)
    {
        VoiceSynthesisEngine? voice = _voice;
        if (voice is null)
            return false;

        bool switched = await voice.SwitchBackendAsync(preference, cancellationToken)
            .ConfigureAwait(false);

        _voiceNote = voice.LastError ?? string.Empty;
        return switched;
    }

    /// <summary>Speaks a short natural line so the operator can audition the active voice.</summary>
    public Task<bool> SpeakTestLineAsync(CancellationToken cancellationToken = default)
    {
        VoiceSynthesisEngine? voice = _voice;
        return voice is null
            ? Task.FromResult(false)
            : voice.SpeakSentenceAsync(
                "Hi there. Let's take a look at your circuit together.",
                cancellationToken);
    }


    public string VramStatus
    {
        get
        {
            try
            {
                VramSnapshot snapshot = _vramMonitor.GetSnapshot();
                if (!snapshot.IsAvailable)
                    return "NO NVIDIA GPU";

                if (_vramMonitor.IsSystemGpuDetected && snapshot.IsMocked)
                {
                    double sysGb = _vramMonitor.SystemVramBytes / 1024d / 1024d / 1024d;
                    return sysGb > 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{sysGb:0.0} GB VRAM")
                        : "DIRECTX / iGPU";
                }

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

    public string GpuDeviceName => _vramMonitor.DeviceName;
    public ulong SystemVramBytes => _vramMonitor.SystemVramBytes;
    public string SystemGpuName => _vramMonitor.SystemGpuName;
    public bool IsSystemGpuDetected => _vramMonitor.IsSystemGpuDetected;

    public VramSnapshot GetVramSnapshot() => _vramMonitor.GetSnapshot();

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

        // Immediately clear stale packet so previous synthetic faults never remain visible
        Volatile.Write(ref _latestPacket, new DrcStatePacket
        {
            FrameNumber = 0,
            YoloInferenceMs = 0,
            DrcComputeMs = 0,
            StateHash = 0,
            Components = Array.Empty<SnappedComponent>(),
            Violations = Array.Empty<DrcViolation>()
        });

        RaiseStatus(enabled
            ? $"Demo mode ON: {DemoScenarioCatalog.GetTitle(ActiveScenario)}"
            : "Demo mode OFF: live YOLO detection");
    }

    private void ApplyScenario(DemoScenario scenario)
    {
        Volatile.Write(ref _scenario, (int)scenario);
        ResetTopology();

        // Immediately clear stale packet so previous synthetic faults never remain visible
        Volatile.Write(ref _latestPacket, new DrcStatePacket
        {
            FrameNumber = 0,
            YoloInferenceMs = 0,
            DrcComputeMs = 0,
            StateHash = 0,
            Components = Array.Empty<SnappedComponent>(),
            Violations = Array.Empty<DrcViolation>()
        });
        DemoScenarioChanged?.Invoke(scenario);
    }

    /// <summary>
    /// Hands the engine to the X-ray builder. Disabling it returns to demo/YOLO
    /// detections without touching the last published packet.
    /// </summary>
    public void SetManualCircuitEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Volatile.Write(ref _manualCircuit, enabled ? 1 : 0);
        ResetTopology();

        // Immediately clear stale packet so previous synthetic faults never remain visible
        Volatile.Write(ref _latestPacket, new DrcStatePacket
        {
            FrameNumber = 0,
            YoloInferenceMs = 0,
            DrcComputeMs = 0,
            StateHash = 0,
            Components = Array.Empty<SnappedComponent>(),
            Violations = Array.Empty<DrcViolation>()
        });
        if (enabled)
        {
            long frame = Interlocked.Increment(ref _frameCount);
            EvaluateFrame(frame);
        }
        RaiseStatus(enabled
            ? $"X-ray builder active: the DRC judges {ManualPartCount} hand-placed part(s)."
            : "X-ray builder closed: live detections are active again.");
    }

    /// <summary>
    /// Publishes an immutable snapshot of the builder's parts to the reflex loop.
    /// Called on the UI thread after every edit.
    /// </summary>
    public void SetManualDetections(IReadOnlyList<DetectedComponent> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        var snapshot = new DetectedComponent[detections.Count];
        for (int i = 0; i < detections.Count; i++)
            snapshot[i] = detections[i];

        Volatile.Write(ref _manualDetections, snapshot);

        if (ManualCircuitEnabled)
        {
            Volatile.Write(ref _lastStateHash, int.MinValue);
            long frame = Interlocked.Increment(ref _frameCount);
            EvaluateFrame(frame);
        }
    }

    private void ResetTopology()
    {
        _lastStateHash = int.MinValue;
        _motionGate.Reset();
        _wireTracker.Reset();
        _icOcr?.ClearCache();
        _lastReconciledEpoch = -1;
        Volatile.Write(ref _activeTrackedWires, Array.Empty<TrackedWirePair>());
        Volatile.Write(ref _latestIcOcrResults, Array.Empty<IcPartNumberResult>());
        Volatile.Write(ref _latestOcrResult, null);
        Volatile.Write(ref _latestSchematicResult, null);
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

    /// <summary>Replays a recorded breadboard inspection video (loops continuously at 60 FPS).</summary>
    public void SetVideoSource(string videoPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        CameraCaptureService replacement;
        try
        {
            replacement = new CameraCaptureService(CameraSource.VideoFile, imagePath: videoPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RaiseStatus($"Video load failed: {ex.Message}");
            return;
        }

        SwapCamera(replacement, videoPath);
    }

    /// <summary>Encodes the current 60 FPS warped live camera frame into PNG bytes for Qwen video inspection.</summary>
    public byte[]? CaptureLiveWarpedPng()
    {
        lock (_cameraSync)
        {
            if (_warpedFrame.Empty())
                return null;
            return OpenCvSharp.Cv2.ImEncode(".png", _warpedFrame, out byte[] png) ? png : null;
        }
    }

    /// <summary>Encodes the current raw live camera frame directly into PNG bytes for AI vision inspection without OpenCV homography or YOLO.</summary>
    public byte[]? CaptureLiveRawPng()
    {
        lock (_cameraSync)
        {
            if (_cameraFrame.Empty())
                return null;
            return OpenCvSharp.Cv2.ImEncode(".png", _cameraFrame, out byte[] png) ? png : null;
        }
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
        _cameraImagePath = (replacement.IsStillImage || replacement.IsVideo) ? imagePath ?? string.Empty : string.Empty;

        // When switching to a real physical camera or media file, disable synthetic demo mode!
        if (replacement.ActiveSource != CameraSource.Synthetic)
        {
            Volatile.Write(ref _demoEnabled, 0);
        }

        _calibrator.ResetCalibration();
        _motionGate.Reset();
        ResetTopology();

        // Immediately clear stale packet so previous synthetic faults never remain visible
        Volatile.Write(ref _latestPacket, new DrcStatePacket
        {
            FrameNumber = 0,
            YoloInferenceMs = 0,
            DrcComputeMs = 0,
            StateHash = 0,
            Components = Array.Empty<SnappedComponent>(),
            Violations = Array.Empty<DrcViolation>()
        });

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
            if ((frame & 15) == 0)
                CpuAffinityManager.ReportProcessor(AffinityRole.Vision);
            frame++;

            try
            {
                if (!IsOpenCvEnabled)
                {
                    EvaluateFrame(frame);
                    await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!TryCaptureWarpedFrame(ref calibrationCooldown))
                {
                    if (ManualCircuitEnabled)
                        EvaluateFrame(frame);

                    await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                long prevEpoch = _motionGate.SettledEpoch;
                bool stable = !IsHandGateEnabled || _motionGate.Update(_warpedFrame);
                bool justSettled = !IsHandGateEnabled || _motionGate.SettledEpoch != prevEpoch || _lastReconciledEpoch < 0;

                if (_options.EnableCameraPreviewBitmap && (frame & 1) == 0)
                    UpdateBoardBitmap();

                if (!stable && !DemoModeEnabled && !ManualCircuitEnabled)
                {
                    await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                EvaluateFrame(frame, justSettled);

                await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);

                if (_lastLoopFault is not null)
                    _lastLoopFault = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Without this the loop task died silently and the viewport froze with
                // no explanation. Report the fault and keep the 60 FPS loop alive.
                ReportLoopFault("VISION", ex);
                await DelayRemainingAsync(frameStart, targetFrameMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Surfaces a worker-loop fault once per distinct message so the UI never freezes silently.</summary>
    private void ReportLoopFault(string loop, Exception exception)
    {
        string message = $"{loop} LOOP FAULT: {exception.GetType().Name}: {exception.Message}";
        if (string.Equals(message, _lastLoopFault, StringComparison.Ordinal))
            return;

        _lastLoopFault = message;
        RaiseStatus(message + " The loop keeps retrying; check the camera / model files if it repeats.");
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

    private PaddleIcPartNumberOcr GetOrCreateIcOcr()
    {
        return LazyInitializer.EnsureInitialized(
            ref _icOcr,
            () => new PaddleIcPartNumberOcr(
                initializeNativeRecognizer: _options.EnableCameraPreviewBitmap || _options.EnableCognitiveTutor))!;
    }

    internal void ReconcileSettledDetections(
        OpenCvSharp.Mat? warpedFrame,
        IReadOnlyList<DetectedComponent> rawDetections,
        bool justSettled,
        List<DetectedComponent> output)
    {
        ArgumentNullException.ThrowIfNull(rawDetections);
        ArgumentNullException.ThrowIfNull(output);

        output.Clear();

        if (!IsWireTrackerEnabled || warpedFrame is null || warpedFrame.Empty())
        {
            for (int i = 0; i < rawDetections.Count; i++)
                output.Add(rawDetections[i]);
        }
        else if (justSettled)
        {
            IReadOnlyList<TrackedWirePair> tracked = _wireTracker.UpdateFromPoseDetections(warpedFrame, rawDetections, output);
            var snapshot = new TrackedWirePair[tracked.Count];
            for (int i = 0; i < tracked.Count; i++)
                snapshot[i] = tracked[i];
            Volatile.Write(ref _activeTrackedWires, snapshot);
            _lastReconciledEpoch = _motionGate.SettledEpoch;
        }
        else
        {
            IReadOnlyList<TrackedWirePair> active = Volatile.Read(ref _activeTrackedWires);

            for (int i = 0; i < rawDetections.Count; i++)
            {
                if (rawDetections[i].Kind != ComponentKind.JumperWire)
                    output.Add(rawDetections[i]);
            }

            for (int i = 0; i < active.Count; i++)
            {
                TrackedWirePair wire = active[i];
                float minX = Math.Min(wire.End1Px, wire.End2Px);
                float minY = Math.Min(wire.End1Py, wire.End2Py);
                float maxX = Math.Max(wire.End1Px, wire.End2Px);
                float maxY = Math.Max(wire.End1Py, wire.End2Py);

                output.Add(new DetectedComponent(
                    TrackId: 1000 + wire.WireId,
                    Kind: ComponentKind.JumperWire,
                    Confidence: wire.Confidence,
                    BoxX: minX,
                    BoxY: minY,
                    BoxW: Math.Max(4f, maxX - minX),
                    BoxH: Math.Max(4f, maxY - minY),
                    Lead1Px: wire.End1Px,
                    Lead1Py: wire.End1Py,
                    Lead2Px: wire.End2Px,
                    Lead2Py: wire.End2Py));
            }
        }

        if (!IsIcOcrEnabled || !justSettled || warpedFrame is null || warpedFrame.Empty())
            return;

        List<IcPartNumberResult>? ocrResults = null;
        PaddleIcPartNumberOcr ocr = GetOrCreateIcOcr();
        for (int i = 0; i < output.Count; i++)
        {
            DetectedComponent component = output[i];
            if (component.Kind != ComponentKind.IntegratedCircuit)
                continue;

            if (ocr.TryReadPartNumber(warpedFrame, in component, isPin1Left: true, out IcPartNumberResult result))
            {
                ocrResults ??= new List<IcPartNumberResult>(4);
                ocrResults.Add(result);
                if (!string.IsNullOrWhiteSpace(result.CanonicalPartNumber))
                {
                    output[i] = component with { DecodedValueLabel = result.CanonicalPartNumber };
                }
            }
        }

        if (ocrResults is { Count: > 0 })
        {
            Volatile.Write(ref _latestIcOcrResults, ocrResults.ToArray());
            Volatile.Write(ref _latestOcrResult, ocrResults[0].CanonicalPartNumber);
        }
    }

    private void EvaluateFrame(long frame, bool justSettled = false)
    {
        bool demo = DemoModeEnabled && ActiveCameraSource == CameraSource.Synthetic;
        bool manual = ManualCircuitEnabled;
        DemoScenario scenario = ActiveScenario;

        ReadOnlySpan<DetectedComponent> detections;
        double yoloMs;

        if (manual)
        {
            detections = Volatile.Read(ref _manualDetections);
            yoloMs = 0d;
        }
        else if (demo)
        {
            detections = DemoScenarioCatalog.GetDetections(scenario);
            yoloMs = 0d;
        }
        else if (CameraCaptureService.IsPhysicalCamera(ActiveCameraSource) && !_calibrator.IsCalibrated && !_calibrator.IsMarkerlessFallbackActive)
        {
            _yoloDetections.Clear();
            detections = CollectionsMarshal.AsSpan(_yoloDetections);
            yoloMs = 0d;
        }
        else if (_yolo is not null && IsYoloEnabled && !IsAiVisionModeActive)
        {
            _yolo.Infer(_warpedFrame, _yoloDetections, 1);
            ReconcileSettledDetections(_warpedFrame, _yoloDetections, justSettled, _reconciledDetections);
            detections = CollectionsMarshal.AsSpan(_reconciledDetections);
            yoloMs = _yolo.LastInferenceMs;
        }
        else
        {
            _yoloDetections.Clear();
            detections = CollectionsMarshal.AsSpan(_yoloDetections);
            yoloMs = 0d;
        }

        DrcStatePacket packet = IsDrcEnabled
            ? _drc.EvaluateBoard(frame, yoloMs, detections, _targetSchematic)
            : new DrcStatePacket
            {
                FrameNumber = frame,
                YoloInferenceMs = yoloMs,
                DrcComputeMs = 0d,
                StateHash = 0,
                Components = Array.Empty<SnappedComponent>(),
                Violations = Array.Empty<DrcViolation>()
            };

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

        try
        {
            var kicadResult = _kicadEngine.CompileFromState(published);
            Volatile.Write(ref _latestSchematicResult, kicadResult);
            SchematicCompiled?.Invoke(kicadResult);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Optional schematic compilation; keeps pipeline running
        }

        IReadOnlyList<RatsnestVector> ratsnest = Array.Empty<RatsnestVector>();
        if ((manual || demo && DemoScenarioCatalog.UsesTargetSchematic(scenario)) && _targetSchematic is not null)
            ratsnest = TargetSchematicLoader.ComputeRatsnest(_targetSchematic, _pinMatrix, _drc);

        Volatile.Write(ref _latestRatsnest, ratsnest);
        Volatile.Write(ref _latestPacket, published);

        ReflexDrcUpdated?.Invoke(published);

        if (published.HasCriticalShort)
            RaiseEmergency(published);

        // In manual / builder mode, automatic AI dispatch is suppressed.
        // The builder runs continuous DRC only; Qwen is dispatched when the user presses "ASK AI".
        if (!manual && (published.Components.Count > 0 || published.Violations.Count > 0))
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

        if (!IsAlarmEnabled)
            return;

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A fault outside one tutor turn (e.g. channel plumbing) must still be visible.
            ReportLoopFault("TUTOR", ex);
        }
    }

    private async Task RunTutorTurnAsync(DrcStatePacket packet, CancellationToken cancellationToken)
    {
        if (!IsCognitiveTutorEnabled)
            return;

        QwenMoEService? qwen = _qwen;
        if (qwen is null)
            return;

        TutorTurnStarted?.Invoke();
        CpuAffinityManager.ReportProcessor(AffinityRole.Ai);

        var splitter = new SentenceStreamSplitter();
        var transcript = new StringBuilder(320);
        var sentences = new List<string>(4);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double firstTokenMs = -1d;

        await foreach (string token in qwen
                           .StreamDiagnosticAsync(packet, prompt: null, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (firstTokenMs < 0d)
                firstTokenMs = clock.Elapsed.TotalMilliseconds;

            transcript.Append(token);
            TutorTranscriptTokenReceived?.Invoke(token);

            foreach (string sentence in splitter.Push(token))
            {
                sentences.Add(sentence);
                TutorSentenceCompleted?.Invoke(sentence);
            }
        }

        string? tail = splitter.Flush();
        if (tail is not null)
        {
            sentences.Add(tail);
            TutorSentenceCompleted?.Invoke(tail);
        }

        // Only the model's own answer is ever spoken. Rule-based / DRC text is
        // shown on screen but never synthesized, so the voice always belongs to
        // Qwen and never to the deterministic checker.
        bool shouldSpeak = QwenMoEService.ShouldSpeakAnswer(
            qwen.LastMode,
            VoiceMuted,
            _voice is not null,
            sentences.Count);

        if (shouldSpeak)
            await _voice!.SpeakParagraphAsync(sentences, cancellationToken).ConfigureAwait(false);

        if (transcript.Length > 0)
        {
            TutorDiagnosticCompleted?.Invoke(transcript.ToString());

            TutorTurnMeasured?.Invoke(new TutorTurnMetrics(
                TimeToFirstTokenMs: firstTokenMs < 0d ? clock.Elapsed.TotalMilliseconds : firstTokenMs,
                TotalMs: clock.Elapsed.TotalMilliseconds,
                Characters: transcript.Length,
                Backend: qwen.LastMode == QwenResponseMode.Server
                    ? _remoteTarget is { IsLoopback: true }
                        ? "LOCAL AI TUTOR"
                        : UsingRemoteTutor ? "REMOTE TUTOR" : "LOCAL TUTOR"
                    : "RULE-BASED TUTOR",
                RuleBased: qwen.LastMode != QwenResponseMode.Server));
        }
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

    /// <summary>Explicitly queues the active DRC packet into Qwen cognitive loop on demand.</summary>
    public bool RequestAiDiagnostic(byte[]? fullBoardPng = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DrcStatePacket? packet = LatestPacket;
        if (packet is null)
        {
            RaiseStatus("AI Tutor unavailable: no DRC packet yet.");
            return false;
        }

        if (fullBoardPng is { Length: > 0 })
            packet.FullBoardPngBytes = fullBoardPng;

        if (_qwen is null || _cognitiveTask is null)
        {
            RaiseStatus("AI Tutor unavailable: cognitive tutor loop is disabled.");
            return false;
        }

        if (!_cognitiveChannel.Writer.TryWrite(packet))
        {
            RaiseStatus("AI Tutor busy: previous response is still streaming.");
            return false;
        }

        RaiseStatus($"AI Tutor: dispatched {packet.Components.Count} part(s) and {packet.Violations.Count} DRC violation(s) to Qwen.");
        return true;
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
        _icOcr?.Dispose();
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

/// <summary>Latency numbers for one completed tutor turn, shown in the activity strip.</summary>
public readonly record struct TutorTurnMetrics(
    double TimeToFirstTokenMs,
    double TotalMs,
    int Characters,
    string Backend,
    bool RuleBased,
    int EstimatedTokens = 0)
{
    public string Describe()
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{TotalMs / 1000d:0.0}s  \u00b7  {Math.Max(1, (int)Math.Ceiling(Characters / 4.0))} TOK  \u00b7  TTFT {TimeToFirstTokenMs:0} MS  \u00b7  {Characters} CHARS");
    }
}
