// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/VoiceSynthesisEngine.cs
// PURPOSE: Kokoro-82M neural TTS via sherpa-onnx on the AI cores, NAudio
//          playback, a Windows SAPI COM fallback when the Kokoro ONNX files are
//          absent, and a <10 ms pre-loaded dual-tone emergency alarm.
//
// NOTE: The alarm WAV is generated in memory (44.1 kHz, 16-bit PCM RIFF), so no
//       binary asset has to ship with the repo and a missing file never breaks
//       the critical-short reflex.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SherpaOnnx;

namespace BreadboardAOI.Intelligence;

public enum VoiceBackend
{
    None = 0,
    Kokoro = 1,
    Sapi = 2
}

/// <summary>Per-machine voice configuration. Environment prefix: <c>BREADBOARD_VOICE_</c>.</summary>
public sealed class VoiceOptions
{
    public const int AlarmSampleRate = 44100;

    public string KokoroModelPath { get; init; } = Path.Combine("assets", "models", "kokoro", "model.onnx");

    public string KokoroTokensPath { get; init; } = Path.Combine("assets", "models", "kokoro", "tokens.txt");

    public string KokoroDataDir { get; init; } = Path.Combine("assets", "models", "kokoro", "espeak-ng-data");

    public string KokoroLexiconPath { get; init; } = Path.Combine("assets", "models", "kokoro", "lexicon-us-en.txt");

    public string KokoroDictDir { get; init; } = Path.Combine("assets", "models", "kokoro", "dict");

    public string AlarmWavPath { get; init; } = Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav");

    public int SpeakerId { get; init; }

    public float Speed { get; init; } = 1.0f;

    public int NumThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public bool EnableSapiFallback { get; init; } = true;

    public static VoiceOptions FromEnvironment()
    {
        int processors = Environment.ProcessorCount;

        return new VoiceOptions
        {
            KokoroModelPath = ReadString("KOKORO_MODEL") ?? Path.Combine("assets", "models", "kokoro", "model.onnx"),
            KokoroTokensPath = ReadString("KOKORO_TOKENS") ?? Path.Combine("assets", "models", "kokoro", "tokens.txt"),
            KokoroDataDir = ReadString("KOKORO_DATA_DIR") ?? Path.Combine("assets", "models", "kokoro", "espeak-ng-data"),
            KokoroLexiconPath = ReadString("KOKORO_LEXICON") ?? Path.Combine("assets", "models", "kokoro", "lexicon-us-en.txt"),
            KokoroDictDir = ReadString("KOKORO_DICT_DIR") ?? Path.Combine("assets", "models", "kokoro", "dict"),
            AlarmWavPath = ReadString("ALARM_WAV") ?? Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav"),
            SpeakerId = ReadInt("SPEAKER") ?? 0,
            Speed = ReadFloat("SPEED") ?? 1.0f,
            NumThreads = ReadInt("THREADS") ?? Math.Clamp(processors / 2, 1, 8),
            EnableSapiFallback = ReadBool("SAPI") ?? true
        };
    }

