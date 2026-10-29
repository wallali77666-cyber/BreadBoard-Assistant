// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/BmoVoiceStyler.cs
// PURPOSE: Pure DSP helpers that keep synthesized speech clean and natural:
//          de-rumble, a gentle presence shelf, soft limiting and punctuation-
//          aware breathing room. Everything here is deterministic and runs on
//          the voice lane only; it never touches the 60 FPS reflex loop.
//
// The styler is deliberately dependency-free. Kokoro already supplies clean
// 24 kHz mono PCM, so a high-pass, a mild presence lift and edge fades are
// enough. There is no vibrato and no pitch lift by default: the tutor should
// sound like a calm person, not a character.
// ============================================================================

using System;
using System.Collections.Generic;

namespace BreadboardAOI.Intelligence;

/// <summary>
/// Voice shaping for the local tutor. The default profile keeps the speaker's
/// natural pitch and formants, removes desk rumble, adds a little presence and
/// trims/fades each clause for smooth playback.
/// </summary>
public static class BmoVoiceStyler
{
    /// <summary>Kokoro speaker 2 is af_bella on both the v0.19 and v1.0 exports.</summary>
    public const int PreferredSpeakerId = 2;

    public const float DefaultPitchFactor = 1.00f;

    private const float MinPitchFactor = 0.85f;
    private const float MaxPitchFactor = 1.30f;
    private const float HighPassHz = 70f;
    private const float PresenceCutoffHz = 1900f;
    private const float PresenceGain = 0.10f;
    private const float SaturationDrive = 1.02f;
    private const float SilenceThreshold = 0.0032f;

