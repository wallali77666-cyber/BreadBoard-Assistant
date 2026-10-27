// ============================================================================
// FILE: src/3_BreadboardAOI.Vision/MicroCropExtractor.cs
// PURPOSE: Slices a 256 x 256 PNG around any faulty/new component so Qwen3-VL
//          only has to prefill a tiny image instead of the full 1080p frame.
// ============================================================================

using System;
using BreadboardAOI.Core.Contracts;
using OpenCvSharp;

namespace BreadboardAOI.Vision;

/// <summary>Extracts padded 256 x 256 component crops from the warped board.</summary>
public sealed class MicroCropExtractor : IDisposable
{
    public const int CropSize = 256;

    private readonly Mat _canvas;
    private bool _disposed;

    public MicroCropExtractor()
    {
        _canvas = new Mat(CropSize, CropSize, MatType.CV_8UC3, new Scalar(24, 24, 24));
    }

    /// <summary>
    /// Crops around <paramref name="component"/>'s bounding-box centre. Regions that
    /// fall outside the frame are padded with the dark pasteboard colour.
    /// </summary>
    public bool TryExtractPng(Mat warpedFrame, DetectedComponent component, out byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(warpedFrame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        pngBytes = Array.Empty<byte>();

        if (warpedFrame.Empty())
            return false;

        int centerX = (int)MathF.Round(component.BoxX + component.BoxW / 2f);
        int centerY = (int)MathF.Round(component.BoxY + component.BoxH / 2f);

        int half = CropSize / 2;
        int left = centerX - half;
        int top = centerY - half;

        var requested = new Rect(left, top, CropSize, CropSize);
        Rect available = Rect.Intersect(requested, new Rect(0, 0, warpedFrame.Width, warpedFrame.Height));

        if (available.Width <= 0 || available.Height <= 0)
            return false;

        _canvas.SetTo(new Scalar(24, 24, 24));

        using (var sourceRoi = new Mat(warpedFrame, available))
        using (var destinationRoi = new Mat(_canvas, new Rect(available.X - left, available.Y - top, available.Width, available.Height)))
        {
            ConvertChannels(sourceRoi, destinationRoi);
        }

        return Cv2.ImEncode(".png", _canvas, out pngBytes);
    }

    private readonly Mat _converted = new();

    private void ConvertChannels(Mat source, Mat destination)
    {
        if (source.Channels() == 3)
        {
            source.CopyTo(destination);
            return;
        }

        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, _converted, ColorConversionCodes.BGRA2BGR);
            _converted.CopyTo(destination);
            return;
        }

        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, _converted, ColorConversionCodes.GRAY2BGR);
            _converted.CopyTo(destination);
            return;
        }

        source.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _canvas.Dispose();
        _converted.Dispose();
    }
}
