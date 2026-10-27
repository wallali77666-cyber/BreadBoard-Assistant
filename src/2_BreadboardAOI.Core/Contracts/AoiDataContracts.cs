// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Contracts/AoiDataContracts.cs
// AUTHOR: Senior Systems Architect
// PURPOSE: Immutable, zero-allocation data contracts shared across Vision, 
//          DRC Graph Engine, SkiaSharp CAD Viewport, and Qwen3-VL Tutor.
// RULE FOR JUNIORS: Do NOT change struct field types without a PR review.
// ============================================================================

using System;
using System.Collections.Generic;

namespace BreadboardAOI.Core.Contracts;

public enum ComponentKind : byte
{
    Unknown = 0,
    JumperWire = 1,
    Resistor = 2,
    Led = 3,
    Capacitor = 4,
    IntegratedCircuit = 5,
    PushButton = 6
}

public enum DrcSeverity : byte
{
    Pass = 0,
    Warning = 1,   // e.g., Floating pin, missing decoupling cap -> Amber #E68619
    Critical = 2   // e.g., VCC-to-GND short, Same-strip short  -> Crimson #D7373F
}

public enum ViolationCode : ushort
{
    None = 0,
    ErcVccGndDirectShort = 100,
    ErcSameStripShort = 101,
    ErcFloatingLead = 102,
    ErcReversedPolarity = 103,
    DrcSchematicNetMismatch = 200
}

/// <summary>
/// Represents a single physical hole on the 1000x500 homography-warped breadboard.
/// Holes A15, B15, C15, D15, E15 have distinct (Px, Py) coordinates but share StripId = 15.
/// </summary>
public readonly record struct HoleCoord(
    string HoleId,      // e.g., "E15", "TOP_VCC_12"
    int StripId,        // Internal conductive metal clip ID (0..130)
    float Px,           // Warped X coordinate in [0..1000]
    float Py            // Warped Y coordinate in [0..500]
);

/// <summary>
/// Emitted by YOLOv11-Pose TensorRT engine for each detected part on the board.
/// </summary>
public readonly record struct DetectedComponent(
    int TrackId,
    ComponentKind Kind,
    float Confidence,
    // Bounding box of component body in 1000x500 warped space (used for 256x256 Qwen micro-crop)
    float BoxX, float BoxY, float BoxW, float BoxH,
    // Keypoint pixel coordinates of the metal leads entering the breadboard holes
    float Lead1Px, float Lead1Py,
    float Lead2Px, float Lead2Py,
    string? DecodedValueLabel = null // Populated later by Qwen3-VL (e.g., "220_OHM")
);

/// <summary>
/// A component whose metal leads have been geometrically snapped to physical breadboard holes.
/// </summary>
public readonly record struct SnappedComponent(
    DetectedComponent RawDetection,
    HoleCoord Pin1Hole,
    HoleCoord Pin2Hole,
    string Net1Name,
    string Net2Name
);

/// <summary>
/// Represents a single KiCad-style ERC/DRC violation with exact pixel vectors
/// so SkiaSharp can draw the X-Ray strip capsule and corrective Ratsnest arrow.
/// </summary>
public readonly record struct DrcViolation(
    ViolationCode Code,
    DrcSeverity Severity,
    string ComponentId,
    int OffendingStripId,
    HoleCoord BadHole,
    HoleCoord SuggestedTargetHole, // If valid target exists, draws green air-wire arrow
    string TechnicalMessage        // Fed directly into Qwen3-VL prompt & Right Inspector Dock
);

/// <summary>
/// Complete deterministic snapshot of the breadboard emitted whenever the hand gate locks.
/// </summary>
public sealed class DrcStatePacket
{
    public required long FrameNumber { get; init; }
    public required double YoloInferenceMs { get; init; }
    public required double DrcComputeMs { get; init; }
    public required int StateHash { get; init; } // Used to detect if circuit topology actually changed
    public required IReadOnlyList<SnappedComponent> Components { get; init; }
    public required IReadOnlyList<DrcViolation> Violations { get; init; }
    public byte[]? MicroCropPngBytes { get; init; } // 256x256 crop of faulty/new component for Qwen3-VL

    public bool HasCriticalShort =>
        Violations.Count > 0 && Violations[0].Severity == DrcSeverity.Critical;
}