// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/CameraCaptureService.cs
// PURPOSE: 60 FPS frame source for the reflex loop. Exposes an explicit
//          CameraSource selector (Synthetic / Camera0..3 / ImageFile / VideoFile),
//          locks USB webcam manual focus, manual exposure, white balance, and
//          single-frame buffer depth to prevent hunting/motion blur, and falls
//          back to the rendered synthetic breadboard when hardware is absent.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BreadboardAOI.Hardware;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>Frame source selected by the operator.</summary>
public enum CameraSource
{
    /// <summary>Offline rendered breadboard with ArUco corner markers (no hardware required).</summary>
    Synthetic = 0,

    /// <summary>OpenCV capture device index 0 (Primary / Integrated webcam).</summary>
    Camera0 = 1,

    /// <summary>OpenCV capture device index 1 (Secondary / External USB camera).</summary>
    Camera1 = 2,

    /// <summary>OpenCV capture device index 2 (USB camera / Microscope).</summary>
    Camera2 = 3,

    /// <summary>OpenCV capture device index 3 (USB camera).</summary>
    Camera3 = 4,

    /// <summary>Still image loaded from disk and replayed every frame (toolbar "Load Image").</summary>
    ImageFile = 5,

    /// <summary>Recorded breadboard inspection video replayed in a continuous 60 FPS loop.</summary>
    VideoFile = 6
}

/// <summary>
/// Manual USB UVC hardware lock settings so auto-focus and auto-exposure never
/// hunt when the user's hand enters the frame.
/// </summary>
public sealed record CameraHardwareLockOptions
{
    /// <summary>True to disable auto-focus and lock to <see cref="ManualFocusPosition"/>.</summary>
    public bool LockManualFocus { get; init; } = true;

    /// <summary>UVC focus step (0..255 typical; 35 corresponds to ~30 cm overhead mount).</summary>
    public double ManualFocusPosition { get; init; } = 35.0;

    /// <summary>True to disable auto-exposure and lock to <see cref="ManualExposureValue"/>.</summary>
    public bool LockManualExposure { get; init; } = true;

    /// <summary>DirectShow / MSMF log2 exposure value (-6 = ~1/64s shutter for crisp hand motion).</summary>
    public double ManualExposureValue { get; init; } = -6.0;

    /// <summary>True to lock white balance temperature so HSV wire colors stay invariant.</summary>
    public bool LockWhiteBalance { get; init; } = true;

    /// <summary>Fixed Kelvin temperature when <see cref="LockWhiteBalance"/> is enabled.</summary>
    public double WhiteBalanceKelvin { get; init; } = 4600.0;

    /// <summary>Fixed sensor gain when manual exposure is locked.</summary>
    public double ManualGain { get; init; } = 16.0;
}

