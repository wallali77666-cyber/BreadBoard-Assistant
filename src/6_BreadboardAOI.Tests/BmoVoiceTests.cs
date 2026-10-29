using System;
using BreadboardAOI.Intelligence;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

/// <summary>
/// Pure tests for the natural voice profile: pronunciation cleanup, neutral
/// pitch, pause length and the SAPI SSML wrapper. None of these need an audio
/// device.
/// </summary>
public sealed class BmoVoiceTests
{
    [Fact]
    public void Normalizer_keeps_vcc_as_letters_and_expands_attached_units()
    {
        string spoken = SpeechTextNormalizer.Normalize("VCC is 5V, GND is 0V.");

        spoken.Should().Contain("V C C");
        spoken.Should().NotContain("volts C C");
        spoken.Should().Contain("5 volts");
        spoken.Should().Contain("0 volts");
        spoken.Should().Contain("ground");
    }

    [Fact]
    public void Normalizer_is_idempotent_for_spoken_letter_names()
    {
        string once = SpeechTextNormalizer.Normalize("VCC is 5V and GND is 0V.");
        string twice = SpeechTextNormalizer.Normalize(once);

        twice.Should().Be(once);
    }

    [Fact]
    public void Normalizer_does_not_add_a_period_after_a_comma()
    {
        SpeechTextNormalizer.Normalize("first,").Should().Be("first,");
    }

    [Fact]
    public void Natural_styler_keeps_pitch_and_every_sample_finite()
    {
        const int sampleRate = 24000;
        var input = new float[sampleRate];
        for (int i = 0; i < input.Length; i++)
            input[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);

        float[] output = BmoVoiceStyler.Apply(input, sampleRate, 1.00f);

        output.Length.Should().BeInRange(input.Length - 2, input.Length);
        output.Should().OnlyContain(value => float.IsFinite(value));
        CountZeroCrossings(output).Should().BeInRange(
            CountZeroCrossings(input) - 2,
            CountZeroCrossings(input) + 2);
    }

    [Fact]
    public void Pause_after_a_sentence_is_longer_than_after_a_comma()
    {
        BmoVoiceStyler.PauseAfter("Move the lead to D16.", 150, 55, 65).Should().Be(150);
        BmoVoiceStyler.PauseAfter("Move the lead to D16,", 150, 55, 65).Should().Be(55);
        BmoVoiceStyler.PauseAfter("Move the lead to D16", 150, 55, 65).Should().Be(65);
    }

    [Fact]
    public void Sapi_ssml_escapes_text_and_requests_the_natural_profile()
    {
        string ssml = VoiceSynthesisEngine.BuildSapiSsml("Use R1 & R2 <carefully>.");

        ssml.Should().StartWith("<speak");
        ssml.Should().Contain("&amp;");
        ssml.Should().Contain("&lt;carefully&gt;");
        ssml.Should().Contain("pitch=\"+0%\"");
        ssml.Should().Contain("rate=\"-3%\"");
    }

    [Fact]
    public void Voice_options_default_to_the_natural_profile()
    {
        var options = new VoiceOptions();

        options.SpeakerId.Should().Be(-1, "auto-select a natural adult Kokoro voice");
        options.BmoVoice.Should().BeTrue();
        options.BmoPitch.Should().BeApproximately(1.00f, 0.001f);
        options.SentencePauseMs.Should().Be(220);
        BmoVoiceStyler.PreferredSpeakerId.Should().Be(2);
    }

    private static int CountZeroCrossings(float[] samples)
    {
        int start = samples.Length / 10;
        int end = samples.Length - start;
        int crossings = 0;

        for (int i = start + 1; i < end; i++)
        {
            if ((samples[i - 1] < 0f && samples[i] >= 0f)
                || (samples[i - 1] >= 0f && samples[i] < 0f))
            {
                crossings++;
            }
        }

        return crossings;
    }
}
