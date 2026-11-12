// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/HandMotionGate.cs
// PURPOSE: Blocks DRC/Qwen evaluations while a hand is inside the frame,
//          latches IsBoardStable only after 250 ms of continuous stillness,
//          and triggers a locked frame snapshot + ~22 KB WebP payload on the
//          rising edge of stability.
// ============================================================================

using System;
using System.Diagnostics;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>
/// Frame-to-frame <c>Cv2.Absdiff</c> motion detector operating on the warped
/// 1000 x 500 board. Work happens on a 320 x 160 grayscale thumbnail so the
/// gate costs well under a millisecond per frame, and upon latching after
/// 250 ms of stillness it locks the canonical board Mat and encodes a compact
/// (~22 KB) WebP snapshot for downstream VLM / telemetry consumers.
/// </summary>
public sealed class HandMotionGate : IDisposable
{
    public const double DefaultStableThresholdMs = 250d;
    public const double DefaultMotionPixelRatio = 0.0035; // 0.35 % of thumbnail pixels
    public const byte DefaultPixelDeltaThreshold = 18;
    public const int TargetWebpPayloadBytes = 22 * 1024; // ~22 KB target budget
    public const double DefaultHandCoverageThreshold = 0.045; // 4.5% of thumbnail area covered by contiguous skin/hand blob

    private const int MinHandBlobThumbnailWidth = 28;
    private const int MinHandBlobThumbnailHeight = 28;

    private static readonly OpenCvSharp.Size ThumbnailSize = new(320, 160);

