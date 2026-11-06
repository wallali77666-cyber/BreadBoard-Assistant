// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/DiagnosticPromptBuilder.cs
// PURPOSE: Clean vision prompt for Qwen. The image is the source of truth:
//          no OpenCV or YOLO integration, no dumb prompts. When the packet carries
//          no image at all, the known fault is appended as plain sentences so
//          headless/unit tests can still verify text-only evaluation.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Intelligence;

public static class DiagnosticPromptBuilder
{
    /// <summary>
    /// Clean vision prompt for Qwen: detects the error in a humanized way,
    /// explains what is wrong, and provides repair instructions in no more than 5 sentences.
    /// Pure vision directly on the image with no OpenCV or YOLO integration.
    /// </summary>
    public const string TutorInstruction =
        "Look at this breadboard image. First, identify whether the circuit is correct or wrong: start your response with 'CIRCUIT VERDICT: CORRECT' or 'CIRCUIT VERDICT: WRONG'. " +
        "Then, in no more than 5 sentences, explain why: name the error first, then say exactly what to move, change or replace.";

    /// <summary>Prompt sent to Qwen with the board image. The image alone is the input.</summary>
    public static string Build(DrcStatePacket state) => Build(state, maxFaults: 4);

    public static string Build(DrcStatePacket state, int maxFaults)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (maxFaults < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFaults));

        if (HasImage(state))
            return TutorInstruction;

        var builder = new StringBuilder(TutorInstruction, TutorInstruction.Length + 256);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("The detector reports:");

        int emitted = 0;
        foreach (DrcViolation violation in state.Violations)
        {
            if (emitted++ >= maxFaults)
                break;

            builder.Append("- ");
            builder.Append(violation.ComponentId);
            builder.Append(" (");
            builder.Append(violation.Code);
            builder.Append("): ");
            builder.Append(violation.TechnicalMessage);

            string target = HoleLabel(violation.SuggestedTargetHole, 0);
            if (!string.Equals(target, "n/a", StringComparison.Ordinal))
            {
                builder.Append(" Move to ");
                builder.Append(target);
                builder.Append('.');
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Uploaded-photo path. The photo is the only input.</summary>
    public static string BuildPhotoInspection() => TutorInstruction;

    /// <summary>Live-camera path. The direct camera frame is the only input.</summary>
    public static string BuildLiveVisionInspection() => TutorInstruction;

    private static bool HasImage(DrcStatePacket state) =>
        state.FullBoardPngBytes is { Length: > 0 } || state.MicroCropPngBytes is { Length: > 0 };

    /// <summary>Human-readable DRC summary for the inspector dock (no JSON brackets).</summary>
    public static string BuildInspectorSummary(DrcStatePacket state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Violations.Count == 0)
        {
            return state.Components.Count == 0
                ? "No components detected yet."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"No violations across {state.Components.Count} detected component(s).");
        }

        var builder = new StringBuilder(256);
        for (int i = 0; i < state.Violations.Count; i++)
        {
            DrcViolation violation = state.Violations[i];
            if (i > 0)
                builder.AppendLine();

            builder.Append(violation.Severity switch
            {
                DrcSeverity.Critical => "CRITICAL",
                DrcSeverity.Warning => "WARNING",
                _ => "INFO"
            });
            builder.Append("  ");
            builder.Append(violation.Code);
            builder.Append("  ");
            builder.Append(violation.ComponentId);
            builder.Append("  ");
            builder.Append(violation.TechnicalMessage);
        }

        return builder.ToString();
    }

    private static string HoleLabel(HoleCoord hole, int fallbackStripId) =>
        string.IsNullOrWhiteSpace(hole.HoleId)
            ? fallbackStripId > 0 ? $"strip-{fallbackStripId}" : "n/a"
            : hole.HoleId;
}