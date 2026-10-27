// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/CameraCaptureServiceTests.cs
// PURPOSE: Verifies the still-image input path used by the "LOAD IMAGE" toolbar
//          button, viewport drag & drop and BREADBOARD_IMAGE_PATH. The service
//          must letterbox photos into the fixed 1280 x 720 capture frame and
//          degrade to the synthetic board when the file is missing or corrupt.
// ============================================================================

using System;
using System.IO;
using BreadboardAOI.Vision;
using FluentAssertions;
using OpenCvSharp;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class CameraCaptureServiceTests : IDisposable
{
    private readonly string _sandbox;

    public CameraCaptureServiceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "breadboardaoi-image-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp cleaner handles leftovers.
        }
    }

    [Fact]
    public void ImageFile_WithTwoToOnePhoto_IsLetterboxedIntoCaptureFrame()
    {
        string path = Path.Combine(_sandbox, "board.png");
        using (var source = new Mat(100, 200, MatType.CV_8UC3, new Scalar(0, 0, 255)))
        {
            Cv2.ImWrite(path, source);
        }

        using var service = new CameraCaptureService(CameraSource.ImageFile, imagePath: path);
        using var frame = new Mat();

        service.ActiveSource.Should().Be(CameraSource.ImageFile);
        service.IsStillImage.Should().BeTrue();
        service.IsSynthetic.Should().BeFalse();
        service.ImagePath.Should().Be(path);
        service.TryGrabFrame(frame).Should().BeTrue();

        frame.Width.Should().Be(CameraCaptureService.DefaultWidth);
        frame.Height.Should().Be(CameraCaptureService.DefaultHeight);

        // 2:1 photo scaled to 1280 x 640 leaves a 40 px black bar top and bottom.
        var bar = frame.At<Vec3b>(10, 640);
        bar.Item0.Should().Be(0);
        bar.Item1.Should().Be(0);
        bar.Item2.Should().Be(0);

        var centre = frame.At<Vec3b>(360, 640);
        centre.Item2.Should().BeGreaterThan(200, "the red board pixel must survive the letterbox resize");
        centre.Item0.Should().BeLessThan(60);
    }

    [Fact]
    public void ImageFile_ReplaysTheSamePhotoEveryFrame()
    {
        string path = Path.Combine(_sandbox, "replay.png");
        using (var source = new Mat(60, 60, MatType.CV_8UC3, new Scalar(0, 255, 0)))
        {
            Cv2.ImWrite(path, source);
        }

        using var service = new CameraCaptureService(CameraSource.ImageFile, imagePath: path);
        using var first = new Mat();
        using var second = new Mat();

        service.TryGrabFrame(first).Should().BeTrue();
        service.TryGrabFrame(second).Should().BeTrue();

        service.FramesCaptured.Should().Be(2);
        Cv2.Norm(first, second, NormTypes.INF).Should().Be(0d);
    }

    [Fact]
    public void ImageFile_WithoutPath_FallsBackToSyntheticBoard()
    {
        using var service = new CameraCaptureService(CameraSource.ImageFile);

        service.ActiveSource.Should().Be(CameraSource.Synthetic);
        service.IsSynthetic.Should().BeTrue();
        service.IsStillImage.Should().BeFalse();
        service.LastError.Should().NotBeNullOrWhiteSpace().And.Contain("No still image");
    }

    [Fact]
    public void ImageFile_WithMissingFile_FallsBackToSyntheticBoard()
    {
        string missing = Path.Combine(_sandbox, "not-there.png");

        using var service = new CameraCaptureService(CameraSource.ImageFile, imagePath: missing);

        service.ActiveSource.Should().Be(CameraSource.Synthetic);
        service.LastError.Should().NotBeNullOrWhiteSpace().And.Contain("not-there.png");
    }

    [Fact]
    public void ImageFile_WithUndecodableFile_FallsBackToSyntheticBoard()
    {
        string corrupt = Path.Combine(_sandbox, "corrupt.png");
        File.WriteAllText(corrupt, "this is not a PNG");

        using var service = new CameraCaptureService(CameraSource.ImageFile, imagePath: corrupt);

        service.ActiveSource.Should().Be(CameraSource.Synthetic);
        service.LastError.Should().NotBeNullOrWhiteSpace().And.Contain("could not be decoded");
    }
}
