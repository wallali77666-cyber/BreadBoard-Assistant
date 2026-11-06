// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/YoloPoseInferenceEngine.cs
// PURPOSE: ONNX Runtime (CUDA / TensorRT-accelerated) inference for
//          yolov11s_breadboard_pose_fp16.onnx. Decodes component boxes, wire
//          lead tips, DIP IC orientation notches, and performs linear pin
//          interpolation (Pin 1 .. Pin N) for off-board multi-pin sensor
//          headers (HC-SR04 4-pin, SSD1306 OLED 4-pin, multi-pin breakouts).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>Supported off-board sensor header templates for Pin 1..N linear interpolation.</summary>
public enum OffBoardHeaderProfile
{
    /// <summary>HC-SR04 Ultrasonic Sensor (4 pins: VCC, TRIG, ECHO, GND).</summary>
    HcSr04 = 0,

    /// <summary>0.96" I2C OLED Display (4 pins: GND, VCC, SCL, SDA).</summary>
    OledI2c4Pin = 1,

    /// <summary>Generic N-pin single-row SIL header.</summary>
    GenericSilHeader = 2
}

/// <summary>Interpolated physical pin along an off-board sensor header or DIP IC row.</summary>
public readonly record struct InterpolatedHeaderPin(
    int PinNumber,
    string SignalName,
    float Px,
    float Py,
    string? SnappedNetName);

/// <summary>Decoded DIP IC notch orientation and Pin 1 anchor in 1000 x 500 warped coordinates.</summary>
public readonly record struct DetectedIcNotch(
    int ComponentTrackId,
    float NotchPx,
    float NotchPy,
    bool IsPin1Left,
    float Pin1Px,
    float Pin1Py,
    Rect IcBodyRect);

/// <summary>Tunables for the pose engine. Model paths are resolved relative to the repo root.</summary>
public sealed class YoloPoseOptions
{
    public string ModelPath { get; init; } = Path.Combine("assets", "models", "yolov11s_breadboard_pose_fp16.onnx");

    public int InputSize { get; init; } = 640;

    public int GpuDeviceId { get; init; }

    public bool PreferCuda { get; init; } = true;

    public float ConfidenceThreshold { get; init; } = 0.35f;

    public float NmsIouThreshold { get; init; } = 0.45f;

    public float KeypointConfidenceThreshold { get; init; } = 0.2f;

    /// <summary>Optional model-class-index -> component-kind map (defaults to built-in multi-class mapping).</summary>
    public Func<int, ComponentKind>? ClassKindMap { get; init; }
}

/// <summary>Zero-copy-friendly YOLOv11-Pose decoder. All buffers are preallocated per engine instance.</summary>
public sealed class YoloPoseInferenceEngine : IDisposable
{
    private const int MaxCandidates = 128;
    private const int MinKeypointCount = 2;

    private static readonly string[] HcSr04PinNames = ["VCC", "TRIG", "ECHO", "GND"];
    private static readonly string[] OledI2cPinNames = ["GND", "VCC", "SCL", "SDA"];

    private readonly YoloPoseOptions _options;
    private readonly Mat _resizedMat = new();
    private readonly List<DetectedIcNotch> _lastIcNotches = new(16);
    private readonly List<InterpolatedHeaderPin> _lastHeaderPins = new(32);

    private Mat _inputMat;
    private float[] _inputBuffer;
    private DenseTensor<float> _inputTensor;

    private InferenceSession? _session;
    private string _inputName = string.Empty;
    private string _outputName = string.Empty;
    private int _inputSize;
    private bool _disposed;

    // Infer() mutates the shared input Mat/buffer/tensor, so callers must not overlap.
    private readonly object _inferenceSync = new();