    /// <summary>
    /// Applies the natural profile to one synthesized clause. The returned array
    /// is shorter than the input only when <paramref name="pitchFactor"/> is above 1.
    /// </summary>
    public static float[] Apply(float[] samples, int sampleRate, float pitchFactor)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Length == 0 || sampleRate < 8000)
            return Array.Empty<float>();

        if (!float.IsFinite(pitchFactor) || pitchFactor <= 0f)
            pitchFactor = DefaultPitchFactor;

        pitchFactor = Math.Clamp(pitchFactor, MinPitchFactor, MaxPitchFactor);

        (int start, int end) = FindSpeechBounds(samples, sampleRate);
        int sourceLength = end - start;
        if (sourceLength <= 1)
            return Array.Empty<float>();

        // High-pass first: removes DC and desk rumble before it can be amplified
        // by the presence shelf.
        var filtered = new float[sourceLength];
        float highPassAlpha = 1f - MathF.Exp(-2f * MathF.PI * HighPassHz / sampleRate);
        float lowPass = 0f;
        for (int i = 0; i < sourceLength; i++)
        {
            float value = samples[start + i];
            if (!float.IsFinite(value))
                value = 0f;

            lowPass += highPassAlpha * (value - lowPass);
            filtered[i] = value - lowPass;
        }

        // Resampling is only used when the operator explicitly asks for a pitch
        // change. At the default 1.00 the voice keeps its natural formants.
        int outputLength = Math.Max(1, (int)MathF.Round(sourceLength / pitchFactor));
        var output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double position = i * pitchFactor;
            output[i] = SampleCubic(filtered, position);
        }

        // Presence shelf plus soft saturation: close and clear, never bright
        // enough to sound synthetic.
        float shelfAlpha = 1f - MathF.Exp(-2f * MathF.PI * PresenceCutoffHz / sampleRate);
        float shelfLow = 0f;
        for (int i = 0; i < output.Length; i++)
        {
            float value = output[i];
            shelfLow += shelfAlpha * (value - shelfLow);
            value += PresenceGain * (value - shelfLow);
            output[i] = MathF.Tanh(value * SaturationDrive);
        }

        ApplyEdgeFades(output, sampleRate, fadeInMs: 4, fadeOutMs: 12);
        return output;
    }

    /// <summary>
    /// Balanced loudness across clauses, with a peak ceiling so the limiter in
    /// the output device never clips a plosive.
    /// </summary>
    public static void NormalizeForPlayback(
        List<float> samples,
        float targetRms = 0.14f,
        float peakCeiling = 0.88f,
        int sampleRate = 24000)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
            return;

        double sumOfSquares = 0d;
        float peak = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            float value = samples[i];
            if (!float.IsFinite(value))
                value = 0f;

            samples[i] = value;
            sumOfSquares += value * value;
            peak = Math.Max(peak, MathF.Abs(value));
        }

        if (peak < 1e-6f || sumOfSquares <= 0d)
            return;

        double rms = Math.Sqrt(sumOfSquares / samples.Count);
        float gain = (float)Math.Min(targetRms / Math.Max(rms, 1e-6d), peakCeiling / peak);
        gain = Math.Clamp(gain, 0.35f, 2.5f);

        for (int i = 0; i < samples.Count; i++)
            samples[i] *= gain;

        ApplyEdgeFades(samples, sampleRate, fadeInMs: 5, fadeOutMs: 18);
    }

    /// <summary>
    /// Pause after a spoken clause. Sentence ends breathe; commas only flicker,
    /// which is what makes the tutor sound fluent instead of read out one word
    /// at a time.
    /// </summary>
    public static int PauseAfter(string? chunk, int sentenceMs, int commaMs, int clauseMs)
    {
        if (string.IsNullOrWhiteSpace(chunk))
            return Math.Max(0, clauseMs);

        int index = chunk.Length - 1;
        while (index >= 0 && char.IsWhiteSpace(chunk[index]))
            index--;

        if (index < 0)
            return Math.Max(0, clauseMs);

        return chunk[index] switch
        {
            '.' or '!' or '?' => Math.Max(0, sentenceMs),
            ',' => Math.Max(0, commaMs),
            ';' or ':' => Math.Max(0, (sentenceMs + commaMs) / 2),
            _ => Math.Max(0, clauseMs)
        };
    }

    private static (int Start, int End) FindSpeechBounds(float[] samples, int sampleRate)
    {
        int start = 0;
        while (start < samples.Length && MathF.Abs(samples[start]) < SilenceThreshold)
            start++;

        if (start >= samples.Length)
            return (0, 0);

        int end = samples.Length;
        while (end > start && MathF.Abs(samples[end - 1]) < SilenceThreshold)
            end--;

        int margin = Math.Max(1, sampleRate * 6 / 1000);
        start = Math.Max(0, start - margin);
        end = Math.Min(samples.Length, end + margin);
        return (start, end);
    }

    private static float SampleCubic(float[] samples, double position)
    {
        int baseIndex = (int)Math.Floor(position);
        float t = (float)(position - baseIndex);

        float p0 = SampleAt(samples, baseIndex - 1);
        float p1 = SampleAt(samples, baseIndex);
        float p2 = SampleAt(samples, baseIndex + 1);
        float p3 = SampleAt(samples, baseIndex + 2);

        float a = (-0.5f * p0) + (1.5f * p1) - (1.5f * p2) + (0.5f * p3);
        float b = p0 - (2.5f * p1) + (2f * p2) - (0.5f * p3);
        float c = (-0.5f * p0) + (0.5f * p2);
        return ((a * t + b) * t + c) * t + p1;
    }

    private static float SampleAt(float[] samples, int index) =>
        samples[Math.Clamp(index, 0, samples.Length - 1)];

    private static void ApplyEdgeFades(List<float> samples, int sampleRate, int fadeInMs, int fadeOutMs)
    {
        int fadeIn = Math.Min(samples.Count / 2, Math.Max(1, sampleRate * fadeInMs / 1000));
        int fadeOut = Math.Min(samples.Count / 2, Math.Max(1, sampleRate * fadeOutMs / 1000));

        for (int i = 0; i < fadeIn; i++)
            samples[i] *= i / (float)fadeIn;

        for (int i = 0; i < fadeOut; i++)
            samples[samples.Count - 1 - i] *= i / (float)fadeOut;
    }

    private static void ApplyEdgeFades(float[] samples, int sampleRate, int fadeInMs, int fadeOutMs)
    {
        int fadeIn = Math.Min(samples.Length / 2, Math.Max(1, sampleRate * fadeInMs / 1000));
        int fadeOut = Math.Min(samples.Length / 2, Math.Max(1, sampleRate * fadeOutMs / 1000));

        for (int i = 0; i < fadeIn; i++)
            samples[i] *= i / (float)fadeIn;

        for (int i = 0; i < fadeOut; i++)
            samples[samples.Length - 1 - i] *= i / (float)fadeOut;
    }
}
