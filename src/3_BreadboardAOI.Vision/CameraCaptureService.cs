// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/CameraCaptureService.cs
// PURPOSE: 60 FPS frame source for the reflex loop. Exposes an explicit
//          CameraSource selector (Synthetic / Camera0 / Camera1 / ImageFile) so
//          the toolbar can toggle a live USB webcam or replay an uploaded photo,
//          and falls back to the rendered synthetic breadboard when neither a
//          camera nor a usable image is available.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using BreadboardAOI.Hardware;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>Frame source selected by the operator.</summary>
public enum CameraSource
{
    /// <summary>Offline rendered breadboard with ArUco corner markers (no hardware required).</summary>
    Synthetic = 0,

    /// <summary>OpenCV capture device index 0.</summary>
    Camera0 = 1,

    /// <summary>OpenCV capture device index 1.</summary>
    Camera1 = 2,

    /// <summary>Still image loaded from disk and replayed every frame (toolbar "Load Image").</summary>
    ImageFile = 3
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

    private VideoCapture? _capture;
    private long _framesCaptured;
    private bool _disposed;

    public CameraCaptureService(
        CameraSource source = CameraSource.Synthetic,
        int width = DefaultWidth,
        int height = DefaultHeight,
        string? imagePath = null)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Capture width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Capture height must be positive.");

        RequestedSource = source;
        ImagePath = source == CameraSource.ImageFile ? imagePath : null;
        _requestedWidth = width;
        _requestedHeight = height;
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

        int requestedIndex = source == CameraSource.Camera0 ? 0 : 1;
        if (TryOpenCamera(requestedIndex))
            return;

        int alternateIndex = requestedIndex == 0 ? 1 : 0;
        if (TryOpenCamera(alternateIndex))
        {
            ActiveSource = alternateIndex == 0 ? CameraSource.Camera0 : CameraSource.Camera1;
            LastError = $"Camera {requestedIndex} unavailable; using camera {alternateIndex}.";
            return;
        }

        ActiveSource = CameraSource.Synthetic;
        _syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame(width, height);
        LastError = $"No camera could be opened on index 0 or 1; using the synthetic board.";
    }

    public CameraSource RequestedSource { get; }

    /// <summary>Source actually delivering frames after auto-detection/fallback.</summary>
    public CameraSource ActiveSource { get; }

    public bool IsSynthetic => ActiveSource == CameraSource.Synthetic;

    public bool IsLiveCamera => _capture is not null;

    /// <summary>True when frames are replayed from the uploaded still image.</summary>
    public bool IsStillImage => _imageFrame is not null;

    /// <summary>Absolute path of the still image requested via <see cref="CameraSource.ImageFile"/>.</summary>
    public string? ImagePath { get; }

    public string? LastError { get; private set; }

    public int Width => _requestedWidth;

    public int Height => _requestedHeight;

    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

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
                capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.ANY);
            }

            if (!capture.IsOpened())
            {
                capture.Dispose();
                return false;
            }

            capture.Set(VideoCaptureProperties.FrameWidth, _requestedWidth);
            capture.Set(VideoCaptureProperties.FrameHeight, _requestedHeight);
            capture.Set(VideoCaptureProperties.Fps, 60);

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
                    LastError = "Camera read returned an empty frame.";
                    return false;
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

    /// <summary>Probes device indices 0 and 1 without keeping either open (for the Phase 5 toolbar).</summary>
    public static IReadOnlyList<CameraSource> ProbeAvailableCameras()
    {
        var available = new List<CameraSource>(2);

        for (int index = 0; index < 2; index++)
        {
            try
            {
                using var probe = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
                if (probe.IsOpened())
                    available.Add(index == 0 ? CameraSource.Camera0 : CameraSource.Camera1);
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
