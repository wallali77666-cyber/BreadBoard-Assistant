using System.Threading;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Hardware;
using BreadboardAOI.Vision;
using FluentAssertions;
using OpenCvSharp;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class VisionPipelineTests
{
    [Fact]
    public void Synthetic_board_is_a_1280x720_bgr_frame()
    {
        using var frame = HomographyCalibrator.CreateSyntheticBoardFrame();

        frame.Empty().Should().BeFalse();
        frame.Width.Should().Be(1280);
        frame.Height.Should().Be(720);
        frame.Channels().Should().Be(3);
    }

    [Fact]
    public void Synthetic_board_calibrates_from_four_aruco_corners_and_warps_to_1000x500()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();

        calibrator.TryCalibrate(cameraFrame, out string? error).Should().BeTrue(error);
        calibrator.IsCalibrated.Should().BeTrue();
        calibrator.LastMarkerCount.Should().BeGreaterThanOrEqualTo(4);
        calibrator.Homography.Rows.Should().Be(3);
        calibrator.Homography.Cols.Should().Be(3);

        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();
        warped.Width.Should().Be(1000);
        warped.Height.Should().Be(500);

        using var gray = new Mat();
        Cv2.CvtColor(warped, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CountNonZero(gray).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Warp_uses_a_fallback_stretch_before_calibration()
    {
        using var calibrator = new HomographyCalibrator();
        using var source = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var warped = new Mat();

        calibrator.IsCalibrated.Should().BeFalse();
        calibrator.Warp(source, warped).Should().BeTrue();
        warped.Size().Should().Be(new OpenCvSharp.Size(1000, 500));
    }

    [Fact]
    public void Lens_correction_runs_before_calibration_and_resets_a_raw_homography()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();

        calibrator.TryCalibrate(cameraFrame, out string? error).Should().BeTrue(error);
        calibrator.IsCalibrated.Should().BeTrue();

        using var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
        cameraMatrix.Set(0, 0, 1100.0);
        cameraMatrix.Set(1, 1, 1100.0);
        cameraMatrix.Set(0, 2, 640.0);
        cameraMatrix.Set(1, 2, 360.0);
        cameraMatrix.Set(2, 2, 1.0);

        using var distortion = new Mat(1, 5, MatType.CV_64FC1, Scalar.All(0));

        calibrator.SetLensDistortion(cameraMatrix, distortion);
        calibrator.HasLensCorrection.Should().BeTrue();
        calibrator.IsCalibrated.Should().BeFalse("the old homography was measured on raw pixels");

        calibrator.TryCalibrate(cameraFrame, out error).Should().BeTrue(error);

        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();
        warped.Size().Should().Be(new OpenCvSharp.Size(1000, 500));

        using var gray = new Mat();
        Cv2.CvtColor(warped, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CountNonZero(gray).Should().BeGreaterThan(0);

        calibrator.SetLensDistortion(null, null);
        calibrator.HasLensCorrection.Should().BeFalse();
        calibrator.IsCalibrated.Should().BeFalse();
    }

    [Fact]
    public void Half_a_lens_calibration_is_rejected()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraMatrix = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));

        Action act = () => calibrator.SetLensDistortion(cameraMatrix, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Hand_gate_ignores_a_global_exposure_shift()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();

        using var brighter = new Mat();
        warped.ConvertTo(brighter, -1, 0.92, 14);

        using var gate = new HandMotionGate();
        gate.Update(warped).Should().BeFalse();
        Thread.Sleep(320);

        gate.Update(brighter).Should().BeTrue("min/max normalisation removes a global exposure shift");
        gate.MotionPixels.Should().BeLessThan(100);
        gate.IsBoardStable.Should().BeTrue();
    }

    [Fact]
    public void Hand_gate_still_detects_a_local_change()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();

        using var changed = warped.Clone();
        Cv2.Rectangle(changed, new Rect(300, 150, 180, 140), new Scalar(230, 230, 230), -1);

        using var gate = new HandMotionGate();
        gate.Update(warped).Should().BeFalse();
        gate.Update(changed).Should().BeFalse();
        gate.MotionPixels.Should().BeGreaterThan(500);
    }

    [Fact]
    public void Hand_gate_debounces_static_frames_for_250ms()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();

        using var gate = new HandMotionGate();

        gate.Update(warped).Should().BeFalse();
        Thread.Sleep(320);
        gate.Update(warped).Should().BeTrue();
        gate.IsBoardStable.Should().BeTrue();
        gate.MotionPixels.Should().Be(0);
    }

    [Fact]
    public void Micro_crop_extracts_a_256x256_png()
    {
        using var frame = HomographyCalibrator.CreateSyntheticBoardFrame(640, 480);
        using var extractor = new MicroCropExtractor();

        var component = new DetectedComponent(
            TrackId: 1,
            Kind: ComponentKind.Led,
            Confidence: 0.9f,
            BoxX: 40,
            BoxY: 40,
            BoxW: 60,
            BoxH: 40,
            Lead1Px: 45,
            Lead1Py: 60,
            Lead2Px: 95,
            Lead2Py: 60);

        extractor.TryExtractPng(frame, component, out byte[] png).Should().BeTrue();
        png.Length.Should().BeGreaterThan(8);
        png[0].Should().Be(0x89);
        png[1].Should().Be(0x50); // PNG signature
        png[2].Should().Be(0x4E);
        png[3].Should().Be(0x47);

        using var decoded = Cv2.ImDecode(png, ImreadModes.Color);
        decoded.Width.Should().Be(256);
        decoded.Height.Should().Be(256);
    }

    [Fact]
    public void Synthetic_camera_hands_frames_to_the_pinned_ring_buffer()
    {
        using var camera = new CameraCaptureService(CameraSource.Synthetic, 640, 480);
        camera.IsSynthetic.Should().BeTrue();
        camera.ActiveSource.Should().Be(CameraSource.Synthetic);

        using var frame = new Mat();
        camera.TryGrabFrame(frame).Should().BeTrue();
        frame.Width.Should().Be(640);
        frame.Height.Should().Be(480);

        using var ring = new PinnedFrameRingBuffer(1000, 500);
        camera.TryGrabIntoRingBuffer(ring).Should().BeTrue();
        ring.PublishedFrames.Should().Be(1);

        ring.TryReadLatest(out FrameReadLease lease).Should().BeTrue();
        using (lease)
        {
            lease.Width.Should().Be(1000);
            lease.Height.Should().Be(500);
            lease.Span.Length.Should().Be(1000 * 500 * 4);
        }
    }

    [Fact]
    public void Yolo_engine_degrades_gracefully_when_the_model_is_missing()
    {
        using var engine = new YoloPoseInferenceEngine(new YoloPoseOptions
        {
            ModelPath = "assets/models/definitely-not-downloaded.onnx"
        });

        engine.IsAvailable.Should().BeFalse();
        engine.LastError.Should().NotBeNullOrWhiteSpace();

        using var warped = new Mat(500, 1000, MatType.CV_8UC3, new Scalar(20, 20, 20));
        var detections = new List<DetectedComponent> { default };
        engine.Infer(warped, detections).Should().Be(0);
        detections.Should().BeEmpty();
    }
}
