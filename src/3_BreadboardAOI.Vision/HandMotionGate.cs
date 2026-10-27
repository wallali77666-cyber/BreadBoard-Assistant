// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/HandMotionGate.cs
// PURPOSE: Blocks DRC/Qwen evaluations while a hand is inside the frame and
//          latches IsBoardStable only after 250 ms of continuous board stability.
// ============================================================================

using System;
using System.Diagnostics;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>
/// Frame-to-frame <c>AbsDiff</c> motion detector operating on the warped
/// 1000 x 500 board. Work happens on a 320 x 160 grayscale thumbnail so the
/// gate costs well under a millisecond per frame.
/// </summary>
public sealed class HandMotionGate : IDisposable
{
    public const double DefaultStableThresholdMs = 250d;
    public const double DefaultMotionPixelRatio = 0.0035; // 0.35 % of thumbnail pixels
    public const byte DefaultPixelDeltaThreshold = 18;

    private static readonly OpenCvSharp.Size ThumbnailSize = new(320, 160);

    private readonly Mat _gray = new();
    private readonly Mat _smallCurrent = new();
    private readonly Mat _smallPrevious = new();
    private readonly Mat _diff = new();
    private readonly Mat _threshold = new();

    private readonly double _stableThresholdMs;
    private readonly double _motionPixelRatio;
    private readonly byte _pixelDeltaThreshold;

    private bool _hasPrevious;
    private bool _disposed;
    private long _stableSinceTimestamp;
    private int _motionPixels;

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

    /// <summary>True only after the board has stayed still for the configured debounce window.</summary>
    public bool IsBoardStable { get; private set; }

    /// <summary>Number of thumbnail pixels that changed beyond the threshold on the last frame.</summary>
    public int MotionPixels => _motionPixels;

    /// <summary>Current contiguous still duration in milliseconds.</summary>
    public double StableElapsedMs => Stopwatch.GetElapsedTime(_stableSinceTimestamp).TotalMilliseconds;

    /// <summary>Feeds the latest warped BGR (or grayscale) frame and returns the debounced stability state.</summary>
    public bool Update(Mat warpedFrame)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ObjectDisposedException.ThrowIf(_disposed, this);

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

        if (!_hasPrevious)
        {
            _smallCurrent.CopyTo(_smallPrevious);
            _hasPrevious = true;
            _stableSinceTimestamp = Stopwatch.GetTimestamp();
            IsBoardStable = false;
            return false;
        }

        Cv2.Absdiff(_smallCurrent, _smallPrevious, _diff);
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
            IsBoardStable = true;
        }

        _smallCurrent.CopyTo(_smallPrevious);
        return IsBoardStable;
    }

    /// <summary>Forces the gate back to "moving", e.g. after a calibration or camera switch.</summary>
    public void Reset()
    {
        _hasPrevious = false;
        _motionPixels = 0;
        IsBoardStable = false;
        _stableSinceTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gray.Dispose();
        _smallCurrent.Dispose();
        _smallPrevious.Dispose();
        _diff.Dispose();
        _threshold.Dispose();
    }
}