    private readonly Mat _gray = new();
    private readonly Mat _smallCurrent = new();
    private readonly Mat _normalizedCurrent = new();
    private readonly Mat _normalizedPrevious = new();
    private readonly Mat _diff = new();
    private readonly Mat _threshold = new();
    private readonly Mat _lockedFrame = new();
    private readonly Mat _webpScratch = new();
    private readonly Mat _smallBgra = new();
    private readonly Mat _smallBgr = new();
    private readonly Mat _ycrcb = new();
    private readonly Mat _hsv = new();
    private readonly Mat _ycrcbMask = new();
    private readonly Mat _hsvMask = new();
    private readonly Mat _skinMask = new();
    private readonly Mat _skinMorphKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));

    private readonly double _stableThresholdMs;
    private readonly double _motionPixelRatio;
    private readonly byte _pixelDeltaThreshold;

    private bool _hasPrevious;
    private bool _disposed;
    private long _stableSinceTimestamp;
    private long _settledEpoch;
    private int _motionPixels;
    private byte[] _lockedWebpBytes = Array.Empty<byte>();

    public HandMotionGate(
        double stableThresholdMs = DefaultStableThresholdMs,
        double motionPixelRatio = DefaultMotionPixelRatio,
        byte pixelDeltaThreshold = DefaultPixelDeltaThreshold)
    {
        if (stableThresholdMs < 0)
            throw new ArgumentOutOfRangeException(nameof(stableThresholdMs));
        if (motionPixelRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(motionPixelRatio));

        _stableThresholdMs = stableThresholdMs;
        _motionPixelRatio = motionPixelRatio;
        _pixelDeltaThreshold = pixelDeltaThreshold;
        _stableSinceTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>True only after the board has stayed still for the configured 250 ms debounce window.</summary>
    public bool IsBoardStable { get; private set; }

    /// <summary>True on the exact frame where the gate transitioned from moving to stable (rising edge).</summary>
    public bool JustSettled { get; private set; }

    /// <summary>True when a physical hand/skin blob covering at least 4.5% of the board is detected.</summary>
    public bool HasHandInFrame { get; private set; }

    /// <summary>Fraction (0..1) of the 320 x 160 thumbnail covered by skin-tone hand pixels.</summary>
    public double HandCoverageRatio { get; private set; }

    /// <summary>Number of thumbnail pixels classified as skin tone after morphological filtering.</summary>
    public int SkinPixelCount { get; private set; }

    /// <summary>True only when the board has settled for 250 ms AND no hand is over the breadboard.</summary>
    public bool IsHandFreeAndStable => IsBoardStable && !HasHandInFrame;

    /// <summary>Monotonically increasing counter incremented each time the hand leaves and the board settles.</summary>
    public long SettledEpoch => _settledEpoch;

    /// <summary>Number of thumbnail pixels that changed beyond the threshold on the last frame.</summary>
    public int MotionPixels => _motionPixels;

    /// <summary>Current contiguous still duration in milliseconds.</summary>
    public double StableElapsedMs => Stopwatch.GetElapsedTime(_stableSinceTimestamp).TotalMilliseconds;

    /// <summary>Locked 1000 x 500 warped frame captured at the moment the board settled.</summary>
    public Mat LockedFrame => _lockedFrame;

    /// <summary>Compact (~22 KB) WebP-encoded snapshot of <see cref="LockedFrame"/> captured on frame lock.</summary>
    public ReadOnlyMemory<byte> LockedWebpBytes => _lockedWebpBytes;

    /// <summary>Feeds the latest warped BGR (or grayscale) frame and returns the debounced stability state.</summary>
    public bool Update(Mat warpedFrame)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        JustSettled = false;

        if (warpedFrame.Empty())
        {
            HasHandInFrame = false;
            HandCoverageRatio = 0;
            SkinPixelCount = 0;
            IsBoardStable = false;
            return false;
        }

        EvaluateHandPresence(warpedFrame);

        if (warpedFrame.Channels() == 4)
            Cv2.CvtColor(warpedFrame, _gray, ColorConversionCodes.BGRA2GRAY);
        else if (warpedFrame.Channels() == 3)
            Cv2.CvtColor(warpedFrame, _gray, ColorConversionCodes.BGR2GRAY);
        else
            warpedFrame.CopyTo(_gray);

        Cv2.Resize(_gray, _smallCurrent, ThumbnailSize, 0, 0, InterpolationFlags.Area);

        // Normalise each thumbnail to the full 0..255 range before differencing.
        // Auto-exposure or a moving shadow shifts the whole frame; without this,
        // a uniformly brighter (but otherwise identical) board looks like motion
        // for several frames and the gate never latches. Min/max scaling removes
        // that global gain/offset while preserving edges and the hand blob.
        Cv2.Normalize(_smallCurrent, _normalizedCurrent, 0, 255, NormTypes.MinMax);

        if (!_hasPrevious)
        {
            _normalizedCurrent.CopyTo(_normalizedPrevious);
            _hasPrevious = true;
            _stableSinceTimestamp = Stopwatch.GetTimestamp();
            IsBoardStable = false;
            return false;
        }

        Cv2.Absdiff(_normalizedCurrent, _normalizedPrevious, _diff);
        Cv2.Threshold(_diff, _threshold, _pixelDeltaThreshold, 255, ThresholdTypes.Binary);
        _motionPixels = Cv2.CountNonZero(_threshold);

        double ratio = _motionPixels / (double)(_smallCurrent.Width * _smallCurrent.Height);
        long now = Stopwatch.GetTimestamp();

        if (HasHandInFrame || ratio > _motionPixelRatio)
        {
            IsBoardStable = false;
            _stableSinceTimestamp = now;
        }
        else if (Stopwatch.GetElapsedTime(_stableSinceTimestamp, now).TotalMilliseconds >= _stableThresholdMs)
        {
            if (!IsBoardStable)
            {
                IsBoardStable = true;
                JustSettled = true;
                _settledEpoch++;
                LockFrameAndEncodeWebp(warpedFrame);
            }
        }

        _normalizedCurrent.CopyTo(_normalizedPrevious);
        return IsBoardStable;
    }

    private void EvaluateHandPresence(Mat warpedFrame)
    {
        int channels = warpedFrame.Channels();
        if (channels is not (3 or 4))
        {
            HasHandInFrame = false;
            HandCoverageRatio = 0;
            SkinPixelCount = 0;
            return;
        }

        if (channels == 4)
        {
            Cv2.Resize(warpedFrame, _smallBgra, ThumbnailSize, 0, 0, InterpolationFlags.Area);
            Cv2.CvtColor(_smallBgra, _smallBgr, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            Cv2.Resize(warpedFrame, _smallBgr, ThumbnailSize, 0, 0, InterpolationFlags.Area);
        }

        Cv2.CvtColor(_smallBgr, _ycrcb, ColorConversionCodes.BGR2YCrCb);
        Cv2.CvtColor(_smallBgr, _hsv, ColorConversionCodes.BGR2HSV);

        // Dual YCrCb + HSV skin-tone mask excludes neutral white/gray breadboard body (S < 30)
        // and saturated red/blue power rail lines (S > 200 or H outside 0..25).
        Cv2.InRange(_ycrcb, new Scalar(45, 135, 77), new Scalar(255, 173, 125), _ycrcbMask);
        Cv2.InRange(_hsv, new Scalar(0, 48, 60), new Scalar(25, 195, 255), _hsvMask);
        Cv2.BitwiseAnd(_ycrcbMask, _hsvMask, _skinMask);

        Cv2.MorphologyEx(_skinMask, _skinMask, MorphTypes.Open, _skinMorphKernel);
        Cv2.MorphologyEx(_skinMask, _skinMask, MorphTypes.Close, _skinMorphKernel);

        int skinPixels = Cv2.CountNonZero(_skinMask);
        double coverage = skinPixels / (double)(ThumbnailSize.Width * ThumbnailSize.Height);
        SkinPixelCount = skinPixels;
        HandCoverageRatio = coverage;

        if (coverage < DefaultHandCoverageThreshold)
        {
            HasHandInFrame = false;
            return;
        }

        Cv2.FindContours(
            _skinMask,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        double maxArea = 0;
        Rect largestBounds = default;
        for (int i = 0; i < contours.Length; i++)
        {
            double area = Cv2.ContourArea(contours[i]);
            if (area > maxArea)
            {
                maxArea = area;
                largestBounds = Cv2.BoundingRect(contours[i]);
            }
        }

        HasHandInFrame = largestBounds.Width >= MinHandBlobThumbnailWidth
                         && largestBounds.Height >= MinHandBlobThumbnailHeight;
    }

    /// <summary>
    /// Copies the locked warped frame into <paramref name="destination"/> and outputs the
    /// ~22 KB WebP byte array when the board is stable.
    /// </summary>
    public bool TryGetLockedSnapshot(Mat destination, out byte[] webpBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsBoardStable || _lockedFrame.Empty() || _lockedWebpBytes.Length == 0)
        {
            webpBytes = Array.Empty<byte>();
            return false;
        }

        _lockedFrame.CopyTo(destination);
        webpBytes = _lockedWebpBytes;
        return true;
    }

    /// <summary>
    /// Encodes any BGR/BGRA/Gray Mat into a compact WebP payload tuned to stay near ~22 KB.
    /// </summary>
    public bool EncodeWebpSnapshot(Mat source, out byte[] webpBytes, int targetBytes = TargetWebpPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        webpBytes = Array.Empty<byte>();
        if (source.Empty())
            return false;

        Mat bgrSource = EnsureBgr(source);

        // Adaptive quality search (starting at Q=72) to converge near the 22 KB target budget in <= 3 steps.
        ReadOnlySpan<int> candidateQualities = [72, 58, 45, 32];
        byte[]? best = null;

        foreach (int quality in candidateQualities)
        {
            Cv2.ImEncode(".webp", bgrSource, out byte[] encoded, new ImageEncodingParam(ImwriteFlags.WebPQuality, quality));
            if (encoded.Length > 0)
            {
                best = encoded;
                if (encoded.Length <= targetBytes)
                    break;
            }
        }

        if (best is null || best.Length == 0)
            return false;

        webpBytes = best;
        return true;
    }

    private void LockFrameAndEncodeWebp(Mat warpedFrame)
    {
        warpedFrame.CopyTo(_lockedFrame);
        if (EncodeWebpSnapshot(_lockedFrame, out byte[] encoded, TargetWebpPayloadBytes))
            _lockedWebpBytes = encoded;
    }

    private Mat EnsureBgr(Mat source)
    {
        if (source.Channels() == 3)
            return source;

        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, _webpScratch, ColorConversionCodes.BGRA2BGR);
            return _webpScratch;
        }

        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, _webpScratch, ColorConversionCodes.GRAY2BGR);
            return _webpScratch;
        }

        return source;
    }

    /// <summary>Forces the gate back to "moving", e.g. after a calibration or camera switch.</summary>
    public void Reset()
    {
        _hasPrevious = false;
        _motionPixels = 0;
        IsBoardStable = false;
        JustSettled = false;
        HasHandInFrame = false;
        HandCoverageRatio = 0;
        SkinPixelCount = 0;
        _lockedWebpBytes = Array.Empty<byte>();
        _stableSinceTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gray.Dispose();
        _smallCurrent.Dispose();
        _normalizedCurrent.Dispose();
        _normalizedPrevious.Dispose();
        _diff.Dispose();
        _threshold.Dispose();
        _lockedFrame.Dispose();
        _webpScratch.Dispose();
        _smallBgra.Dispose();
        _smallBgr.Dispose();
        _ycrcb.Dispose();
        _hsv.Dispose();
        _ycrcbMask.Dispose();
        _hsvMask.Dispose();
        _skinMask.Dispose();
        _skinMorphKernel.Dispose();
    }
}