/// <summary>
/// Owns the <see cref="VideoCapture"/> (or the synthetic frame) and hands frames
/// to the vision thread without blocking on the UI.
/// </summary>
public sealed class CameraCaptureService : IDisposable
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    private readonly object _sync = new();
    private readonly Mat _scratch = new();
    private readonly Mat _bgra = new();
    private readonly Mat _resized = new();
    private readonly Mat? _syntheticFrame;
    private readonly Mat? _imageFrame;
    private readonly int _requestedWidth;
    private readonly int _requestedHeight;
    private readonly CameraHardwareLockOptions _lockOptions;

    private VideoCapture? _capture;
    private bool _isVideo;
    private long _framesCaptured;
    private bool _disposed;

    public CameraCaptureService(
        CameraSource source = CameraSource.Synthetic,
        int width = DefaultWidth,
        int height = DefaultHeight,
        string? imagePath = null,
        CameraHardwareLockOptions? lockOptions = null)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Capture width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Capture height must be positive.");

        RequestedSource = source;
        ImagePath = (source is CameraSource.ImageFile or CameraSource.VideoFile) ? imagePath : null;
        _requestedWidth = width;
        _requestedHeight = height;
        _lockOptions = lockOptions ?? new CameraHardwareLockOptions();
        ActiveSource = source;

        if (source == CameraSource.Synthetic)
        {
            _syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame(width, height);
            return;
        }

        if (source == CameraSource.ImageFile)
        {
            _imageFrame = TryLoadImageFrame(imagePath, width, height, out string? imageError);
            if (_imageFrame is not null)
                return;

            ActiveSource = CameraSource.Synthetic;
            _syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame(width, height);
            LastError = imageError ?? "Still image unavailable; using the synthetic board.";
            return;
        }

        if (source == CameraSource.VideoFile)
        {
            _capture = TryOpenVideoFile(imagePath, out string? videoError);
            if (_capture is not null)
            {
                _isVideo = true;
                return;
            }

            ActiveSource = CameraSource.Synthetic;
            _syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame(width, height);
            LastError = videoError ?? "Video file unavailable; using the synthetic board.";
            return;
        }

        int requestedIndex = GetDeviceIndex(source);
        if (requestedIndex >= 0 && TryOpenCamera(requestedIndex))
            return;

        int[] alternates = [0, 1, 2, 3];
        foreach (int altIndex in alternates)
        {
            if (altIndex == requestedIndex) continue;
            if (TryOpenCamera(altIndex))
            {
                ActiveSource = GetSourceFromIndex(altIndex);
                LastError = $"Camera {requestedIndex} unavailable; using camera {altIndex}.";
                return;
            }
        }

        ActiveSource = CameraSource.Synthetic;
        _syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame(width, height);
        LastError = "No hardware camera could be opened; using the synthetic board.";
    }

    public static int GetDeviceIndex(CameraSource source) => source switch
    {
        CameraSource.Camera0 => 0,
        CameraSource.Camera1 => 1,
        CameraSource.Camera2 => 2,
        CameraSource.Camera3 => 3,
        _ => -1
    };

    public static CameraSource GetSourceFromIndex(int index) => index switch
    {
        0 => CameraSource.Camera0,
        1 => CameraSource.Camera1,
        2 => CameraSource.Camera2,
        3 => CameraSource.Camera3,
        _ => CameraSource.Camera0
    };

    public static bool IsPhysicalCamera(CameraSource source) =>
        source is CameraSource.Camera0 or CameraSource.Camera1 or CameraSource.Camera2 or CameraSource.Camera3;

    public CameraSource RequestedSource { get; }

    /// <summary>Source actually delivering frames after auto-detection/fallback.</summary>
    public CameraSource ActiveSource { get; private set; }

    public bool IsSynthetic => ActiveSource == CameraSource.Synthetic;

    public bool IsLiveCamera => _capture is not null && !_isVideo;

    public bool IsVideo => _isVideo;

    /// <summary>True when frames are replayed from the uploaded still image.</summary>
    public bool IsStillImage => _imageFrame is not null;

    /// <summary>True when UVC manual focus and exposure locks were applied to the active USB webcam.</summary>
    public bool IsHardwareLocked { get; private set; }

    /// <summary>Current focus value reported or configured on the camera.</summary>
    public double LockedFocusValue { get; private set; }

    /// <summary>Current exposure value reported or configured on the camera.</summary>
    public double LockedExposureValue { get; private set; }

    /// <summary>Absolute path of the file requested via ImageFile or VideoFile.</summary>
    public string? ImagePath { get; }

    public string? LastError { get; private set; }

    public int Width => _requestedWidth;

    public int Height => _requestedHeight;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    /// <summary>
    /// Re-applies or updates manual focus and exposure on an open live webcam at runtime.
    /// </summary>
    public bool TryLockManualFocusAndExposure(double focusPosition, double exposureValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_capture is null || !_capture.IsOpened())
                return false;

            ApplyHardwareLocks(_capture, focusPosition, exposureValue, _lockOptions.WhiteBalanceKelvin, _lockOptions.ManualGain);
            return IsHardwareLocked;
        }
    }

    /// <summary>Opens the requested device; returns false and writes <see cref="LastError"/> on failure.</summary>
    private bool TryOpenCamera(int deviceIndex)
    {
        VideoCapture? capture = null;
        try
        {
            capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.MSMF);
            }

            if (!capture.IsOpened())
            {
                capture.Dispose();
                capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.ANY);
            }

            if (!capture.IsOpened())
            {
                capture.Dispose();
                return false;
            }

            // Request MJPG FourCC first so 1280x720@60fps streams over USB 2.0 without saturating bus bandwidth.
            capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            capture.Set(VideoCaptureProperties.BufferSize, 1);
            capture.Set(VideoCaptureProperties.FrameWidth, _requestedWidth);
            capture.Set(VideoCaptureProperties.FrameHeight, _requestedHeight);
            capture.Set(VideoCaptureProperties.Fps, 60);

            ApplyHardwareLocks(
                capture,
                _lockOptions.ManualFocusPosition,
                _lockOptions.ManualExposureValue,
                _lockOptions.WhiteBalanceKelvin,
                _lockOptions.ManualGain);

            using var probe = new Mat();
            if (!capture.Read(probe) || probe.Empty())
            {
                capture.Dispose();
                LastError = $"Camera {deviceIndex} opened but delivered no frames.";
                return false;
            }

            _capture = capture;
            return true;
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            LastError = $"Camera {deviceIndex} failed: {ex.Message}";
            return false;
        }
    }

    private static VideoCapture? TryOpenVideoFile(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No video file specified; using the synthetic board.";
            return null;
        }

        if (!File.Exists(path))
        {
            error = $"Video file not found: {Path.GetFileName(path)}";
            return null;
        }

        try
        {
            var capture = new VideoCapture(path);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                error = $"Failed to open video file: {Path.GetFileName(path)}";
                return null;
            }

            using var testFrame = new Mat();
            if (!capture.Read(testFrame) || testFrame.Empty())
            {
                capture.Dispose();
                error = $"Video file contains no decodable frames: {Path.GetFileName(path)}";
                return null;
            }

            capture.Set(VideoCaptureProperties.PosFrames, 0);
            return capture;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"Video load error: {ex.Message}";
            return null;
        }
    }

    private void ApplyHardwareLocks(
        VideoCapture capture,
        double focusPosition,
        double exposureValue,
        double whiteBalanceKelvin,
        double gain)
    {
        try
        {
            bool focusApplied = false;
            bool exposureApplied = false;

            if (_lockOptions.LockManualFocus)
            {
                // Disable continuous autofocus (0 = manual on DirectShow/MSMF/V4L2) and set fixed lens step.
                focusApplied = capture.Set(VideoCaptureProperties.AutoFocus, 0);
                focusApplied |= capture.Set(VideoCaptureProperties.Focus, focusPosition);
                LockedFocusValue = focusPosition;
            }

            if (_lockOptions.LockManualExposure)
            {
                // DirectShow uses 0.25 (or 0.0) for manual exposure; V4L2/MSMF uses 1.0 for manual mode.
                exposureApplied = capture.Set(VideoCaptureProperties.AutoExposure, 0.25);
                if (!exposureApplied)
                    exposureApplied = capture.Set(VideoCaptureProperties.AutoExposure, 0.0);

                exposureApplied |= capture.Set(VideoCaptureProperties.Exposure, exposureValue);
                capture.Set(VideoCaptureProperties.Gain, gain);
                LockedExposureValue = exposureValue;
            }

            if (_lockOptions.LockWhiteBalance)
            {
                capture.Set(VideoCaptureProperties.AutoWB, 0);
                capture.Set(VideoCaptureProperties.WBTemperature, whiteBalanceKelvin);
            }

            IsHardwareLocked = focusApplied || exposureApplied;
        }
        catch
        {
            // UVC driver property exceptions must never prevent frame capture.
            IsHardwareLocked = false;
        }
    }

    /// <summary>
    /// Loads a still photo into a fixed-size BGR canvas, letterboxed to preserve
    /// the original aspect ratio. Returns null (and an operator-facing reason)
    /// when the file is missing, unreadable or not a decodable image.
    /// </summary>
    private static Mat? TryLoadImageFrame(string? path, int width, int height, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No still image selected; using the synthetic board.";
            return null;
        }

        if (!File.Exists(path))
        {
            error = $"Image not found: {Path.GetFileName(path)}";
            return null;
        }

        Mat? decoded = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (decoded.Empty())
            {
                decoded.Dispose();
                error = $"Image could not be decoded: {Path.GetFileName(path)}";
                return null;
            }

            var frame = new Mat(new OpenCvSharp.Size(width, height), MatType.CV_8UC3, Scalar.Black);
            double scale = Math.Min((double)width / decoded.Width, (double)height / decoded.Height);
            int targetWidth = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            using var scaled = new Mat();
            Cv2.Resize(
                decoded,
                scaled,
                new OpenCvSharp.Size(targetWidth, targetHeight),
                0,
                0,
                scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);

            using (var roi = new Mat(frame, new Rect((width - targetWidth) / 2, (height - targetHeight) / 2, targetWidth, targetHeight)))
            {
                scaled.CopyTo(roi);
            }

            decoded.Dispose();
            return frame;
        }
        catch (Exception ex)
        {
            decoded?.Dispose();
            error = $"Image load failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>Grabs the next frame into a caller-owned BGR Mat (reallocates only on size changes).</summary>
    public bool TryGrabFrame(Mat destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_capture is not null)
            {
                if (!_capture.Read(destination) || destination.Empty())
                {
                    if (_isVideo)
                    {
                        // Video EOF reached: rewind to start so video plays continuously
                        _capture.Set(VideoCaptureProperties.PosFrames, 0);
                        if (!_capture.Read(destination) || destination.Empty())
                        {
                            LastError = "Video looping reached end.";
                            return false;
                        }
                    }
                    else
                    {
                        LastError = "Camera read returned an empty frame.";
                        return false;
                    }
                }

                Interlocked.Increment(ref _framesCaptured);
                return true;
            }

            if (_imageFrame is not null)
            {
                _imageFrame.CopyTo(destination);
                Interlocked.Increment(ref _framesCaptured);
                return true;
            }

            if (_syntheticFrame is not null)
            {
                _syntheticFrame.CopyTo(destination);
                Interlocked.Increment(ref _framesCaptured);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Grabs a frame, converts it to BGRA and copies it row-by-row into the
    /// lock-free <see cref="PinnedFrameRingBuffer"/> used by the vision thread.
    /// Returns false when the producer must drop the frame (ring full).
    /// </summary>
    public bool TryGrabIntoRingBuffer(PinnedFrameRingBuffer ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (!TryGrabFrame(_scratch))
                return false;

            if (_scratch.Channels() == 3)
            {
                Cv2.CvtColor(_scratch, _bgra, ColorConversionCodes.BGR2BGRA);
            }
            else if (_scratch.Channels() == 4)
            {
                _scratch.CopyTo(_bgra);
            }
            else
            {
                LastError = $"Unsupported camera channel count: {_scratch.Channels()}.";
                return false;
            }

            Mat source = _bgra;
            if (_bgra.Width != ring.Width || _bgra.Height != ring.Height)
            {
                Cv2.Resize(_bgra, _resized, new OpenCvSharp.Size(ring.Width, ring.Height), 0, 0, InterpolationFlags.Area);
                source = _resized;
            }

            if (!ring.TryBeginWrite(out FrameWriteLease lease))
                return false;

            using (lease)
            {
                CopyRows(source, lease.Span, ring.Stride, ring.Width);
                lease.Publish();
            }

            return true;
        }
    }

    private static unsafe void CopyRows(Mat source, Span<byte> destination, int destinationStride, int width)
    {
        int rowBytes = width * 4;
        byte* basePointer = (byte*)source.Data;

        for (int y = 0; y < source.Height; y++)
        {
            byte* sourceRow = basePointer + y * source.Step();
            new ReadOnlySpan<byte>(sourceRow, rowBytes)
                .CopyTo(destination.Slice(y * destinationStride, rowBytes));
        }
    }

    /// <summary>Probes device indices 0 to 3 without keeping any open.</summary>
    public static IReadOnlyList<CameraSource> ProbeAvailableCameras()
    {
        var available = new List<CameraSource>(4);

        for (int index = 0; index < 4; index++)
        {
            try
            {
                using var probe = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
                if (probe.IsOpened())
                    available.Add(GetSourceFromIndex(index));
            }
            catch (Exception)
            {
                // Device probing is best-effort; absence is expressed by the empty result.
            }
        }

        return available;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_sync)
        {
            _capture?.Dispose();
            _capture = null;
            _syntheticFrame?.Dispose();
            _imageFrame?.Dispose();
            _scratch.Dispose();
            _bgra.Dispose();
            _resized.Dispose();
        }
    }
}
