// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/WiringGuideModels.cs
// PURPOSE: Data contracts and models for the DevBoard-Sensor Wiring Guide &
//          Interface Tutor Studio. Represents step-by-step pin connections,
//          sensor operating principles, voltage level shifting, and verification test code.
// ============================================================================

using System;
using System.Collections.Generic;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

/// <summary>One physical pin-to-pin wiring step between a sensor and a microcontroller board.</summary>
public sealed record PinConnectionStep(
    int SensorPinNumber,
    string SensorPinName,
    string TargetBoardPin,
    string WireColor,
    string WireColorHex,
    string Explanation,
    string? Warning = null)
{
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
}

/// <summary>Comprehensive specification of a sensor/module including working principle and hardware characteristics.</summary>
public sealed record SensorInterfaceSpec(
    string SensorId,
    string DisplayName,
    string Category,
    string Summary,
    string WorkingPrinciple,
    string OperatingVoltage,
    double TypicalCurrentMa,
    string Protocol,
    string PinoutSummary,
    IReadOnlyList<string> RequiredExternalParts,
    IReadOnlyList<string> SafetyRules,
    string MinimalTestCodeIno,
    string MinimalTestCodePy);

/// <summary>Complete generated wiring and interface instruction document for a specific dev board and sensor pair.</summary>
public sealed record WiringGuideDocument(
    SensorInterfaceSpec Sensor,
    MicrocontrollerBoardType BoardType,
    string BoardName,
    bool IsLevelShiftingRequired,
    string? LevelShiftingAdvice,
    IReadOnlyList<PinConnectionStep> Steps,
    string FormattedMarkdown);
