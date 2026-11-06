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

    private static readonly OpenCvSharp.Size ThumbnailSize = new(320, 160);

    private readonly Mat _gray = new();
    private readonly Mat _smallCurrent = new();
    private readonly Mat _normalizedCurrent = new();
    private readonly Mat _normalizedPrevious = new();
    private readonly Mat _diff = new();
    private readonly Mat _threshold = new();
    private readonly Mat _lockedFrame = new();
    private readonly Mat _webpScratch = new();

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
            IsBoardStable = false;
            return false;
        }

        if (warpedFrame.Channels() > 1)
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

        if (ratio > _motionPixelRatio)
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
    }
}