    private static string? ReadString(string suffix)
    {
        string? value = Environment.GetEnvironmentVariable("BREADBOARD_VOICE_" + suffix);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static int? ReadInt(string suffix) =>
        int.TryParse(ReadString(suffix), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static float? ReadFloat(string suffix) =>
        float.TryParse(ReadString(suffix), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    private static bool? ReadBool(string suffix)
    {
        string? raw = ReadString(suffix);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }
}

/// <summary>
/// Serializes all speech so the tutor voice never overlaps the emergency alarm.
/// Missing Kokoro files degrade to Windows SAPI and then to silent no-op.
/// </summary>
public sealed class VoiceSynthesisEngine : IDisposable
{
    private readonly VoiceOptions _options;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _alarmSync = new();
    private readonly byte[] _alarmWavBytes;

    private OfflineTts? _tts;
    private object? _sapiVoice;
    private bool _disposed;

    public VoiceSynthesisEngine(VoiceOptions? options = null)
    {
        _options = options ?? VoiceOptions.FromEnvironment();
        string baseDirectory = AppContext.BaseDirectory;

        KokoroModelPath = LocalPathResolver.Resolve(_options.KokoroModelPath, baseDirectory);
        KokoroTokensPath = LocalPathResolver.Resolve(_options.KokoroTokensPath, baseDirectory);
        KokoroDataDir = LocalPathResolver.ResolveDirectory(_options.KokoroDataDir, baseDirectory);
        KokoroLexiconPath = LocalPathResolver.Resolve(_options.KokoroLexiconPath, baseDirectory);
        KokoroDictDir = LocalPathResolver.ResolveDirectory(_options.KokoroDictDir, baseDirectory);
        AlarmWavPath = LocalPathResolver.Resolve(_options.AlarmWavPath, baseDirectory);

        _alarmWavBytes = LoadAlarmClip(AlarmWavPath);

        if (TryCreateKokoro())
        {
            Backend = VoiceBackend.Kokoro;
        }
        else if (_options.EnableSapiFallback && TryCreateSapi())
        {
            Backend = VoiceBackend.Sapi;
        }
        else
        {
            Backend = VoiceBackend.None;
        }
    }

    public VoiceOptions Options => _options;

    public VoiceBackend Backend { get; private set; }

    public bool IsAvailable => Backend != VoiceBackend.None;

    public string? LastError { get; private set; }

    public string KokoroModelPath { get; }

    public string KokoroTokensPath { get; }

    public string KokoroDataDir { get; }

    public string KokoroLexiconPath { get; }

    public string KokoroDictDir { get; }

    public string AlarmWavPath { get; }

    public bool HasAlarmClip => _alarmWavBytes.Length > 44;

    /// <summary>
    /// Plays the pre-loaded alarm immediately. The clip is generated in memory at
    /// construction, so a missing asset folder costs nothing on the reflex path.
    /// </summary>
    public bool PlayEmergencyReflexAlarm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_alarmWavBytes.Length <= 44)
            return false;

        lock (_alarmSync)
        {
            WaveOutEvent? output = null;
            WaveFileReader? reader = null;

            try
            {
                reader = new WaveFileReader(new MemoryStream(_alarmWavBytes, writable: false));
                output = new WaveOutEvent { DesiredLatency = 40, NumberOfBuffers = 2 };

                WaveOutEvent capturedOutput = output;
                WaveFileReader capturedReader = reader;
                output.PlaybackStopped += (_, _) =>
                {
                    capturedOutput.Dispose();
                    capturedReader.Dispose();
                };

                output.Init(reader);
                output.Play();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LastError = $"Emergency alarm playback failed: {ex.Message}";
                reader?.Dispose();
                output?.Dispose();
                return false;
            }
        }
    }

    /// <summary>Speaks one complete sentence with Kokoro (preferred) or SAPI.</summary>
    public async Task<bool> SpeakSentenceAsync(string sentence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return false;

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Backend switch
            {
                VoiceBackend.Kokoro => await SpeakKokoroAsync(sentence.Trim(), cancellationToken).ConfigureAwait(false),
                VoiceBackend.Sapi => await SpeakSapiAsync(sentence.Trim(), cancellationToken).ConfigureAwait(false),
                _ => false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"Speech synthesis failed: {ex.Message}";
            return false;
        }
        finally
        {
            _speechGate.Release();
        }
    }

    /// <summary>Speaks a sentence stream in order (used by the Phase 5 coordinator).</summary>
    public async Task SpeakSentencesAsync(IAsyncEnumerable<string> sentences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sentences);

        await foreach (string sentence in sentences.WithCancellation(cancellationToken).ConfigureAwait(false))
            await SpeakSentenceAsync(sentence, cancellationToken).ConfigureAwait(false);
    }

    private bool TryCreateKokoro()
    {
        bool assetsPresent =
            File.Exists(KokoroModelPath)
            && File.Exists(KokoroTokensPath)
            && Directory.Exists(KokoroDataDir);

        if (!assetsPresent)
        {
            LastError = $"Kokoro TTS files not found; falling back to Windows SAPI. Expected '{KokoroModelPath}'.";
            return false;
        }

        try
        {
            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = KokoroModelPath;
            config.Model.Vits.Tokens = KokoroTokensPath;
            config.Model.Vits.DataDir = KokoroDataDir;
            config.Model.Vits.Lexicon = File.Exists(KokoroLexiconPath) ? KokoroLexiconPath : string.Empty;
            config.Model.Vits.DictDir = Directory.Exists(KokoroDictDir) ? KokoroDictDir : string.Empty;
            config.Model.NumThreads = Math.Max(1, _options.NumThreads);
            config.Model.Provider = "cpu";

            _tts = new OfflineTts(config);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _tts?.Dispose();
            _tts = null;
            LastError = $"Kokoro TTS could not load ({ex.Message}); falling back to Windows SAPI.";
            return false;
        }
    }

