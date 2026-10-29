// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/PiperTtsTests.cs
// PURPOSE: Pure tests for the optional BMO Piper voice: the operator-facing
//          engine preference, the Piper CLI contract, WAV ingestion and the
//          fallback chain when the model files are absent. No Piper binary,
//          ONNX model or audio device is required.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using BreadboardAOI.Intelligence;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class PiperTtsTests
{
    [Theory]
    [InlineData("auto", VoiceEnginePreference.Auto)]
    [InlineData("AUTO", VoiceEnginePreference.Auto)]
    [InlineData(" default ", VoiceEnginePreference.Auto)]
    [InlineData("piper", VoiceEnginePreference.Piper)]
    [InlineData("BMO", VoiceEnginePreference.Piper)]
    [InlineData("bmo-piper", VoiceEnginePreference.Piper)]
    [InlineData("kokoro", VoiceEnginePreference.Kokoro)]
    [InlineData("sherpa-onnx", VoiceEnginePreference.Kokoro)]
    [InlineData("sapi", VoiceEnginePreference.Sapi)]
    [InlineData("windows", VoiceEnginePreference.Sapi)]
    public void Preference_parsing_accepts_the_ui_and_cli_names(
        string value,
        VoiceEnginePreference expected)
    {
        VoiceEnginePreferences.TryParse(value, out VoiceEnginePreference preference)
            .Should().BeTrue();

        preference.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("elevenlabs")]
    public void Preference_parsing_rejects_unknown_names(string? value)
    {
        VoiceEnginePreferences.TryParse(value, out VoiceEnginePreference preference)
            .Should().BeFalse();

        preference.Should().Be(VoiceEnginePreference.Auto);
        VoiceEnginePreferences.Parse(value).Should().Be(VoiceEnginePreference.Auto);
        VoiceEnginePreferences.Parse(value, VoiceEnginePreference.Sapi)
            .Should().Be(VoiceEnginePreference.Sapi);
    }

    [Theory]
    [InlineData(VoiceEnginePreference.Auto)]
    [InlineData(VoiceEnginePreference.Piper)]
    [InlineData(VoiceEnginePreference.Kokoro)]
    [InlineData(VoiceEnginePreference.Sapi)]
    public void Preference_keys_round_trip(VoiceEnginePreference preference)
    {
        string key = VoiceEnginePreferences.ToKey(preference);

        VoiceEnginePreferences.TryParse(key, out VoiceEnginePreference parsed)
            .Should().BeTrue();
        parsed.Should().Be(preference);
        VoiceEnginePreferences.ToDisplayName(preference).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void WithPreferredBackend_returns_a_copy_and_leaves_the_original_untouched()
    {
        var options = new VoiceOptions
        {
            PiperModelPath = Path.Combine("assets", "models", "piper", "bmo.onnx"),
            BmoPitch = 1.12f
        };

        VoiceOptions copy = options.WithPreferredBackend(VoiceEnginePreference.Piper);

        copy.Should().NotBeSameAs(options);
        copy.PreferredBackend.Should().Be(VoiceEnginePreference.Piper);
        copy.PiperModelPath.Should().Be(options.PiperModelPath);
        copy.BmoPitch.Should().Be(options.BmoPitch);
        options.PreferredBackend.Should().Be(VoiceEnginePreference.Auto,
            "the copy must not mutate the caller's options");
    }

    [Fact]
    public void Piper_arguments_name_the_model_the_output_and_the_voice_parameters()
    {
        IReadOnlyList<string> arguments = PiperTtsEngine.BuildArguments(
            @"C:\voice\bmo.onnx",
            @"C:\voice\bmo.onnx.json",
            new PiperSynthesisSettings(SpeakerId: 2, LengthScale: 1.10f, SentenceSilenceMs: 200),
            @"C:\temp\out.wav");

        arguments.Should().ContainInOrder(
            "--model", @"C:\voice\bmo.onnx",
            "--output_file", @"C:\temp\out.wav",
            "--length_scale", "1.1",
            "--noise_scale", "0.667",
            "--noise_w", "0.8",
            "--sentence_silence", "0.2",
            "--config", @"C:\voice\bmo.onnx.json",
            "--speaker", "2");
    }

    [Fact]
    public void Piper_arguments_omit_the_speaker_for_a_single_voice_model()
    {
        IReadOnlyList<string> arguments = PiperTtsEngine.BuildArguments(
            "bmo.onnx",
            configPath: null,
            new PiperSynthesisSettings(SpeakerId: -1),
            "out.wav");

        arguments.Should().NotContain("--speaker");
        arguments.Should().NotContain("--config");
    }

    [Fact]
    public void Piper_arguments_clamp_out_of_range_settings()
    {
        string[] fast = ToArray(PiperTtsEngine.BuildArguments(
            "bmo.onnx",
            null,
            new PiperSynthesisSettings(
                LengthScale: 9f,
                NoiseScale: -5f,
                NoiseW: 99f,
                SentenceSilenceMs: 90_000),
            "out.wav"));

        IndexOf(fast, "--length_scale").Should().BeGreaterThanOrEqualTo(0);
        fast[IndexOf(fast, "--length_scale") + 1].Should().Be("2");
        fast[IndexOf(fast, "--noise_scale") + 1].Should().Be("0");
        fast[IndexOf(fast, "--noise_w") + 1].Should().Be("1.5");
        fast[IndexOf(fast, "--sentence_silence") + 1].Should().Be("2");

        string[] slow = ToArray(PiperTtsEngine.BuildArguments(
            "bmo.onnx",
            null,
            new PiperSynthesisSettings(LengthScale: 0.1f, SentenceSilenceMs: -20),
            "out.wav"));

        slow[IndexOf(slow, "--length_scale") + 1].Should().Be("0.5");
        slow[IndexOf(slow, "--sentence_silence") + 1].Should().Be("0");
    }

    [Fact]
    public void Piper_is_unavailable_until_the_executable_model_and_json_all_exist()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BreadboardAOI-piper-tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            string exe = Path.Combine(directory, "piper.exe");
            string model = Path.Combine(directory, "bmo.onnx");
            string config = model + ".json";

            PiperTtsEngine.IsAvailable(exe, model).Should().BeFalse("nothing exists yet");

            File.WriteAllBytes(exe, Array.Empty<byte>());
            File.WriteAllBytes(model, Array.Empty<byte>());
            PiperTtsEngine.IsAvailable(exe, model).Should().BeFalse("the voice JSON is still missing");

            File.WriteAllText(config, "{}");
            PiperTtsEngine.IsAvailable(exe, model).Should().BeTrue();
            PiperTtsEngine.IsAvailable(exe, model, config).Should().BeTrue();
            PiperTtsEngine.IsAvailable(exe, model, Path.Combine(directory, "missing.json"))
                .Should().BeFalse();

            var engine = new PiperTtsEngine(exe, model);
            engine.VoiceName.Should().Be("bmo");
            engine.ConfigPath.Should().Be(Path.GetFullPath(config));
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    [Fact]
    public void Pcm_parser_reads_the_in_memory_alarm_clip()
    {
        byte[] wav = VoiceSynthesisEngine.GenerateAlarmWav();

        bool parsed = PiperTtsEngine.TryReadPcm16Mono(wav, out byte[] pcm, out int sampleRate);

        parsed.Should().BeTrue();
        sampleRate.Should().Be(VoiceOptions.AlarmSampleRate);
        pcm.Length.Should().Be(wav.Length - 44);
        (pcm.Length % sizeof(short)).Should().Be(0);
    }

    [Fact]
    public void Pcm_parser_rejects_missing_non_pcm_and_multichannel_audio()
    {
        PiperTtsEngine.TryReadPcm16Mono(null, out _, out _).Should().BeFalse();
        PiperTtsEngine.TryReadPcm16Mono(Array.Empty<byte>(), out _, out _).Should().BeFalse();
        PiperTtsEngine.TryReadPcm16Mono(new byte[128], out _, out _).Should().BeFalse();

        PiperTtsEngine.TryReadPcm16Mono(
            BuildPcmWav(channels: 2, sampleRate: 22_050, bitsPerSample: 16, sampleBytes: 8),
            out _,
            out _).Should().BeFalse("the shared playback path is mono only");

        PiperTtsEngine.TryReadPcm16Mono(
            BuildPcmWav(channels: 1, sampleRate: 22_050, bitsPerSample: 8, sampleBytes: 8),
            out _,
            out _).Should().BeFalse("8-bit audio is not the 16-bit path");

        PiperTtsEngine.TryReadPcm16Mono(
            BuildPcmWav(channels: 1, sampleRate: 22_050, bitsPerSample: 16, sampleBytes: 8),
            out byte[] pcm,
            out int sampleRate).Should().BeTrue();
        sampleRate.Should().Be(22_050);
        pcm.Length.Should().Be(8);
    }

    /// <summary>Minimal RIFF/WAVE PCM file so format guards can be tested exactly.</summary>
    private static byte[] BuildPcmWav(
        short channels,
        int sampleRate,
        short bitsPerSample,
        int sampleBytes)
    {
        int blockAlign = channels * bitsPerSample / 8;
        var wav = new byte[44 + sampleBytes];

        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes(channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(wav, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(wav, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(wav, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(sampleBytes).CopyTo(wav, 40);

        return wav;
    }

    [Fact]
    public void Selecting_piper_without_the_model_files_falls_back_and_says_why()
    {
        var options = new VoiceOptions
        {
            KokoroModelPath = Path.Combine("assets", "models", "kokoro", "missing.onnx"),
            KokoroTokensPath = Path.Combine("assets", "models", "kokoro", "missing-tokens.txt"),
            KokoroDataDir = Path.Combine("assets", "models", "kokoro", "missing-data"),
            KokoroLexiconPath = Path.Combine("assets", "models", "kokoro", "missing-lexicon.txt"),
            KokoroDictDir = Path.Combine("assets", "models", "kokoro", "missing-dict"),
            PiperExePath = Path.Combine("assets", "models", "piper", "missing-piper.exe"),
            PiperModelPath = Path.Combine("assets", "models", "piper", "missing-bmo.onnx"),
            AlarmWavPath = Path.Combine("assets", "audio_reflex", "missing-alarm.wav")
        }.WithPreferredBackend(VoiceEnginePreference.Piper);

        using var engine = new VoiceSynthesisEngine(options);

        engine.PreferredBackend.Should().Be(VoiceEnginePreference.Piper);
        engine.HasPiperVoice.Should().BeFalse();
        engine.Backend.Should().NotBe(VoiceBackend.Piper);
        engine.Backend.Should().BeOneOf(VoiceBackend.Sapi, VoiceBackend.None);
        engine.LastError.Should().Contain("Piper", "the operator must know the BMO model is missing");
        engine.LastError.Should().Contain("missing-bmo.onnx");
    }

    [Fact]
    public void Auto_prefers_piper_but_still_ends_on_a_working_fallback()
    {
        var options = new VoiceOptions
        {
            KokoroModelPath = Path.Combine("assets", "models", "kokoro", "missing.onnx"),
            KokoroTokensPath = Path.Combine("assets", "models", "kokoro", "missing-tokens.txt"),
            KokoroDataDir = Path.Combine("assets", "models", "kokoro", "missing-data"),
            PiperExePath = Path.Combine("assets", "models", "piper", "missing-piper.exe"),
            PiperModelPath = Path.Combine("assets", "models", "piper", "missing-bmo.onnx")
        };

        using var engine = new VoiceSynthesisEngine(options);

        engine.PreferredBackend.Should().Be(VoiceEnginePreference.Auto);
        engine.Backend.Should().BeOneOf(VoiceBackend.Sapi, VoiceBackend.None);

        // Only one engine may be active, whatever the fallback order did.
        engine.VoiceProfileName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Environment_overrides_the_piper_voice_parameters()
    {
        const string backendKey = "BREADBOARD_VOICE_BACKEND";
        const string speakerKey = "BREADBOARD_VOICE_PIPER_SPEAKER";
        const string lengthKey = "BREADBOARD_VOICE_PIPER_LENGTH_SCALE";
        const string modelKey = "BREADBOARD_VOICE_PIPER_MODEL";

        string? previousBackend = Environment.GetEnvironmentVariable(backendKey);
        string? previousSpeaker = Environment.GetEnvironmentVariable(speakerKey);
        string? previousLength = Environment.GetEnvironmentVariable(lengthKey);
        string? previousModel = Environment.GetEnvironmentVariable(modelKey);

        try
        {
            Environment.SetEnvironmentVariable(backendKey, "Piper");
            Environment.SetEnvironmentVariable(speakerKey, "3");
            Environment.SetEnvironmentVariable(lengthKey, "1.25");
            Environment.SetEnvironmentVariable(modelKey, @"C:\voice\bmo.onnx");

            VoiceOptions options = VoiceOptions.FromEnvironment();

            options.PreferredBackend.Should().Be(VoiceEnginePreference.Piper);
            options.PiperSpeakerId.Should().Be(3);
            options.PiperLengthScale.Should().BeApproximately(1.25f, 0.001f);
            options.PiperModelPath.Should().Be(@"C:\voice\bmo.onnx");
        }
        finally
        {
            Environment.SetEnvironmentVariable(backendKey, previousBackend);
            Environment.SetEnvironmentVariable(speakerKey, previousSpeaker);
            Environment.SetEnvironmentVariable(lengthKey, previousLength);
            Environment.SetEnvironmentVariable(modelKey, previousModel);
        }
    }

    private static string[] ToArray(IReadOnlyList<string> values)
    {
        var copy = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
            copy[i] = values[i];

        return copy;
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
