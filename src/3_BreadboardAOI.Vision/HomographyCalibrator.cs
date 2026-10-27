// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/HomographyCalibrator.cs
// PURPOSE: Detects the four ArUco corner markers (IDs 0..3), builds the 3x3
//          perspective transform and warps every camera frame into the fixed
//          1000 x 500 board space. Also renders the offline synthetic board so
//          the entire pipeline runs on machines without a camera.
// ============================================================================

using System;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace BreadboardAOI.Vision;

/// <summary>ArUco-corner calibration + perspective warp.</summary>
public sealed class HomographyCalibrator : IDisposable
{
    public const int WarpedWidth = BreadboardSpec.CanvasWidth;   // 1000
    public const int WarpedHeight = BreadboardSpec.CanvasHeight; // 500
    public const float TargetMargin = 20f;

    private readonly Dictionary _dictionary;
    private readonly DetectorParameters _detectorParameters;
    private readonly Mat _gray = new();

    private Mat _homography = new(3, 3, MatType.CV_64FC1);
    private Mat? _fallbackHomography;
    private int _fallbackWidth;
    private int _fallbackHeight;
    private bool _disposed;

    public HomographyCalibrator()
    {
        _dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
        _detectorParameters = new DetectorParameters();
    }

    public bool IsCalibrated { get; private set; }

    public int LastMarkerCount { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Current 3x3 CV_64F transform, camera pixels -> 1000 x 500 board space.</summary>
    public Mat Homography => _homography;

    /// <summary>
    /// Locates marker IDs 0 (TL), 1 (TR), 2 (BR), 3 (BL) and rebuilds the homography.
    /// Returns false (and leaves any previous calibration untouched) when a corner is missing.
    /// </summary>
    public bool TryCalibrate(Mat cameraFrame, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cameraFrame);

        error = null;
        if (cameraFrame.Empty())
        {
            error = "Calibration frame is empty.";
            LastError = error;
            return false;
        }

        if (cameraFrame.Channels() > 1)
            Cv2.CvtColor(cameraFrame, _gray, ColorConversionCodes.BGR2GRAY);
        else
            cameraFrame.CopyTo(_gray);

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
                (quad[0].X + quad[1].X + quad[2].X + quad[3].X) / 4f,
                (quad[0].Y + quad[1].Y + quad[2].Y + quad[3].Y) / 4f);
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
        if (!IsFinite(candidate))
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
        LastError = null;
        return true;
    }

    /// <summary>
    /// Warps <paramref name="source"/> into <paramref name="destination"/> at 1000 x 500.
    /// Before calibration a non-uniform fallback stretch is used so the pipeline keeps
    /// producing frames instead of freezing the UI.
    /// </summary>
    public bool Warp(Mat source, Mat destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Empty())
            return false;

        Mat transform = IsCalibrated
            ? _homography
            : GetFallbackHomography(source.Width, source.Height);

        Cv2.WarpPerspective(
            source,
            destination,
            transform,
            new OpenCvSharp.Size(WarpedWidth, WarpedHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            new Scalar(20, 20, 20));

        return true;
    }

    public void ResetCalibration()
    {
        IsCalibrated = false;
        LastMarkerCount = 0;
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
        _homography.Dispose();
        _fallbackHomography?.Dispose();
    }
}
