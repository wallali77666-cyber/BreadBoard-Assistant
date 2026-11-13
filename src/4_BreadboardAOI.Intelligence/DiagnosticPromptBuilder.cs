// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/DiagnosticPromptBuilder.cs
// PURPOSE: The vision tutor receives exactly two things: this instruction and
//          the raw camera image. No YOLO detections, no DRC violations and no
//          crop are injected, so the model judges the same picture a human
//          would paste into a chat client.
// ============================================================================

using System;
using System.Globalization;
using System.Text;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Intelligence;

public static class DiagnosticPromptBuilder
{
    /// <summary>
    /// The only prompt sent to the vision model. The image is the sole input.
    /// </summary>
    public const string TutorInstruction =
        "Look at this breadboard image. First, decide whether the circuit is correct or wrong based only on the connections visible in the image. " +
        "Start your response with 'CIRCUIT VERDICT: CORRECT' or 'CIRCUIT VERDICT: WRONG'. " +
        "If a component is not connected to a complete powered path, the verdict is WRONG; do not assume wires or connections that are not visible. " +
        "Then, in no more than 5 sentences, explain what is visibly wrong and give simple instructions on how to solve it.";

    /// <summary>The prompt is constant; the packet is accepted for call-site compatibility.</summary>
    public static string Build(DrcStatePacket state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TutorInstruction;
    }

    /// <summary>Uploaded-photo path. The photo is the only input.</summary>
    public static string BuildPhotoInspection() => TutorInstruction;

    /// <summary>Live-camera path. The direct camera frame is the only input.</summary>
    public static string BuildLiveVisionInspection() => TutorInstruction;

    /// <summary>Human-readable DRC summary for the inspector dock (never sent to the model).</summary>
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
}
