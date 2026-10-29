// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/DiagnosticPromptBuilder.cs
// PURPOSE: Turns a DrcStatePacket into a compact, vision-model-friendly prompt
//          (structured JSON + 256x256 crop hint) that asks for one action-first
//          fix sentence and two short spoken explanations.
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
        builder.AppendLine("You are the workstation's electronics tutor, speaking to a beginner across the bench.");
        builder.AppendLine("Sound like a calm, experienced person: use contractions, short sentences and ordinary workshop words. Never be theatrical, babyish, sarcastic or robotic.");
        builder.AppendLine("The attached image is a 256x256 crop of the faulty area; the JSON below is deterministic DRC output.");
        builder.AppendLine("Answer with exactly three short spoken sentences, in this order:");
        builder.AppendLine("1. FIX: start with Move, Remove, Add, Refit or Recheck. When a lead must move, write it as: Move <lead or part> from <hole> to <hole>, as it <reason>. Name the exact hole labels from the JSON.");
        builder.AppendLine("2. WHY: say what the fault does to the circuit in plain words (dead short, bypassed part, no return path, open net).");
        builder.AppendLine("3. CHECK: one short verification step the beginner can see or measure.");
        builder.AppendLine("Do not use markdown, bullet points, numbering, code blocks, headings, emoji or sound effects. Keep every sentence under 24 words.");
        builder.AppendLine("Speak the action first; never open with 'I see' or 'The problem is'.");
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
