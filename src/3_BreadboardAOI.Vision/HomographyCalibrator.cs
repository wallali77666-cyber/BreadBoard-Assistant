// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/HomographyCalibrator.cs
// PURPOSE: Detects the four ArUco corner markers (IDs 0..3), builds the 3x3
//          perspective transform, and warps every camera frame into the fixed
//          1000 x 500 board space in <2.5 ms via fused fixed-point remap tables
//          (CV_16SC2 + CV_16UC1). Also renders the offline synthetic board so
//          the entire pipeline runs on machines without a camera.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace BreadboardAOI.Vision;

/// <summary>ArUco-corner calibration + <2.5 ms perspective warp via precomputed LUT remap.</summary>
public sealed class HomographyCalibrator : IDisposable
{
    public const int WarpedWidth = BreadboardSpec.CanvasWidth;   // 1000
    public const int WarpedHeight = BreadboardSpec.CanvasHeight; // 500
    public const float TargetMargin = 20f;
    private const int MarkerlessRefreshIntervalFrames = 30;

    private static readonly Point2f[] MarkerlessDestination =
    [
        new(0f, 0f),
        new(WarpedWidth, 0f),
        new(WarpedWidth, WarpedHeight),
        new(0f, WarpedHeight)
    ];

    private readonly Dictionary _dictionary;
    private readonly DetectorParameters _detectorParameters;
    private readonly Mat _gray = new();
    private readonly Mat _undistorted = new();
    private readonly Mat _markerlessBgr = new();
    private readonly Mat _markerlessHsv = new();
    private readonly Mat _markerlessMask = new();
    private readonly Mat _markerlessGray = new();
    private readonly Mat _markerlessEdges = new();
    private readonly Mat _markerlessCloseKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 9));
    private readonly Mat _markerlessOpenKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
    private readonly Mat _markerlessEdgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(7, 7));

    private Mat _homography = new(3, 3, MatType.CV_64FC1);
    private Mat? _fallbackHomography;
    private int _fallbackWidth;
    private int _fallbackHeight;
    private Mat? _markerlessHomography;
    private int _markerlessWidth;
    private int _markerlessHeight;
    private int _markerlessFramesSinceRefresh;
    private long _transformVersion;

    private Mat? _cameraMatrix;
    private Mat? _distortionCoefficients;
    private Mat? _undistortMap1;
    private Mat? _undistortMap2;
    private int _mapWidth;
    private int _mapHeight;

    // Precomputed fused warp LUT (1000x500 CV_16SC2 + CV_16UC1) for <2.5 ms Cv2.Remap execution.
    private Mat? _warpLutMap1;
    private Mat? _warpLutMap2;
    private int _warpLutSourceWidth;
    private int _warpLutSourceHeight;
    private bool _warpLutUsesCalibratedHomography;
    private long _warpLutTransformVersion = -1;

    private bool _disposed;

    public HomographyCalibrator()
    {
        _dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
        _detectorParameters = new DetectorParameters
        {
            CornerRefinementMethod = CornerRefineMethod.Subpix,
            CornerRefinementWinSize = 5,
            CornerRefinementMaxIterations = 25,
            CornerRefinementMinAccuracy = 0.05
        };
    }

    public bool IsCalibrated { get; private set; }

    /// <summary>True when markerless breadboard contour homography is active in place of ArUco calibration.</summary>
    public bool IsMarkerlessFallbackActive { get; private set; }

    public int LastMarkerCount { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Duration of the most recent <see cref="Warp"/> call in milliseconds (target &lt; 2.5 ms).</summary>
    public double LastWarpMs { get; private set; }

    /// <summary>Duration of the most recent <see cref="TryCalibrate"/> call in milliseconds.</summary>
    public double LastCalibrationMs { get; private set; }

    /// <summary>True when frames are lens-corrected before or during the homography warp.</summary>
    public bool HasLensCorrection => _cameraMatrix is not null && _distortionCoefficients is not null;

    /// <summary>Current 3x3 CV_64F transform, camera pixels -> 1000 x 500 board space.</summary>
    public Mat Homography => _homography;

    /// <summary>
    /// Supplies the camera intrinsics and distortion coefficients captured once for
    /// this lens (for example with an OpenCV chessboard calibration). Frames are
    /// remapped before ArUco detection and fused into the perspective warp LUT, so a
    /// barrel-distorted top/bottom rail no longer drifts outside the 11.5 px snap
    /// radius. Passing <c>null</c> for both restores the raw-camera path.
    /// </summary>
    public void SetLensDistortion(Mat? cameraMatrix, Mat? distortionCoefficients)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cameraMatrix is null || distortionCoefficients is null)
        {
            if (cameraMatrix is not null || distortionCoefficients is not null)
                throw new ArgumentException("Camera matrix and distortion coefficients must be supplied together.");

            ClearLensCorrection();
            return;
        }

        if (cameraMatrix.Empty() || cameraMatrix.Rows != 3 || cameraMatrix.Cols != 3 || cameraMatrix.Channels() != 1)
            throw new ArgumentException("Camera matrix must be a single-channel 3x3 intrinsic matrix.", nameof(cameraMatrix));

        if (distortionCoefficients.Empty() || distortionCoefficients.Channels() != 1)
            throw new ArgumentException("Distortion coefficients must be a non-empty single-channel matrix.", nameof(distortionCoefficients));

        Mat matrix = ToDouble(cameraMatrix);
        Mat coefficients = ToDouble(distortionCoefficients);
        bool adopted = false;

        try
        {
            if (coefficients.Rows != 1 && coefficients.Cols != 1)
                throw new ArgumentException("Distortion coefficients must be a single row or column.", nameof(distortionCoefficients));

            _cameraMatrix?.Dispose();
            _distortionCoefficients?.Dispose();
            _cameraMatrix = matrix;
            _distortionCoefficients = coefficients;
            adopted = true;

            ReleaseUndistortMaps();
            ReleaseWarpLut();

            // The previous homography was measured on raw pixels and is no longer
            // valid once the input coordinate system changes.
            ResetCalibration();
            LastError = null;
        }
        finally
        {
            if (!adopted)
            {
                matrix.Dispose();
                coefficients.Dispose();
            }
        }
    }

    private static Mat ToDouble(Mat source)
    {
        var destination = new Mat();
        if (source.Type() == MatType.CV_64FC1)
            source.CopyTo(destination);
        else
            source.ConvertTo(destination, MatType.CV_64FC1);
        return destination;
    }

    private void ClearLensCorrection()
    {
        _cameraMatrix?.Dispose();
        _distortionCoefficients?.Dispose();
        _cameraMatrix = null;
        _distortionCoefficients = null;
        ReleaseUndistortMaps();
        ReleaseWarpLut();
        ResetCalibration();
    }

    private void ReleaseUndistortMaps()
    {
        _undistortMap1?.Dispose();
        _undistortMap2?.Dispose();
        _undistortMap1 = null;
        _undistortMap2 = null;
        _mapWidth = 0;
        _mapHeight = 0;
    }

    private void ReleaseWarpLut()
    {
        _warpLutMap1?.Dispose();
        _warpLutMap2?.Dispose();
        _warpLutMap1 = null;
        _warpLutMap2 = null;
        _warpLutSourceWidth = 0;
        _warpLutSourceHeight = 0;
        _warpLutUsesCalibratedHomography = false;
        _warpLutTransformVersion = -1;
    }

    private void ClearMarkerlessHomography()
    {
        if (_markerlessHomography is not null)
        {
            _markerlessHomography.Dispose();
            _markerlessHomography = null;
            _markerlessWidth = 0;
            _markerlessHeight = 0;
            _transformVersion++;
        }

        IsMarkerlessFallbackActive = false;
    }

    /// <summary>Applies the pre-computed rectification maps when intrinsics are set.</summary>
    private Mat PrepareSource(Mat source)
    {
        if (!HasLensCorrection)
            return source;

        if (_undistortMap1 is null || _undistortMap2 is null
            || _mapWidth != source.Width || _mapHeight != source.Height)
        {
            ReleaseUndistortMaps();

            _undistortMap1 = new Mat();
            _undistortMap2 = new Mat();
            using var identity = new Mat();
            Cv2.InitUndistortRectifyMap(
                _cameraMatrix!,
                _distortionCoefficients!,
                identity,
                _cameraMatrix!,
                new OpenCvSharp.Size(source.Width, source.Height),
                MatType.CV_16SC2,
                _undistortMap1,
                _undistortMap2);
            _mapWidth = source.Width;
            _mapHeight = source.Height;
        }

        Cv2.Remap(
            source,
            _undistorted,
            _undistortMap1,
            _undistortMap2,
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            new Scalar(20, 20, 20));

        return _undistorted;
    }

    /// <summary>
    /// Locates marker IDs 0 (TL), 1 (TR), 2 (BR), 3 (BL) and rebuilds the homography
    /// plus the fixed-point 1000 x 500 warp LUT.
    /// Returns false (and leaves any previous calibration untouched) when a corner is missing.
    /// </summary>
    public bool TryCalibrate(Mat cameraFrame, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cameraFrame);

        long start = Stopwatch.GetTimestamp();
        error = null;

        try
        {
            if (cameraFrame.Empty())
            {
                error = "Calibration frame is empty.";
                LastError = error;
                return false;
            }

            Mat prepared = PrepareSource(cameraFrame);

            if (prepared.Channels() > 1)
                Cv2.CvtColor(prepared, _gray, ColorConversionCodes.BGR2GRAY);
            else
                prepared.CopyTo(_gray);

            CvAruco.DetectMarkers(
                _gray,
                _dictionary,
                out Point2f[][] corners,
                out int[] ids,
                _detectorParameters,
                out _);

            LastMarkerCount = ids.Length;
            if (ids.Length == 0)
            {
                error = "No ArUco markers detected in the frame.";
                LastError = error;
                return false;
            }

            var centers = new Point2f[4];
            var found = new bool[4];

            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                if (id is < 0 or > 3 || corners[i].Length < 4)
                    continue;

                Point2f[] quad = corners[i];
                centers[id] = new Point2f(
                    (quad[0].X + quad[1].X + quad[2].X + quad[3].X) * 0.25f,
                    (quad[0].Y + quad[1].Y + quad[2].Y + quad[3].Y) * 0.25f);
                found[id] = true;
            }

            for (int id = 0; id < 4; id++)
            {
                if (!found[id])
                {
                    error = $"Corner marker {id} was not detected (found {ids.Length} markers).";
                    LastError = error;
                    return false;
                }
            }

            Point2f[] destination =
            [
                new(TargetMargin, TargetMargin),
                new(WarpedWidth - TargetMargin, TargetMargin),
                new(WarpedWidth - TargetMargin, WarpedHeight - TargetMargin),
                new(TargetMargin, WarpedHeight - TargetMargin)
            ];

            Mat candidate = Cv2.GetPerspectiveTransform(centers, destination);
            if (!IsFinite(candidate) || !HasUsableDeterminant(candidate))
            {
                candidate.Dispose();
                error = "Detected corner geometry produced a degenerate homography.";
                LastError = error;
                return false;
            }

            Mat previous = _homography;
            _homography = candidate;
            previous.Dispose();

            IsCalibrated = true;
            IsMarkerlessFallbackActive = false;
            LastError = null;
            _transformVersion++;

            // Pre-build the fixed-point LUT right away so the very next Warp() runs in <2.5 ms.
            RebuildWarpLut(cameraFrame.Width, cameraFrame.Height, _homography, calibrated: true);
            return true;
        }
        finally
        {
            LastCalibrationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Attempts markerless breadboard contour detection when ArUco corner markers are not present.
    /// Locates bright low-saturation rectangular regions (or Otsu/Canny contours) covering 18%..92%
    /// of the frame with a breadboard-compatible aspect ratio (1.3..2.8), returning corners ordered
    /// as [TopLeft, TopRight, BottomRight, BottomLeft].
    /// </summary>
    public bool TryDetectBreadboardQuadFallback(Mat source, out Point2f[] quad)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        quad = Array.Empty<Point2f>();
        if (source.Empty() || source.Width < 32 || source.Height < 32)
            return false;

        double frameArea = (double)source.Width * source.Height;
        int channels = source.Channels();

        if (channels == 4)
        {
            Cv2.CvtColor(source, _markerlessBgr, ColorConversionCodes.BGRA2BGR);
            Cv2.CvtColor(_markerlessBgr, _markerlessHsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(_markerlessHsv, new Scalar(0, 0, 145), new Scalar(180, 65, 255), _markerlessMask);
        }
        else if (channels == 3)
        {
            Cv2.CvtColor(source, _markerlessHsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(_markerlessHsv, new Scalar(0, 0, 145), new Scalar(180, 65, 255), _markerlessMask);
        }
        else
        {
            Cv2.Threshold(source, _markerlessMask, 145, 255, ThresholdTypes.Binary);
        }

        Cv2.MorphologyEx(_markerlessMask, _markerlessMask, MorphTypes.Close, _markerlessCloseKernel);
        Cv2.MorphologyEx(_markerlessMask, _markerlessMask, MorphTypes.Open, _markerlessOpenKernel);

        if (TryExtractBestBreadboardQuad(_markerlessMask, frameArea, out quad))
            return true;

        // Secondary fallback via Otsu threshold & Canny contour extraction for dimmer lighting.
        if (channels == 4)
            Cv2.CvtColor(source, _markerlessGray, ColorConversionCodes.BGRA2GRAY);
        else if (channels == 3)
            Cv2.CvtColor(source, _markerlessGray, ColorConversionCodes.BGR2GRAY);
        else
            source.CopyTo(_markerlessGray);

        Cv2.GaussianBlur(_markerlessGray, _markerlessGray, new OpenCvSharp.Size(5, 5), 0);

        Cv2.Threshold(_markerlessGray, _markerlessMask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.MorphologyEx(_markerlessMask, _markerlessMask, MorphTypes.Close, _markerlessCloseKernel);
        Cv2.MorphologyEx(_markerlessMask, _markerlessMask, MorphTypes.Open, _markerlessOpenKernel);

        if (TryExtractBestBreadboardQuad(_markerlessMask, frameArea, out quad))
            return true;

        Cv2.Canny(_markerlessGray, _markerlessEdges, 50, 150);
        Cv2.MorphologyEx(_markerlessEdges, _markerlessEdges, MorphTypes.Close, _markerlessEdgeKernel);

        return TryExtractBestBreadboardQuad(_markerlessEdges, frameArea, out quad);
    }

    private static bool TryExtractBestBreadboardQuad(Mat binaryMask, double frameArea, out Point2f[] quad)
    {
        quad = Array.Empty<Point2f>();
        Cv2.FindContours(
            binaryMask,
            out OpenCvSharp.Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0)
            return false;

        var candidates = new (int Index, double Area)[contours.Length];
        int candidateCount = 0;
        double minArea = frameArea * 0.18;
        double maxArea = frameArea * 0.92;

        for (int i = 0; i < contours.Length; i++)
        {
            double area = Cv2.ContourArea(contours[i]);
            if (area >= minArea && area <= maxArea)
                candidates[candidateCount++] = (i, area);
        }

        if (candidateCount == 0)
            return false;

        Array.Sort(candidates, 0, candidateCount, Comparer<(int Index, double Area)>.Create(
            (a, b) => b.Area.CompareTo(a.Area)));

        for (int c = 0; c < candidateCount; c++)
        {
            OpenCvSharp.Point[] contour = contours[candidates[c].Index];
            double contourArea = candidates[c].Area;
            double perimeter = Cv2.ArcLength(contour, closed: true);
            if (perimeter <= 0)
                continue;

            Point2f[]? rawCorners = null;
            OpenCvSharp.Point[] approx = Cv2.ApproxPolyDP(contour, 0.02 * perimeter, closed: true);
            if (approx.Length != 4)
                approx = Cv2.ApproxPolyDP(contour, 0.035 * perimeter, closed: true);

            if (approx.Length != 4)
            {
                OpenCvSharp.Point[] hull = Cv2.ConvexHull(contour);
                double hullPerim = Cv2.ArcLength(hull, closed: true);
                if (hullPerim > 0)
                {
                    OpenCvSharp.Point[] hullApprox = Cv2.ApproxPolyDP(hull, 0.025 * hullPerim, closed: true);
                    if (hullApprox.Length == 4)
                        approx = hullApprox;
                }
            }

            if (approx.Length == 4)
            {
                rawCorners =
                [
                    new(approx[0].X, approx[0].Y),
                    new(approx[1].X, approx[1].Y),
                    new(approx[2].X, approx[2].Y),
                    new(approx[3].X, approx[3].Y)
                ];
            }
            else
            {
                RotatedRect minRect = Cv2.MinAreaRect(contour);
                double rectArea = (double)minRect.Size.Width * minRect.Size.Height;
                if (rectArea <= 0 || contourArea / rectArea < 0.65)
                    continue;

                rawCorners = minRect.Points();
            }

            if (rawCorners is null || rawCorners.Length != 4)
                continue;

            Point2f[] ordered = OrderQuadTopLeftClockwise(rawCorners);
            float topW = Distance(ordered[0], ordered[1]);
            float botW = Distance(ordered[3], ordered[2]);
            float leftH = Distance(ordered[0], ordered[3]);
            float rightH = Distance(ordered[1], ordered[2]);

            float avgW = (topW + botW) * 0.5f;
            float avgH = (leftH + rightH) * 0.5f;
            if (avgW < 40f || avgH < 20f)
                continue;

            if (Math.Min(topW, botW) / Math.Max(topW, botW) < 0.45f
                || Math.Min(leftH, rightH) / Math.Max(leftH, rightH) < 0.45f)
            {
                continue;
            }

            float aspect = avgW / avgH;
            if (aspect is < 1.3f or > 2.8f)
                continue;

            using Mat transform = Cv2.GetPerspectiveTransform(ordered, MarkerlessDestination);
            if (!IsFinite(transform) || !HasUsableDeterminant(transform))
                continue;

            quad = ordered;
            return true;
        }

        return false;
    }

    private static Point2f[] OrderQuadTopLeftClockwise(Point2f[] points)
    {
        float cx = (points[0].X + points[1].X + points[2].X + points[3].X) * 0.25f;
        float cy = (points[0].Y + points[1].Y + points[2].Y + points[3].Y) * 0.25f;

        var sorted = new Point2f[4];
        Array.Copy(points, sorted, 4);
        Array.Sort(sorted, (a, b) =>
        {
            double angleA = Math.Atan2(a.Y - cy, a.X - cx);
            double angleB = Math.Atan2(b.Y - cy, b.X - cx);
            return angleA.CompareTo(angleB);
        });

        int tlIndex = 0;
        float minSum = sorted[0].X + sorted[0].Y;
        for (int i = 1; i < 4; i++)
        {
            float sum = sorted[i].X + sorted[i].Y;
            if (sum < minSum)
            {
                minSum = sum;
                tlIndex = i;
            }
        }

        return
        [
            sorted[tlIndex],
            sorted[(tlIndex + 1) % 4],
            sorted[(tlIndex + 2) % 4],
            sorted[(tlIndex + 3) % 4]
        ];
    }

    private static float Distance(Point2f a, Point2f b)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Warps <paramref name="source"/> into <paramref name="destination"/> at 1000 x 500 in &lt;2.5 ms
    /// using precomputed fixed-point <c>CV_16SC2</c> / <c>CV_16UC1</c> remap tables.
    /// Before calibration, uses markerless breadboard contour homography when detected, or
    /// falls back to a non-uniform stretch so the pipeline keeps producing frames.
    /// </summary>
    public bool Warp(Mat source, Mat destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Empty())
            return false;

        long start = Stopwatch.GetTimestamp();

        try
        {
            Mat prepared = PrepareSource(source);

            Mat transform;
            if (IsCalibrated)
            {
                IsMarkerlessFallbackActive = false;
                transform = _homography;
            }
            else
            {
                bool needsRefresh = _markerlessHomography is null
                                    || _markerlessWidth != prepared.Width
                                    || _markerlessHeight != prepared.Height
                                    || _markerlessFramesSinceRefresh >= MarkerlessRefreshIntervalFrames;

                if (needsRefresh)
                {
                    _markerlessFramesSinceRefresh = 0;
                    if (TryDetectBreadboardQuadFallback(prepared, out Point2f[] detectedQuad))
                    {
                        Mat candidate = Cv2.GetPerspectiveTransform(detectedQuad, MarkerlessDestination);
                        if (IsFinite(candidate) && HasUsableDeterminant(candidate))
                        {
                            _markerlessHomography?.Dispose();
                            _markerlessHomography = candidate;
                            _markerlessWidth = prepared.Width;
                            _markerlessHeight = prepared.Height;
                            _transformVersion++;
                        }
                        else
                        {
                            candidate.Dispose();
                            ClearMarkerlessHomography();
                        }
                    }
                    else
                    {
                        ClearMarkerlessHomography();
                    }
                }
                else
                {
                    _markerlessFramesSinceRefresh++;
                }

                if (_markerlessHomography is not null)
                {
                    IsMarkerlessFallbackActive = true;
                    transform = _markerlessHomography;
                }
                else
                {
                    IsMarkerlessFallbackActive = false;
                    transform = GetFallbackHomography(prepared.Width, prepared.Height);
                }
            }

            EnsureWarpLut(prepared.Width, prepared.Height, transform, IsCalibrated);

            if (_warpLutMap1 is not null && _warpLutMap2 is not null)
            {
                Cv2.Remap(
                    prepared,
                    destination,
                    _warpLutMap1,
                    _warpLutMap2,
                    InterpolationFlags.Linear,
                    BorderTypes.Constant,
                    new Scalar(20, 20, 20));
            }
            else
            {
                Cv2.WarpPerspective(
                    prepared,
                    destination,
                    transform,
                    new OpenCvSharp.Size(WarpedWidth, WarpedHeight),
                    InterpolationFlags.Linear,
                    BorderTypes.Constant,
                    new Scalar(20, 20, 20));
            }

            return true;
        }
        finally
        {
            LastWarpMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    private void EnsureWarpLut(int sourceWidth, int sourceHeight, Mat transform, bool calibrated)
    {
        if (_warpLutMap1 is not null
            && _warpLutMap2 is not null
            && _warpLutSourceWidth == sourceWidth
            && _warpLutSourceHeight == sourceHeight
            && _warpLutUsesCalibratedHomography == calibrated
            && _warpLutTransformVersion == _transformVersion)
        {
            return;
        }

        RebuildWarpLut(sourceWidth, sourceHeight, transform, calibrated);
    }

    private unsafe void RebuildWarpLut(int sourceWidth, int sourceHeight, Mat transform, bool calibrated)
    {
        ReleaseWarpLut();

        using var inverse = new Mat();
        Cv2.Invert(transform, inverse, DecompTypes.LU);
        if (inverse.Empty() || !IsFinite(inverse))
            return;

        double m00 = inverse.At<double>(0, 0), m01 = inverse.At<double>(0, 1), m02 = inverse.At<double>(0, 2);
        double m10 = inverse.At<double>(1, 0), m11 = inverse.At<double>(1, 1), m12 = inverse.At<double>(1, 2);
        double m20 = inverse.At<double>(2, 0), m21 = inverse.At<double>(2, 1), m22 = inverse.At<double>(2, 2);

        using var floatMapX = new Mat(WarpedHeight, WarpedWidth, MatType.CV_32FC1);
        using var floatMapY = new Mat(WarpedHeight, WarpedWidth, MatType.CV_32FC1);

        float* baseX = (float*)floatMapX.Data;
        float* baseY = (float*)floatMapY.Data;
        int strideX = (int)(floatMapX.Step() / sizeof(float));
        int strideY = (int)(floatMapY.Step() / sizeof(float));

        for (int y = 0; y < WarpedHeight; y++)
        {
            float* rowX = baseX + y * strideX;
            float* rowY = baseY + y * strideY;

            double numXBase = m01 * y + m02;
            double numYBase = m11 * y + m12;
            double denBase = m21 * y + m22;

            for (int x = 0; x < WarpedWidth; x++)
            {
                double w = m20 * x + denBase;
                double invW = Math.Abs(w) > 1e-12 ? 1.0 / w : 0.0;
                rowX[x] = (float)((m00 * x + numXBase) * invW);
                rowY[x] = (float)((m10 * x + numYBase) * invW);
            }
        }

        _warpLutMap1 = new Mat();
        _warpLutMap2 = new Mat();
        Cv2.ConvertMaps(floatMapX, floatMapY, _warpLutMap1, _warpLutMap2, MatType.CV_16SC2, false);

        _warpLutSourceWidth = sourceWidth;
        _warpLutSourceHeight = sourceHeight;
        _warpLutUsesCalibratedHomography = calibrated;
        _warpLutTransformVersion = _transformVersion;
    }

    public void ResetCalibration()
    {
        IsCalibrated = false;
        LastMarkerCount = 0;
        _markerlessFramesSinceRefresh = 0;
        ClearMarkerlessHomography();
        _transformVersion++;
        ReleaseWarpLut();
    }

    private Mat GetFallbackHomography(int sourceWidth, int sourceHeight)
    {
        if (_fallbackHomography is not null
            && _fallbackWidth == sourceWidth
            && _fallbackHeight == sourceHeight)
        {
            return _fallbackHomography;
        }

        _fallbackHomography?.Dispose();

        Point2f[] source =
        [
            new(0, 0),
            new(sourceWidth, 0),
            new(sourceWidth, sourceHeight),
            new(0, sourceHeight)
        ];

        Point2f[] destination =
        [
            new(0, 0),
            new(WarpedWidth, 0),
            new(WarpedWidth, WarpedHeight),
            new(0, WarpedHeight)
        ];

        _fallbackHomography = Cv2.GetPerspectiveTransform(source, destination);
        _fallbackWidth = sourceWidth;
        _fallbackHeight = sourceHeight;
        _transformVersion++;
        return _fallbackHomography;
    }

    private static bool IsFinite(Mat matrix)
    {
        for (int row = 0; row < matrix.Rows; row++)
        {
            for (int column = 0; column < matrix.Cols; column++)
            {
                if (!double.IsFinite(matrix.At<double>(row, column)))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rejects a nearly singular 3x3 matrix: corner points that are almost
    /// collinear produce a huge, unstable warp even when every element is finite.
    /// </summary>
    private static bool HasUsableDeterminant(Mat matrix)
    {
        if (matrix.Rows != 3 || matrix.Cols != 3)
            return false;

        double a = matrix.At<double>(0, 0), b = matrix.At<double>(0, 1), c = matrix.At<double>(0, 2);
        double d = matrix.At<double>(1, 0), e = matrix.At<double>(1, 1), f = matrix.At<double>(1, 2);
        double g = matrix.At<double>(2, 0), h = matrix.At<double>(2, 1), i = matrix.At<double>(2, 2);

        double determinant = a * (e * i - f * h)
                             - b * (d * i - f * g)
                             + c * (d * h - e * g);

        return double.IsFinite(determinant) && Math.Abs(determinant) > 1e-9;
    }

    /// <summary>
    /// Renders the offline 1280 x 720 camera substitute: a scaled breadboard with
    /// the four calibration markers at IDs 0..3. The pattern is deterministic so
    /// homography, motion gate and overlay can be exercised without hardware.
    /// </summary>
    public static Mat CreateSyntheticBoardFrame(int width = CameraCaptureService.DefaultWidth, int height = CameraCaptureService.DefaultHeight)
    {
        if (width < 320 || height < 240)
            throw new ArgumentOutOfRangeException(nameof(width), "Synthetic frames need at least 320 x 240 pixels.");

        var frame = new Mat(height, width, MatType.CV_8UC3, new Scalar(20, 20, 20));

        try
        {
            int boardWidth = Math.Min(width - 80, (int)(width * 0.82f));
            int boardHeight = boardWidth / 2;

            if (boardHeight > height - 80)
            {
                boardHeight = height - 80;
                boardWidth = boardHeight * 2;
            }

            int boardX = (width - boardWidth) / 2;
            int boardY = (height - boardHeight) / 2;
            float scaleX = boardWidth / (float)WarpedWidth;
            float scaleY = boardHeight / (float)WarpedHeight;

            OpenCvSharp.Point ToScreen(float px, float py) => new(
                (int)MathF.Round(boardX + px * scaleX),
                (int)MathF.Round(boardY + py * scaleY));

            // Board body + border.
            Cv2.Rectangle(frame, new Rect(boardX, boardY, boardWidth, boardHeight), new Scalar(232, 232, 232), -1);
            Cv2.Rectangle(frame, new Rect(boardX, boardY, boardWidth, boardHeight), new Scalar(90, 90, 90), 2);

            // Center IC trench.
            OpenCvSharp.Point trenchTopLeft = ToScreen(0, 175);
            OpenCvSharp.Point trenchBottomRight = ToScreen(1000, 225);
            Cv2.Rectangle(
                frame,
                new Rect(trenchTopLeft.X, trenchTopLeft.Y, trenchBottomRight.X - trenchTopLeft.X, trenchBottomRight.Y - trenchTopLeft.Y),
                new Scalar(205, 205, 205),
                -1);

            // Power rails: red = VCC, blue = GND.
            Cv2.Line(frame, ToScreen(0, BreadboardSpec.TopVccRailY), ToScreen(1000, BreadboardSpec.TopVccRailY), new Scalar(40, 40, 200), 2);
            Cv2.Line(frame, ToScreen(0, BreadboardSpec.TopGndRailY), ToScreen(1000, BreadboardSpec.TopGndRailY), new Scalar(200, 90, 40), 2);
            Cv2.Line(frame, ToScreen(0, BreadboardSpec.BottomVccRailY), ToScreen(1000, BreadboardSpec.BottomVccRailY), new Scalar(40, 40, 200), 2);
            Cv2.Line(frame, ToScreen(0, BreadboardSpec.BottomGndRailY), ToScreen(1000, BreadboardSpec.BottomGndRailY), new Scalar(200, 90, 40), 2);

            // All 830 tie points.
            int holeRadius = Math.Max(2, (int)(boardWidth / 420f));
            foreach (HoleCoord hole in PinMatrixLookup.Shared.Holes)
                Cv2.Circle(frame, ToScreen(hole.Px, hole.Py), holeRadius, new Scalar(45, 45, 45), -1);

            // Corner ArUco markers (quiet zone + marker).
            using var dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
            int markerSize = Math.Clamp(boardWidth / 16, 48, 120);
            int quietZone = Math.Max(8, markerSize / 8);

            ReadOnlySpan<(int Id, float Px, float Py)> markerAnchors =
            [
                (0, TargetMargin, TargetMargin),
                (1, WarpedWidth - TargetMargin, TargetMargin),
                (2, WarpedWidth - TargetMargin, WarpedHeight - TargetMargin),
                (3, TargetMargin, WarpedHeight - TargetMargin)
            ];

            foreach ((int id, float px, float py) in markerAnchors)
            {
                OpenCvSharp.Point center = ToScreen(px, py);
                int left = center.X - markerSize / 2;
                int top = center.Y - markerSize / 2;

                Cv2.Rectangle(
                    frame,
                    new Rect(left - quietZone, top - quietZone, markerSize + quietZone * 2, markerSize + quietZone * 2),
                    Scalar.White,
                    -1);

                using var marker = new Mat(markerSize, markerSize, MatType.CV_8UC1);
                dictionary.GenerateImageMarker(id, markerSize, marker, 1);

                using var markerBgr = new Mat();
                Cv2.CvtColor(marker, markerBgr, ColorConversionCodes.GRAY2BGR);

                using var markerRoi = new Mat(frame, new Rect(left, top, markerSize, markerSize));
                markerBgr.CopyTo(markerRoi);
            }

            Cv2.PutText(
                frame,
                "SYNTHETIC TEST BOARD - NO CAMERA",
                new OpenCvSharp.Point(24, 34),
                HersheyFonts.HersheySimplex,
                0.65,
                new Scalar(130, 130, 130),
                1,
                LineTypes.AntiAlias);

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dictionary.Dispose();
        _gray.Dispose();
        _undistorted.Dispose();
        _markerlessBgr.Dispose();
        _markerlessHsv.Dispose();
        _markerlessMask.Dispose();
        _markerlessGray.Dispose();
        _markerlessEdges.Dispose();
        _markerlessCloseKernel.Dispose();
        _markerlessOpenKernel.Dispose();
        _markerlessEdgeKernel.Dispose();
        _homography.Dispose();
        _fallbackHomography?.Dispose();
        _markerlessHomography?.Dispose();
        _cameraMatrix?.Dispose();
        _distortionCoefficients?.Dispose();
        ReleaseUndistortMaps();
        ReleaseWarpLut();
    }
}
