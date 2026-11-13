using System.Collections.Generic;
using System.Threading;
using BreadboardAOI.App.Orchestration;
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

    [Fact]
    public void Hand_gate_detects_stationary_skin_hand_and_blocks_stability()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var cleanWarped = new Mat();
        calibrator.Warp(cameraFrame, cleanWarped).Should().BeTrue();

        using var handFrame = cleanWarped.Clone();
        // Draw a stationary skin-toned hand silhouette (~12% area, well above 28x28 thumbnail blob)
        Cv2.Rectangle(handFrame, new Rect(350, 100, 240, 260), new Scalar(115, 150, 200), -1);

        using var gate = new HandMotionGate();
        gate.Update(handFrame).Should().BeFalse();
        gate.HasHandInFrame.Should().BeTrue();
        gate.HandCoverageRatio.Should().BeGreaterThanOrEqualTo(HandMotionGate.DefaultHandCoverageThreshold);
        gate.SkinPixelCount.Should().BeGreaterThan(2000);
        gate.IsHandFreeAndStable.Should().BeFalse();

        // Even after waiting longer than 250ms with zero frame-to-frame motion,
        // the stationary hand must keep the board unstable.
        Thread.Sleep(320);
        gate.Update(handFrame).Should().BeFalse();
        gate.MotionPixels.Should().Be(0);
        gate.HasHandInFrame.Should().BeTrue();
        gate.IsBoardStable.Should().BeFalse();
        gate.IsHandFreeAndStable.Should().BeFalse();
        gate.JustSettled.Should().BeFalse();
    }

    [Fact]
    public void Hand_gate_latches_and_increments_epoch_after_hand_leaves()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var cleanWarped = new Mat();
        calibrator.Warp(cameraFrame, cleanWarped).Should().BeTrue();

        using var handFrame = cleanWarped.Clone();
        Cv2.Rectangle(handFrame, new Rect(300, 80, 250, 280), new Scalar(110, 145, 195), -1);

        using var gate = new HandMotionGate();
        gate.Update(handFrame).Should().BeFalse();
        Thread.Sleep(300);
        gate.Update(handFrame).Should().BeFalse();
        gate.HasHandInFrame.Should().BeTrue();
        long epochBeforeClear = gate.SettledEpoch;

        // Hand leaves the breadboard
        gate.Update(cleanWarped).Should().BeFalse();
        gate.HasHandInFrame.Should().BeFalse();
        gate.IsHandFreeAndStable.Should().BeFalse();

        Thread.Sleep(320);
        gate.Update(cleanWarped).Should().BeTrue();
        gate.HasHandInFrame.Should().BeFalse();
        gate.IsBoardStable.Should().BeTrue();
        gate.IsHandFreeAndStable.Should().BeTrue();
        gate.JustSettled.Should().BeTrue();
        gate.SettledEpoch.Should().BeGreaterThan(epochBeforeClear);
    }

    [Fact]
    public void Hand_gate_ignores_small_skin_or_tan_component_patches()
    {
        using var calibrator = new HomographyCalibrator();
        using var cameraFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var warped = new Mat();
        calibrator.Warp(cameraFrame, warped).Should().BeTrue();

        // Draw several small resistor/capacitor-sized tan patches (36x24 in 1000x500 -> ~12x8 in 320x160 thumbnail, < 28x28)
        using var withParts = warped.Clone();
        for (int i = 0; i < 6; i++)
        {
            Cv2.Rectangle(withParts, new Rect(100 + i * 130, 140, 36, 24), new Scalar(115, 150, 200), -1);
        }

        using var gate = new HandMotionGate();
        gate.Update(withParts).Should().BeFalse();
        Thread.Sleep(320);
        gate.Update(withParts).Should().BeTrue();
        gate.HasHandInFrame.Should().BeFalse();
        gate.IsBoardStable.Should().BeTrue();
        gate.IsHandFreeAndStable.Should().BeTrue();
    }

    [Fact]
    public void Hand_gate_handles_grayscale_frames_and_reset_cleanly()
    {
        using var gray = new Mat(500, 1000, MatType.CV_8UC1, new Scalar(180));
        using var gate = new HandMotionGate();

        gate.Update(gray).Should().BeFalse();
        gate.HasHandInFrame.Should().BeFalse();
        gate.HandCoverageRatio.Should().Be(0);
        gate.SkinPixelCount.Should().Be(0);

        Thread.Sleep(320);
        gate.Update(gray).Should().BeTrue();
        gate.IsHandFreeAndStable.Should().BeTrue();

        using var handBgr = new Mat(500, 1000, MatType.CV_8UC3, new Scalar(230, 230, 230));
        Cv2.Rectangle(handBgr, new Rect(200, 100, 300, 280), new Scalar(115, 150, 200), -1);
        gate.Update(handBgr).Should().BeFalse();
        gate.HasHandInFrame.Should().BeTrue();

        gate.Reset();
        gate.HasHandInFrame.Should().BeFalse();
        gate.HandCoverageRatio.Should().Be(0);
        gate.SkinPixelCount.Should().Be(0);
        gate.IsBoardStable.Should().BeFalse();
        gate.IsHandFreeAndStable.Should().BeFalse();
    }

    [Fact]
    public void Markerless_fallback_detects_perspective_tilted_breadboard_without_aruco_markers()
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(24, 24, 24));
        OpenCvSharp.Point[] boardQuad =
        [
            new(180, 145),
            new(1100, 170),
            new(1140, 565),
            new(140, 535)
        ];
        Cv2.FillConvexPoly(frame, boardQuad, new Scalar(232, 230, 226));
        // Add realistic red/blue power rail lines and center IC trench inside the breadboard
        Cv2.Line(frame, new OpenCvSharp.Point(180, 175), new OpenCvSharp.Point(1100, 200), new Scalar(40, 40, 205), 2);
        Cv2.Line(frame, new OpenCvSharp.Point(145, 505), new OpenCvSharp.Point(1135, 535), new Scalar(205, 90, 40), 2);
        Cv2.Line(frame, new OpenCvSharp.Point(162, 340), new OpenCvSharp.Point(1120, 365), new Scalar(190, 190, 190), 12);

        using var calibrator = new HomographyCalibrator();
        calibrator.TryCalibrate(frame, out _).Should().BeFalse("no ArUco markers are present");
        calibrator.IsCalibrated.Should().BeFalse();

        calibrator.TryDetectBreadboardQuadFallback(frame, out Point2f[] detectedQuad).Should().BeTrue();
        detectedQuad.Should().HaveCount(4);
        // Verify TL, TR, BR, BL corner ordering
        detectedQuad[0].X.Should().BeLessThan(detectedQuad[1].X);
        detectedQuad[0].Y.Should().BeLessThan(detectedQuad[3].Y);
        detectedQuad[2].X.Should().BeGreaterThan(detectedQuad[3].X);
        detectedQuad[2].Y.Should().BeGreaterThan(detectedQuad[1].Y);

        using var warped = new Mat();
        calibrator.Warp(frame, warped).Should().BeTrue();
        warped.Size().Should().Be(new OpenCvSharp.Size(1000, 500));
        calibrator.IsMarkerlessFallbackActive.Should().BeTrue();

        // Center of warped frame must contain the bright breadboard body rather than dark desk background
        Vec3b centerPixel = warped.At<Vec3b>(250, 500);
        ((centerPixel.Item0 + centerPixel.Item1 + centerPixel.Item2) / 3).Should().BeGreaterThan(150);

        calibrator.ResetCalibration();
        calibrator.IsMarkerlessFallbackActive.Should().BeFalse();
    }

    [Fact]
    public void Markerless_fallback_returns_false_on_tiny_or_missing_board_and_uses_stretch_fallback()
    {
        using var darkFrame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(20, 20, 20));
        Cv2.Rectangle(darkFrame, new Rect(580, 320, 90, 45), new Scalar(235, 235, 235), -1);

        using var calibrator = new HomographyCalibrator();
        calibrator.TryDetectBreadboardQuadFallback(darkFrame, out Point2f[] quad).Should().BeFalse();
        quad.Should().BeEmpty();

        using var warped = new Mat();
        calibrator.Warp(darkFrame, warped).Should().BeTrue();
        warped.Size().Should().Be(new OpenCvSharp.Size(1000, 500));
        calibrator.IsMarkerlessFallbackActive.Should().BeFalse();
    }

    [Fact]
    public void Calibrated_aruco_mode_clears_markerless_fallback_flag()
    {
        using var syntheticFrame = HomographyCalibrator.CreateSyntheticBoardFrame();
        using var calibrator = new HomographyCalibrator();
        using var warped = new Mat();

        calibrator.Warp(syntheticFrame, warped).Should().BeTrue();
        calibrator.IsMarkerlessFallbackActive.Should().BeTrue();

        calibrator.TryCalibrate(syntheticFrame, out string? error).Should().BeTrue(error);
        calibrator.IsCalibrated.Should().BeTrue();

        calibrator.Warp(syntheticFrame, warped).Should().BeTrue();
        calibrator.IsMarkerlessFallbackActive.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_exposes_wire_tracker_ocr_and_hand_gate_telemetry()
    {
        using var coordinator = new InspectionPipelineCoordinator(new InspectionPipelineOptions
        {
            DemoModeEnabled = false,
            CameraSource = CameraSource.Synthetic,
            EnableCognitiveTutor = false,
            EnableVoice = false,
            EnableAffinity = false,
            EnableCameraPreviewBitmap = false
        });

        coordinator.IsWireTrackerAvailable.Should().BeTrue();
        coordinator.IsOcrAvailable.Should().BeTrue();
        coordinator.IsWireTrackerEnabled.Should().BeTrue();
        coordinator.IsIcOcrEnabled.Should().BeTrue();

        coordinator.SetWireTrackerEnabled(false);
        coordinator.IsWireTrackerEnabled.Should().BeFalse();
        coordinator.SetWireTrackerEnabled(true);
        coordinator.IsWireTrackerEnabled.Should().BeTrue();

        coordinator.SetIcOcrEnabled(false);
        coordinator.IsIcOcrEnabled.Should().BeFalse();
        coordinator.SetIcOcrEnabled(true);
        coordinator.IsIcOcrEnabled.Should().BeTrue();

        coordinator.HasHandInFrame.Should().BeFalse();
        coordinator.HandCoverageRatio.Should().Be(0);
        coordinator.SettledEpoch.Should().BeGreaterThanOrEqualTo(0);
        coordinator.ActiveTrackedWires.Should().BeEmpty();
    }

    [Fact]
    public void Coordinator_reconciles_overlapping_wires_via_wire_insertion_tracker_on_settled_frame()
    {
        using var coordinator = new InspectionPipelineCoordinator(new InspectionPipelineOptions
        {
            DemoModeEnabled = false,
            CameraSource = CameraSource.Synthetic,
            EnableCognitiveTutor = false,
            EnableVoice = false,
            EnableAffinity = false,
            EnableCameraPreviewBitmap = false
        });

        using var warped = new Mat(500, 1000, MatType.CV_8UC3, new Scalar(230, 230, 230));
        // Paint red insulation near wire 1 tips and blue insulation near wire 2 tips
        Cv2.Circle(warped, new OpenCvSharp.Point(160, 120), 8, new Scalar(30, 30, 220), -1);
        Cv2.Circle(warped, new OpenCvSharp.Point(340, 300), 8, new Scalar(30, 30, 220), -1);

        var rawDetections = new List<DetectedComponent>
        {
            new(1, ComponentKind.JumperWire, 0.92f, 150, 110, 200, 200, 160, 120, 340, 300),
            new(2, ComponentKind.Resistor, 0.95f, 400, 200, 60, 20, 410, 210, 450, 210, "220R")
        };

        var reconciled = new List<DetectedComponent>();
        coordinator.ReconcileSettledDetections(warped, rawDetections, justSettled: true, reconciled);

        coordinator.ActiveTrackedWires.Should().HaveCount(1);
        reconciled.Should().ContainSingle(c => c.Kind == ComponentKind.JumperWire);
        reconciled.Should().ContainSingle(c => c.Kind == ComponentKind.Resistor);

        // Second settled epoch adding one more wire deterministically pairs by temporal delta
        Cv2.Circle(warped, new OpenCvSharp.Point(600, 120), 8, new Scalar(220, 80, 30), -1);
        Cv2.Circle(warped, new OpenCvSharp.Point(600, 380), 8, new Scalar(220, 80, 30), -1);
        rawDetections.Add(new DetectedComponent(3, ComponentKind.JumperWire, 0.90f, 590, 120, 20, 260, 600, 120, 600, 380));

        coordinator.ReconcileSettledDetections(warped, rawDetections, justSettled: true, reconciled);
        coordinator.ActiveTrackedWires.Should().HaveCount(2);
        coordinator.ActiveTrackedWires[1].PairedByTemporalDelta.Should().BeTrue();
    }
}
