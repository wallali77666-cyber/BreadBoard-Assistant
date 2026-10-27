// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/DiagnosticPromptBuilder.cs
// PURPOSE: Turns a DrcStatePacket into a compact, vision-model-friendly prompt
//          (structured JSON + 256x256 crop hint) that asks for exactly two
//          spoken tutor sentences.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Intelligence;

public static class DiagnosticPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Prompt sent to Qwen3-VL together with <see cref="DrcStatePacket.MicroCropPngBytes"/>.
    /// </summary>
    public static string Build(DrcStatePacket state) => Build(state, maxComponents: 8);

    public static string Build(DrcStatePacket state, int maxComponents)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (maxComponents < 0)
            throw new ArgumentOutOfRangeException(nameof(maxComponents));

        var builder = new StringBuilder(1024);
        builder.AppendLine("You are a patient breadboard electronics tutor speaking to a beginner.");
        builder.AppendLine("The attached image is a 256x256 crop of the faulty area; the JSON below is deterministic DRC output.");
        builder.AppendLine("Reply with at most two short spoken sentences. Name the hole labels, explain why it is wrong, and give one concrete fix.");
        builder.AppendLine("Do not use markdown, bullet points, code blocks or headings.");
        builder.AppendLine();
        builder.AppendLine("DRC_JSON:");
        builder.AppendLine(BuildCompactJson(state, maxComponents));
        return builder.ToString().TrimEnd();
    }

    /// <summary>Compact machine-readable snapshot used by both the prompt and the transcript diagnostics pane.</summary>
    public static string BuildCompactJson(DrcStatePacket state, int maxComponents = 8)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (maxComponents < 0)
            throw new ArgumentOutOfRangeException(nameof(maxComponents));

        var violations = new List<object>(state.Violations.Count);
        foreach (DrcViolation violation in state.Violations)
        {
            violations.Add(new
            {
                code = violation.Code.ToString(),
                severity = violation.Severity.ToString(),
                component = violation.ComponentId,
                strip = violation.OffendingStripId,
                badHole = HoleLabel(violation.BadHole, violation.OffendingStripId),
                targetHole = HoleLabel(violation.SuggestedTargetHole, 0),
                message = violation.TechnicalMessage
            });
        }

        var components = new List<object>(Math.Min(maxComponents, state.Components.Count));
        int emitted = 0;
        foreach (SnappedComponent component in state.Components)
        {
            if (emitted++ >= maxComponents)
                break;

            components.Add(new
            {
                trackId = component.RawDetection.TrackId,
                kind = component.RawDetection.Kind.ToString(),
                confidence = Math.Round(component.RawDetection.Confidence, 2),
                pin1 = component.Pin1Hole.HoleId,
                pin2 = component.Pin2Hole.HoleId,
                net1 = component.Net1Name,
                net2 = component.Net2Name
            });
        }

        var report = new
        {
            frame = state.FrameNumber,
            stateHash = state.StateHash,
            yoloMs = Math.Round(state.YoloInferenceMs, 2),
            drcMs = Math.Round(state.DrcComputeMs, 3),
            critical = state.HasCriticalShort,
            cropAttached = state.MicroCropPngBytes is { Length: > 0 },
            violations,
            components
        };

        return JsonSerializer.Serialize(report, JsonOptions);
    }

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