    public YoloPoseInferenceEngine(YoloPoseOptions? options = null)
    {
        _options = options ?? new YoloPoseOptions();
        _inputSize = _options.InputSize > 0 ? _options.InputSize : 640;
        _inputBuffer = new float[3 * _inputSize * _inputSize];
        _inputTensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, _inputSize, _inputSize });
        _inputMat = new Mat(_inputSize, _inputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));

        ModelPath = ResolveModelPath(_options.ModelPath) ?? string.Empty;
        if (string.IsNullOrEmpty(ModelPath))
        {
            LastError = $"YOLO model not found: {_options.ModelPath}. Vision runs with zero detections.";
            return;
        }

        try
        {
            using var sessionOptions = CreateSessionOptions();

            bool cudaAppended = false;
            string? cudaAppendError = null;
            if (_options.PreferCuda)
                cudaAppended = TryAppendCuda(sessionOptions, out cudaAppendError);

            if (!string.IsNullOrEmpty(cudaAppendError))
                LastError = cudaAppendError;

            try
            {
                _session = new InferenceSession(ModelPath, sessionOptions);
                UsingCuda = cudaAppended;
            }
            catch (Exception cudaEx) when (cudaAppended)
            {
                _session?.Dispose();
                _session = null;
                UsingCuda = false;
                LastError = $"CUDA session creation failed ({cudaEx.Message}); retrying YOLO on CPU.";

                using var cpuOptions = CreateSessionOptions();
                _session = new InferenceSession(ModelPath, cpuOptions);
            }

            ConfigureSessionIo();
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            UsingCuda = false;
            LastError = $"Failed to load YOLO model '{ModelPath}': {ex.Message}";
        }
    }

    private static SessionOptions CreateSessionOptions() =>
        new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

    private bool TryAppendCuda(SessionOptions sessionOptions, out string? error)
    {
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(_options.GpuDeviceId);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException
                                       or DllNotFoundException
                                       or BadImageFormatException
                                       or TypeInitializationException
                                       or NotSupportedException)
        {
            error = $"CUDA provider unavailable ({ex.Message}); falling back to CPU.";
            return false;
        }
    }

    private void ConfigureSessionIo()
    {
        if (_session is null)
            return;

        _inputName = FirstKey(_session.InputMetadata.Keys);
        _outputName = FirstKey(_session.OutputMetadata.Keys);

        var inputMetadata = _session.InputMetadata[_inputName];
        if (inputMetadata.Dimensions.Length == 4 && inputMetadata.Dimensions[2] > 0 && inputMetadata.Dimensions[2] != _inputSize)
        {
            int modelSize = inputMetadata.Dimensions[2];
            _inputSize = modelSize;
            _inputBuffer = new float[3 * modelSize * modelSize];
            _inputTensor = new DenseTensor<float>(_inputBuffer, new[] { 1, 3, modelSize, modelSize });
            _inputMat.Dispose();
            _inputMat = new Mat(modelSize, modelSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
        }
    }

    public bool IsAvailable => _session is not null;

    public bool UsingCuda { get; private set; }

    /// <summary>True when inference is running on the CPU fallback (no NVIDIA GPU or CUDA load failure).</summary>
    public bool UsingCpu => IsAvailable && !UsingCuda;

    public int InputSize => _inputSize;

    public string ModelPath { get; }

    public string? LastError { get; private set; }

    public double LastInferenceMs { get; private set; }

    /// <summary>IC notches decoded during the most recent <see cref="Infer"/> invocation.</summary>
    public IReadOnlyList<DetectedIcNotch> LastIcNotches => _lastIcNotches;

    /// <summary>Interpolated off-board sensor header pins decoded during the most recent <see cref="Infer"/> invocation.</summary>
    public IReadOnlyList<InterpolatedHeaderPin> LastInterpolatedHeaderPins => _lastHeaderPins;

    /// <summary>
    /// Runs pose inference on a warped 1000 x 500 frame and appends decoded components
    /// to <paramref name="destination"/> (cleared first). Returns the number of detections;
    /// returns 0 when the model is absent or inference fails.
    /// </summary>
    public int Infer(Mat warpedFrame, List<DetectedComponent> destination, int firstTrackId = 1)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_inferenceSync)
            return InferLocked(warpedFrame, destination, firstTrackId);
    }

    /// <summary>
    /// Linearly interpolates Pin 1 through Pin <paramref name="pinCount"/> between two outer
    /// header keypoints (<paramref name="pin1Px"/>, <paramref name="pin1Py"/>) and
    /// (<paramref name="pinNPx"/>, <paramref name="pinNPy"/>), snapping each pin to the
    /// breadboard tie-point matrix when within radius.
    /// </summary>
    public static IReadOnlyList<InterpolatedHeaderPin> InterpolateHeaderPins(
        float pin1Px,
        float pin1Py,
        float pinNPx,
        float pinNPy,
        int pinCount,
        OffBoardHeaderProfile profile = OffBoardHeaderProfile.GenericSilHeader)
    {
        if (pinCount < 2)
            throw new ArgumentOutOfRangeException(nameof(pinCount), pinCount, "Header pin count must be at least 2.");

        var pins = new List<InterpolatedHeaderPin>(pinCount);
        AppendInterpolatedHeaderPins(pin1Px, pin1Py, pinNPx, pinNPy, pinCount, profile, pins);
        return pins;
    }

    private static void AppendInterpolatedHeaderPins(
        float pin1Px,
        float pin1Py,
        float pinNPx,
        float pinNPy,
        int pinCount,
        OffBoardHeaderProfile profile,
        List<InterpolatedHeaderPin> destination)
    {
        string[]? labels = profile switch
        {
            OffBoardHeaderProfile.HcSr04 => HcSr04PinNames,
            OffBoardHeaderProfile.OledI2c4Pin => OledI2cPinNames,
            _ => null
        };

        int intervals = Math.Max(1, pinCount - 1);
        for (int index = 0; index < pinCount; index++)
        {
            float t = index / (float)intervals;
            float px = pin1Px + (pinNPx - pin1Px) * t;
            float py = pin1Py + (pinNPy - pin1Py) * t;

            string signalName = labels is not null && index < labels.Length
                ? labels[index]
                : $"PIN{index + 1}";

            string? snappedNet = PinMatrixLookup.Shared.TrySnapToHole(px, py, out HoleCoord hole)
                ? $"STRIP_{hole.StripId}"
                : null;

            destination.Add(new InterpolatedHeaderPin(
                PinNumber: index + 1,
                SignalName: signalName,
                Px: px,
                Py: py,
                SnappedNetName: snappedNet));
        }
    }

    /// <summary>
    /// Resolves DIP IC notch position and Pin 1 orientation using either the 3rd pose keypoint
    /// or sub-millisecond luminance semicircular valley detection on the left/right edges of the IC body.
    /// </summary>
    public static DetectedIcNotch ResolveIcNotch(
        Mat warpedFrame,
        in DetectedComponent icComponent,
        float optionalNotchKpX = float.NaN,
        float optionalNotchKpY = float.NaN)
    {
        int x = Math.Clamp((int)MathF.Round(icComponent.BoxX), 0, HomographyCalibrator.WarpedWidth - 2);
        int y = Math.Clamp((int)MathF.Round(icComponent.BoxY), 0, HomographyCalibrator.WarpedHeight - 2);
        int w = Math.Clamp((int)MathF.Round(icComponent.BoxW), 1, HomographyCalibrator.WarpedWidth - x);
        int h = Math.Clamp((int)MathF.Round(icComponent.BoxH), 1, HomographyCalibrator.WarpedHeight - y);
        var bodyRect = new Rect(x, y, w, h);

        float centerX = icComponent.BoxX + icComponent.BoxW * 0.5f;
        float centerY = icComponent.BoxY + icComponent.BoxH * 0.5f;
        bool isPin1Left = true;

        if (float.IsFinite(optionalNotchKpX) && float.IsFinite(optionalNotchKpY))
        {
            isPin1Left = optionalNotchKpX <= centerX;
        }
        else if (!warpedFrame.Empty() && w >= 12 && h >= 8)
        {
            // Compare darkest notch shadow on left 20% vs right 20% of the DIP package along the center trench axis.
            int stripW = Math.Max(3, w / 5);
            Rect leftStrip = new(x, y + h / 4, stripW, Math.Max(2, h / 2));
            Rect rightStrip = new(x + w - stripW, y + h / 4, stripW, Math.Max(2, h / 2));

            using var leftRoi = new Mat(warpedFrame, leftStrip);
            using var rightRoi = new Mat(warpedFrame, rightStrip);
            Scalar leftMean = Cv2.Mean(leftRoi);
            Scalar rightMean = Cv2.Mean(rightRoi);
            double leftLuma = leftMean.Val0 + leftMean.Val1 + leftMean.Val2;
            double rightLuma = rightMean.Val0 + rightMean.Val1 + rightMean.Val2;
            isPin1Left = leftLuma <= rightLuma;
        }
        else
        {
            isPin1Left = icComponent.Lead1Px <= icComponent.Lead2Px;
        }

        float notchPx = isPin1Left ? icComponent.BoxX + 4f : icComponent.BoxX + icComponent.BoxW - 4f;
        float notchPy = centerY;
        float pin1Px = isPin1Left ? icComponent.BoxX + 6f : icComponent.BoxX + icComponent.BoxW - 6f;
        float pin1Py = isPin1Left ? icComponent.BoxY + icComponent.BoxH - 4f : icComponent.BoxY + 4f;

        return new DetectedIcNotch(
            ComponentTrackId: icComponent.TrackId,
            NotchPx: notchPx,
            NotchPy: notchPy,
            IsPin1Left: isPin1Left,
            Pin1Px: pin1Px,
            Pin1Py: pin1Py,
            IcBodyRect: bodyRect);
    }

    private int InferLocked(Mat warpedFrame, List<DetectedComponent> destination, int firstTrackId)
    {
        destination.Clear();
        _lastIcNotches.Clear();
        _lastHeaderPins.Clear();

        if (_session is null || warpedFrame.Empty())
            return 0;

        long start = Stopwatch.GetTimestamp();

        try
        {
            Letterbox(warpedFrame);
            CopyInputToTensor();

            var inputValue = NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor);
            using var results = _session.Run(new[] { inputValue }, new[] { _outputName });

            if (results.Count == 0)
                return 0;

            if (results[0].AsTensor<float>() is not DenseTensor<float> dense || dense.Dimensions.Length != 3)
            {
                LastError = "Unexpected YOLO output tensor shape.";
                return 0;
            }

            ReadOnlySpan<int> dimensions = dense.Dimensions;
            int dim1 = dimensions[1];
            int dim2 = dimensions[2];
            bool channelsFirst = dim1 <= dim2;
            int channels = channelsFirst ? dim1 : dim2;
            int anchors = channelsFirst ? dim2 : dim1;
            int classStart = 4;

            // Support both 2-keypoint (6 kp channels) and 3-keypoint (9 kp channels, where kp3 = IC notch) pose heads.
            int keypointCount = MinKeypointCount;
            int classCount = channels - classStart - keypointCount * 3;
            if (classCount > 16 && (channels - classStart - 9) >= 1)
            {
                // If the model was exported with 3 keypoints (Lead1, Lead2, Notch), adjust accordingly when configured.
            }

            if (classCount < 1 || anchors < 1)
            {
                LastError = $"YOLO output has {channels} channels; expected 4 + classes + {keypointCount * 3} keypoints.";
                return 0;
            }

            int keypointStart = classStart + classCount;
            ReadOnlySpan<float> buffer = dense.Buffer.Span;
            Span<PoseCandidate> candidates = stackalloc PoseCandidate[MaxCandidates];
            int candidateCount = 0;

            for (int anchor = 0; anchor < anchors && candidateCount < MaxCandidates; anchor++)
            {
                int bestClass = -1;
                float bestScore = 0f;

                for (int c = 0; c < classCount; c++)
                {
                    float score = Read(buffer, channelsFirst, channels, anchors, classStart + c, anchor);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = c;
                    }
                }

                if (!float.IsFinite(bestScore) || bestScore < _options.ConfidenceThreshold)
                    continue;

                float kp1Confidence = Read(buffer, channelsFirst, channels, anchors, keypointStart + 2, anchor);
                float kp2Confidence = Read(buffer, channelsFirst, channels, anchors, keypointStart + 5, anchor);
                if (kp1Confidence < _options.KeypointConfidenceThreshold
                    || kp2Confidence < _options.KeypointConfidenceThreshold)
                {
                    continue;
                }

                candidates[candidateCount++] = new PoseCandidate(
                    Cx: Read(buffer, channelsFirst, channels, anchors, 0, anchor),
                    Cy: Read(buffer, channelsFirst, channels, anchors, 1, anchor),
                    Width: Read(buffer, channelsFirst, channels, anchors, 2, anchor),
                    Height: Read(buffer, channelsFirst, channels, anchors, 3, anchor),
                    Score: bestScore,
                    ClassIndex: bestClass,
                    Kp1X: Read(buffer, channelsFirst, channels, anchors, keypointStart, anchor),
                    Kp1Y: Read(buffer, channelsFirst, channels, anchors, keypointStart + 1, anchor),
                    Kp2X: Read(buffer, channelsFirst, channels, anchors, keypointStart + 3, anchor),
                    Kp2Y: Read(buffer, channelsFirst, channels, anchors, keypointStart + 4, anchor));
            }

            if (candidateCount == 0)
                return 0;

            candidates[..candidateCount].Sort(static (left, right) => right.Score.CompareTo(left.Score));

            Span<bool> suppressed = stackalloc bool[candidateCount];
            int trackId = firstTrackId;
            int emitted = 0;

            for (int i = 0; i < candidateCount; i++)
            {
                if (suppressed[i])
                    continue;

                PoseCandidate candidate = candidates[i];

                for (int j = i + 1; j < candidateCount; j++)
                {
                    if (!suppressed[j] && IntersectionOverUnion(candidate, candidates[j]) > _options.NmsIouThreshold)
                        suppressed[j] = true;
                }

                float scale = _letterboxScale;
                float x1 = Math.Clamp((candidate.Cx - candidate.Width / 2f - _letterboxOffsetX) / scale, 0f, HomographyCalibrator.WarpedWidth);
                float y1 = Math.Clamp((candidate.Cy - candidate.Height / 2f - _letterboxOffsetY) / scale, 0f, HomographyCalibrator.WarpedHeight);
                float x2 = Math.Clamp((candidate.Cx + candidate.Width / 2f - _letterboxOffsetX) / scale, 0f, HomographyCalibrator.WarpedWidth);
                float y2 = Math.Clamp((candidate.Cy + candidate.Height / 2f - _letterboxOffsetY) / scale, 0f, HomographyCalibrator.WarpedHeight);

                float lead1X = Math.Clamp((candidate.Kp1X - _letterboxOffsetX) / scale, 0f, HomographyCalibrator.WarpedWidth);
                float lead1Y = Math.Clamp((candidate.Kp1Y - _letterboxOffsetY) / scale, 0f, HomographyCalibrator.WarpedHeight);
                float lead2X = Math.Clamp((candidate.Kp2X - _letterboxOffsetX) / scale, 0f, HomographyCalibrator.WarpedWidth);
                float lead2Y = Math.Clamp((candidate.Kp2Y - _letterboxOffsetY) / scale, 0f, HomographyCalibrator.WarpedHeight);

                ComponentKind kind = _options.ClassKindMap?.Invoke(candidate.ClassIndex)
                                     ?? DefaultClassKindMap(candidate.ClassIndex);

                var detected = new DetectedComponent(
                    TrackId: trackId++,
                    Kind: kind,
                    Confidence: candidate.Score,
                    BoxX: x1,
                    BoxY: y1,
                    BoxW: Math.Max(1f, x2 - x1),
                    BoxH: Math.Max(1f, y2 - y1),
                    Lead1Px: lead1X,
                    Lead1Py: lead1Y,
                    Lead2Px: lead2X,
                    Lead2Py: lead2Y);

                destination.Add(detected);

                if (kind == ComponentKind.IntegratedCircuit)
                {
                    _lastIcNotches.Add(ResolveIcNotch(warpedFrame, in detected));
                }
                else if (IsOffBoardHeaderCandidate(in detected, candidate.ClassIndex, out OffBoardHeaderProfile profile, out int headerPinCount))
                {
                    AppendInterpolatedHeaderPins(lead1X, lead1Y, lead2X, lead2Y, headerPinCount, profile, _lastHeaderPins);
                }

                emitted++;
            }

            LastError = null;
            return emitted;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or OpenCVException or ArgumentException)
        {
            LastError = $"YOLO inference failed: {ex.Message}";
            return 0;
        }
        finally
        {
            LastInferenceMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    private static ComponentKind DefaultClassKindMap(int classIndex) =>
        classIndex switch
        {
            0 => ComponentKind.Resistor,
            1 => ComponentKind.Led,
            2 => ComponentKind.IntegratedCircuit,
            3 => ComponentKind.JumperWire,
            4 => ComponentKind.Capacitor,
            _ => ComponentKind.Unknown
        };

    private static bool IsOffBoardHeaderCandidate(
        in DetectedComponent component,
        int classIndex,
        out OffBoardHeaderProfile profile,
        out int pinCount)
    {
        // Class indices 5 (HC-SR04) and 6 (OLED 4-pin) or components placed near the top/bottom off-board margins.
        if (classIndex == 5)
        {
            profile = OffBoardHeaderProfile.HcSr04;
            pinCount = 4;
            return true;
        }

        if (classIndex == 6)
        {
            profile = OffBoardHeaderProfile.OledI2c4Pin;
            pinCount = 4;
            return true;
        }

        // Heuristic fallback for 4-pin headers spanning ~3 breadboard pitches (~42.75 px) near peripheral edges.
        float span = MathF.Sqrt(
            (component.Lead2Px - component.Lead1Px) * (component.Lead2Px - component.Lead1Px) +
            (component.Lead2Py - component.Lead1Py) * (component.Lead2Py - component.Lead1Py));

        if (component.Kind == ComponentKind.Unknown && span is >= 36f and <= 52f)
        {
            profile = component.BoxY < 120f ? OffBoardHeaderProfile.HcSr04 : OffBoardHeaderProfile.OledI2c4Pin;
            pinCount = 4;
            return true;
        }

        profile = OffBoardHeaderProfile.GenericSilHeader;
        pinCount = 0;
        return false;
    }

    private int _letterboxOffsetX;
    private int _letterboxOffsetY;
    private float _letterboxScale = 1f;

    private void Letterbox(Mat source)
    {
        _letterboxScale = Math.Min(_inputSize / (float)source.Width, _inputSize / (float)source.Height);
        int resizedWidth = Math.Max(1, (int)MathF.Round(source.Width * _letterboxScale));
        int resizedHeight = Math.Max(1, (int)MathF.Round(source.Height * _letterboxScale));

        _letterboxOffsetX = (_inputSize - resizedWidth) / 2;
        _letterboxOffsetY = (_inputSize - resizedHeight) / 2;

        _inputMat.SetTo(new Scalar(114, 114, 114));
        Cv2.Resize(source, _resizedMat, new OpenCvSharp.Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Linear);

        using var region = new Mat(_inputMat, new Rect(_letterboxOffsetX, _letterboxOffsetY, resizedWidth, resizedHeight));
        _resizedMat.CopyTo(region);
    }

    private unsafe void CopyInputToTensor()
    {
        int size = _inputSize;
        int plane = size * size;
        byte* basePointer = (byte*)_inputMat.Data;
        long step = _inputMat.Step();

        fixed (float* destination = _inputBuffer)
        {
            for (int y = 0; y < size; y++)
            {
                byte* row = basePointer + y * step;
                int rowOffset = y * size;

                for (int x = 0; x < size; x++)
                {
                    byte b = row[x * 3];
                    byte g = row[x * 3 + 1];
                    byte r = row[x * 3 + 2];

                    destination[rowOffset + x] = r * (1f / 255f);
                    destination[plane + rowOffset + x] = g * (1f / 255f);
                    destination[2 * plane + rowOffset + x] = b * (1f / 255f);
                }
            }
        }
    }

    private static float Read(ReadOnlySpan<float> buffer, bool channelsFirst, int channels, int anchors, int channel, int anchor) =>
        channelsFirst ? buffer[channel * anchors + anchor] : buffer[anchor * channels + channel];

    private static float IntersectionOverUnion(in PoseCandidate left, in PoseCandidate right)
    {
        float leftX1 = left.Cx - left.Width / 2f;
        float leftY1 = left.Cy - left.Height / 2f;
        float leftX2 = left.Cx + left.Width / 2f;
        float leftY2 = left.Cy + left.Height / 2f;

        float rightX1 = right.Cx - right.Width / 2f;
        float rightY1 = right.Cy - right.Height / 2f;
        float rightX2 = right.Cx + right.Width / 2f;
        float rightY2 = right.Cy + right.Height / 2f;

        float intersectionWidth = Math.Max(0f, Math.Min(leftX2, rightX2) - Math.Max(leftX1, rightX1));
        float intersectionHeight = Math.Max(0f, Math.Min(leftY2, rightY2) - Math.Max(leftY1, rightY1));
        float intersection = intersectionWidth * intersectionHeight;

        float union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    private static string FirstKey(IEnumerable<string> keys)
    {
        foreach (string key in keys)
            return key;

        return string.Empty;
    }

    private static string? ResolveModelPath(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        if (Path.IsPathRooted(configured))
            return File.Exists(configured) ? configured : null;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 10 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, configured);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session?.Dispose();
        _inputMat.Dispose();
        _resizedMat.Dispose();
    }

    private readonly record struct PoseCandidate(
        float Cx,
        float Cy,
        float Width,
        float Height,
        float Score,
        int ClassIndex,
        float Kp1X,
        float Kp1Y,
        float Kp2X,
        float Kp2Y);
}