    private bool TryCreateSapi()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            Type? sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null)
                return false;

            _sapiVoice = Activator.CreateInstance(sapiType);
            return _sapiVoice is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"Windows SAPI is unavailable ({ex.Message}).";
            _sapiVoice = null;
            return false;
        }
    }

    private async Task<bool> SpeakKokoroAsync(string sentence, CancellationToken cancellationToken)
    {
        OfflineTts? tts = _tts;
        if (tts is null)
            return false;

        return await Task.Run(async () =>
        {
            OfflineTtsGeneratedAudio audio = tts.Generate(
                sentence,
                Math.Clamp(_options.Speed, 0.5f, 2.0f),
                _options.SpeakerId);

            try
            {
                if (audio.NumSamples <= 0)
                    return false;

                byte[] pcm = ConvertToPcm16(audio.Samples);
                var format = new WaveFormat(audio.SampleRate, 16, 1);
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                using var output = new WaveOutEvent { DesiredLatency = 80 };
                using var stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), format);
                output.PlaybackStopped += (_, eventArgs) => completion.TrySetResult(eventArgs.Exception is null);

                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        output.Stop();
                    }
                    catch (Exception)
                    {
                        // The device is already closing.
                    }

                    completion.TrySetCanceled(cancellationToken);
                });

                output.Init(stream);
                output.Play();
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                audio.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> SpeakSapiAsync(string sentence, CancellationToken cancellationToken)
    {
        object? voice = _sapiVoice;
        if (voice is null)
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // SVSFDefault (0) blocks until the utterance completes, which keeps
                // the semaphore ordering identical to the Kokoro path.
                voice.GetType().InvokeMember(
                    "Speak",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: voice,
                    args: new object[] { sentence, 0 });

                return true;
            }
            catch (TargetInvocationException ex)
            {
                LastError = $"SAPI Speak failed: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (COMException ex)
            {
                LastError = $"SAPI Speak failed: {ex.Message}";
                return false;
            }
        }, cancellationToken);
    }

    private static byte[] ConvertToPcm16(float[] samples)
    {
        if (samples.Length == 0)
            return Array.Empty<byte>();

        byte[] pcm = new byte[samples.Length * sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            float value = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
            short quantized = (short)Math.Round(value * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short), sizeof(short)), quantized);
        }

        return pcm;
    }

    private static byte[] LoadAlarmClip(string configuredPath)
    {
        if (File.Exists(configuredPath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(configuredPath);
                using var reader = new WaveFileReader(new MemoryStream(bytes, writable: false));
                if (reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm && reader.WaveFormat.Channels > 0)
                    return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                // Corrupt custom WAV: fall through to the generated alarm.
            }
        }

        return GenerateAlarmWav();
    }

    /// <summary>
    /// Builds a deterministic dual-tone 880/660 Hz alarm as a complete RIFF/WAVE
    /// byte array: 44.1 kHz, mono, 16-bit PCM, four 100 ms segments with 5 ms fades.
    /// </summary>
    public static byte[] GenerateAlarmWav()
    {
        const int sampleRate = VoiceOptions.AlarmSampleRate;
        const int channels = 1;
        const int bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;
        const int segmentSamples = sampleRate / 10; // 100 ms
        const int segments = 4;

        int totalSamples = segmentSamples * segments;
        int dataSize = totalSamples * channels * bytesPerSample;
        int fadeSamples = Math.Max(1, sampleRate / 200); // 5 ms

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            int segment = i / segmentSamples;
            int withinSegment = i % segmentSamples;
            double frequency = segment % 2 == 0 ? 880d : 660d;

            double envelope = 1d;
            if (withinSegment < fadeSamples)
                envelope = withinSegment / (double)fadeSamples;
            else if (withinSegment > segmentSamples - fadeSamples)
                envelope = (segmentSamples - withinSegment) / (double)fadeSamples;

            double sample = Math.Sin(2d * Math.PI * frequency * i / sampleRate) * 0.35d * envelope;
            writer.Write((short)Math.Round(Math.Clamp(sample, -1d, 1d) * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _tts?.Dispose();
        _tts = null;

        if (_sapiVoice is not null)
        {
            try
            {
                if (Marshal.IsComObject(_sapiVoice))
                    Marshal.FinalReleaseComObject(_sapiVoice);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
            {
                // COM object already released by the host.
            }

            _sapiVoice = null;
        }

        _speechGate.Dispose();
    }
}
