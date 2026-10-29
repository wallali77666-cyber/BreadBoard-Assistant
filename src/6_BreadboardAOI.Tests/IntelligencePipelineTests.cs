using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Intelligence;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class IntelligencePipelineTests
{
    [Fact]
    public void Sentence_splitter_keeps_decimal_values_whole()
    {
        IReadOnlyList<string> sentences = SentenceStreamSplitter.Split(
            "The rail measures 3.3V. Move the LED lead to D16!");

        sentences.Should().HaveCount(2);
        sentences[0].Should().Be("The rail measures 3.3V.");
        sentences[1].Should().Be("Move the LED lead to D16!");
    }

    [Fact]
    public void Sentence_splitter_waits_for_a_decimal_split_across_tokens()
    {
        var splitter = new SentenceStreamSplitter();

        splitter.Push("The rail is 3.").Should().BeEmpty();
        splitter.Push("3V. Move the lead").Should().Equal("The rail is 3.3V.");

        string? tail = splitter.Flush();
        tail.Should().Be("Move the lead");
    }

    [Fact]
    public void Prompt_contains_hole_labels_violation_code_and_three_sentence_rule()
    {
        string prompt = DiagnosticPromptBuilder.Build(CreateSameStripPacket());

        prompt.Should().Contain("ErcSameStripShort");
        prompt.Should().Contain("D15");
        prompt.Should().Contain("D16");
        prompt.Should().Contain("three short spoken sentences");
        prompt.Should().Contain("FIX: start with Move");
        prompt.Should().Contain("from <hole> to <hole>, as it <reason>");
        prompt.Should().Contain("WHY:");
        prompt.Should().Contain("DRC_JSON");
    }

    [Fact]
    public void Inspector_summary_formats_critical_violations()
    {
        string summary = DiagnosticPromptBuilder.BuildInspectorSummary(CreateSameStripPacket());

        summary.Should().Contain("CRITICAL");
        summary.Should().Contain("ErcSameStripShort");
        summary.Should().Contain("LED1");
    }

    [Theory]
    [InlineData(ViolationCode.ErcVccGndDirectShort, "dead short")]
    [InlineData(ViolationCode.ErcSameStripShort, "D16")]
    [InlineData(ViolationCode.ErcFloatingLead, "ground rail")]
    [InlineData(ViolationCode.DrcSchematicNetMismatch, "target net is open")]
    public void Rule_based_diagnostic_explains_the_fault(ViolationCode code, string expectedFragment)
    {
        DrcStatePacket packet = CreatePacket(new DrcViolation(
            code,
            DrcSeverity.Critical,
            "LED1",
            115,
            new HoleCoord("D15", 115, 400f, 140f),
            new HoleCoord("D16", 116, 414.8f, 140f),
            "Technical DRC message."));

        QwenMoEService.BuildRuleBasedDiagnostic(packet).Should().Contain(expectedFragment);
    }

    [Fact]
    public void Rule_based_diagnostic_speaks_exactly_three_sentences_even_with_many_faults()
    {
        var packet = new DrcStatePacket
        {
            FrameNumber = 43,
            YoloInferenceMs = 0.5,
            DrcComputeMs = 0.08,
            StateHash = 7,
            Components = Array.Empty<SnappedComponent>(),
            Violations = new[]
            {
                new DrcViolation(
                    ViolationCode.ErcSameStripShort,
                    DrcSeverity.Critical,
                    "LED1",
                    115,
                    new HoleCoord("D15", 115, 400f, 140f),
                    new HoleCoord("D16", 116, 414.8f, 140f),
                    "Short."),
                new DrcViolation(
                    ViolationCode.ErcFloatingLead,
                    DrcSeverity.Warning,
                    "R1",
                    130,
                    new HoleCoord("A30", 130, 470f, 95f),
                    default,
                    "Floating.")
            }
        };

        string answer = QwenMoEService.BuildRuleBasedDiagnostic(packet);

        SentenceStreamSplitter.Split(answer).Should().HaveCount(3);
        answer.Should().Contain("D15").And.Contain("D16");
    }

    [Fact]
    public async Task Rule_based_tutor_streams_without_llama_server_or_model()
    {
        var options = new QwenMoEOptions
        {
            EndpointUrl = "http://127.0.0.1:9/v1/chat/completions",
            ModelPath = "assets/models/does-not-exist.gguf",
            MmprojPath = "assets/models/does-not-exist-mmproj.gguf",
            ServerExecutablePath = "tools/does-not-exist-llama-server.exe",
            AutoLaunchServer = false,
            RequestTimeoutSeconds = 1,
            FallbackTokensPerSecond = 500
        };

        using var service = new QwenMoEService(options);
        var tokens = new List<string>();

        await foreach (string token in service.StreamDiagnosticAsync(CreateSameStripPacket()))
            tokens.Add(token);

        tokens.Should().NotBeEmpty();

        string answer = string.Concat(tokens);
        answer.Should().Contain("D15").And.Contain("D16");
        service.LastMode.Should().Be(QwenResponseMode.RuleBasedFallback);
        service.LastError.Should().NotBeNullOrWhiteSpace();
        SentenceStreamSplitter.Split(answer).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Offline_message_streams_when_rule_based_fallback_disabled()
    {
        var options = new QwenMoEOptions
        {
            EndpointUrl = "http://127.0.0.1:9/v1/chat/completions",
            ModelPath = "assets/models/does-not-exist.gguf",
            MmprojPath = "assets/models/does-not-exist-mmproj.gguf",
            ServerExecutablePath = "tools/does-not-exist-llama-server.exe",
            AutoLaunchServer = false,
            RequestTimeoutSeconds = 1,
            FallbackTokensPerSecond = 500
        };

        using var service = new QwenMoEService(options)
        {
            AllowRuleBasedFallback = false
        };
        var tokens = new List<string>();

        await foreach (string token in service.StreamDiagnosticAsync(CreateSameStripPacket()))
            tokens.Add(token);

        tokens.Should().NotBeEmpty();
        string answer = string.Concat(tokens);
        answer.Should().Contain("Qwen AI is offline");
    }

    [Fact]
    public void Threads_scale_with_the_processor_count()
    {
        QwenMoEOptions.DeriveThreadsBatch(20).Should().Be(12);
        QwenMoEOptions.DeriveThreads(20).Should().Be(6);

        QwenMoEOptions.DeriveThreadsBatch(8).Should().Be(8);
        QwenMoEOptions.DeriveThreads(8).Should().Be(4);

        QwenMoEOptions.DeriveThreadsBatch(4).Should().Be(4);
        QwenMoEOptions.DeriveThreads(4).Should().Be(2);

        QwenMoEOptions.DeriveThreadsBatch(1).Should().Be(1);
        QwenMoEOptions.DeriveThreads(1).Should().Be(1);

        QwenMoEOptions.DeriveThreadsBatch(64).Should().Be(12);
    }

    [Fact]
    public void Server_arguments_match_the_workstation_spec()
    {
        var options = new QwenMoEOptions { Threads = 6, ThreadsBatch = 12, ContextSize = 4096, CpuMoeLayers = 22 };

        IReadOnlyList<string> arguments = options.BuildServerArguments(
            8089,
            @"C:\models\qwen3-vl.gguf",
            @"C:\models\mmproj.gguf");

        arguments.Should().ContainInOrder("--model", @"C:\models\qwen3-vl.gguf");
        arguments.Should().ContainInOrder("--mmproj", @"C:\models\mmproj.gguf");
        arguments.Should().ContainInOrder("--threads", "6");
        arguments.Should().ContainInOrder("--threads-batch", "12");
        arguments.Should().ContainInOrder("--ctx-size", "4096");
        arguments.Should().ContainInOrder("--n-cpu-moe", "22");
        arguments.Should().Contain("--mlock");
        arguments.Should().Contain("--flash-attn");
        arguments.Should().ContainInOrder("--cache-type-k", "q8_0");
        arguments.Should().ContainInOrder("--cache-type-v", "q8_0");
        arguments.Should().ContainInOrder("--port", "8089");
    }

    [Fact]
    public void Environment_overrides_allow_a_smaller_model_and_thread_count()
    {
        const string modelVariable = "BREADBOARD_QWEN_MODEL";
        const string threadsVariable = "BREADBOARD_QWEN_THREADS";
        const string batchVariable = "BREADBOARD_QWEN_THREADS_BATCH";
        const string autoLaunchVariable = "BREADBOARD_QWEN_AUTO_LAUNCH";

        string? previousModel = Environment.GetEnvironmentVariable(modelVariable);
        string? previousThreads = Environment.GetEnvironmentVariable(threadsVariable);
        string? previousBatch = Environment.GetEnvironmentVariable(batchVariable);
        string? previousAutoLaunch = Environment.GetEnvironmentVariable(autoLaunchVariable);

        try
        {
            Environment.SetEnvironmentVariable(modelVariable, "assets/models/qwen3-vl-4b.gguf");
            Environment.SetEnvironmentVariable(threadsVariable, "3");
            Environment.SetEnvironmentVariable(batchVariable, "5");
            Environment.SetEnvironmentVariable(autoLaunchVariable, "false");

            QwenMoEOptions options = QwenMoEOptions.FromEnvironment();

            options.ModelPath.Should().Be("assets/models/qwen3-vl-4b.gguf");
            options.Threads.Should().Be(3);
            options.ThreadsBatch.Should().Be(5);
            options.AutoLaunchServer.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(modelVariable, previousModel);
            Environment.SetEnvironmentVariable(threadsVariable, previousThreads);
            Environment.SetEnvironmentVariable(batchVariable, previousBatch);
            Environment.SetEnvironmentVariable(autoLaunchVariable, previousAutoLaunch);
        }
    }

    [Fact]
    public void Voice_engine_degrades_to_sapi_when_kokoro_is_missing()
    {
        var options = new VoiceOptions
        {
            KokoroModelPath = "assets/models/kokoro/missing.onnx",
            KokoroTokensPath = "assets/models/kokoro/missing-tokens.txt",
            KokoroDataDir = "assets/models/kokoro/missing-espeak-data",
            KokoroLexiconPath = "assets/models/kokoro/missing-lexicon.txt",
            KokoroDictDir = "assets/models/kokoro/missing-dict",
            AlarmWavPath = "assets/audio_reflex/missing-alarm.wav"
        };

        using var engine = new VoiceSynthesisEngine(options);

        engine.Backend.Should().BeOneOf(VoiceBackend.Sapi, VoiceBackend.None);
        engine.IsAvailable.Should().Be(engine.Backend != VoiceBackend.None);
        engine.HasAlarmClip.Should().BeTrue("the alarm clip is generated in memory when no WAV exists");
        engine.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Generated_alarm_is_a_44khz_16bit_mono_riff_wave()
    {
        byte[] wav = VoiceSynthesisEngine.GenerateAlarmWav();

        wav.Length.Should().BeGreaterThan(44);
        Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        Encoding.ASCII.GetString(wav, 12, 4).Should().Be("fmt ");
        BitConverter.ToInt16(wav, 20).Should().Be(1, "PCM format");
        BitConverter.ToInt16(wav, 22).Should().Be(1, "mono");
        BitConverter.ToInt32(wav, 24).Should().Be(VoiceOptions.AlarmSampleRate);
        BitConverter.ToInt16(wav, 34).Should().Be(16);
        Encoding.ASCII.GetString(wav, 36, 4).Should().Be("data");
        BitConverter.ToInt32(wav, 40).Should().Be(wav.Length - 44);
    }

    private static DrcStatePacket CreateSameStripPacket() => CreatePacket(new DrcViolation(
        ViolationCode.ErcSameStripShort,
        DrcSeverity.Critical,
        "LED1",
        115,
        new HoleCoord("D15", 115, 400f, 140f),
        new HoleCoord("D16", 116, 414.8f, 140f),
        "LED1 bridges A15 and D15 inside strip 115."));

    private static DrcStatePacket CreatePacket(DrcViolation violation) => new()
    {
        FrameNumber = 42,
        YoloInferenceMs = 6.2,
        DrcComputeMs = 0.04,
        StateHash = unchecked((int)0xDEADBEEF),
        Components = Array.Empty<SnappedComponent>(),
        Violations = new[] { violation },
        MicroCropPngBytes = null
    };
}
