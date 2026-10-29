// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/VoiceSynthesisEngine.cs
// PURPOSE: Kokoro-82M neural TTS via sherpa-onnx on the AI cores, an optional
//          Piper voice, NAudio playback, a Windows SAPI fallback when neither
//          neural voice is installed, and a <10 ms pre-loaded dual-tone emergency
//          alarm. The default profile is neutral and natural: no vibrato, no
//          pitch lift, punctuation-aware pauses.
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
    Sapi = 2,
    Piper = 3
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

    /// <summary>
    /// Piper CLI executable. The default folder is also where the voice model
    /// and the rest of the Piper Windows build should live.
    /// </summary>
    public string PiperExePath { get; init; } = Path.Combine("assets", "models", "piper", "piper.exe");

    /// <summary>Piper/VITS voice model, e.g. <c>en_US-lessac-medium.onnx</c>.</summary>
    public string PiperModelPath { get; init; } = Path.Combine("assets", "models", "piper", "bmo.onnx");

    /// <summary>Optional explicit JSON config; empty means <c>&lt;model&gt;.json</c>.</summary>
    public string PiperConfigPath { get; init; } = string.Empty;

    /// <summary>Requested engine. The engine still falls back when files are missing.</summary>
    public VoiceEnginePreference PreferredBackend { get; private set; } = VoiceEnginePreference.Auto;

    /// <summary>Piper speaker id, or -1 to leave the choice to the voice's JSON.</summary>
    public int PiperSpeakerId { get; init; } = -1;

    /// <summary>Piper length scale; above 1 speaks a little more slowly.</summary>
    public float PiperLengthScale { get; init; } = 1.03f;

    public float PiperNoiseScale { get; init; } = 0.667f;

    public float PiperNoiseW { get; init; } = 0.8f;

    /// <summary>Silence Piper inserts between its own sentence splits, in milliseconds.</summary>
    public int PiperSentenceSilenceMs { get; init; } = 260;

    public string AlarmWavPath { get; init; } = Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav");

    /// <summary>
    /// Kokoro speaker id, or -1 to pick a natural adult profile automatically.
    /// The engine clamps the value to the model's speaker count.
    /// </summary>
    public int SpeakerId { get; init; } = -1;

    public float Speed { get; init; } = 0.98f;

    /// <summary>Silence inserted after a comma or an unpunctuated clause, in milliseconds.</summary>
    public int ClausePauseMs { get; init; } = 70;

    /// <summary>Silence inserted after a full stop / question / exclamation mark, in milliseconds.</summary>
    public int SentencePauseMs { get; init; } = 220;

    /// <summary>
    /// When true, speech gets the natural finishing profile: de-rumble, a little
    /// presence and punctuation-aware pauses. Pitch stays neutral by default.
    /// </summary>
    public bool BmoVoice { get; init; } = true;

    /// <summary>Pitch/formant multiplier. 1.00 keeps the speaker's natural voice.</summary>
    public float BmoPitch { get; init; } = BmoVoiceStyler.DefaultPitchFactor;

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
            PiperExePath = ReadString("PIPER_EXE") ?? Path.Combine("assets", "models", "piper", "piper.exe"),
            PiperModelPath = ReadString("PIPER_MODEL") ?? Path.Combine("assets", "models", "piper", "bmo.onnx"),
            PiperConfigPath = ReadString("PIPER_CONFIG") ?? string.Empty,
            PreferredBackend = VoiceEnginePreferences.Parse(ReadString("BACKEND")),
            PiperSpeakerId = ReadInt("PIPER_SPEAKER") ?? -1,
            PiperLengthScale = ReadFloat("PIPER_LENGTH_SCALE") ?? 1.03f,
            PiperNoiseScale = ReadFloat("PIPER_NOISE_SCALE") ?? 0.667f,
            PiperNoiseW = ReadFloat("PIPER_NOISE_W") ?? 0.8f,
            PiperSentenceSilenceMs = ReadInt("PIPER_SENTENCE_SILENCE_MS") ?? 260,
            AlarmWavPath = ReadString("ALARM_WAV") ?? Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav"),
            SpeakerId = ReadInt("SPEAKER") ?? -1,
            Speed = ReadFloat("SPEED") ?? 0.98f,
            ClausePauseMs = ReadInt("CLAUSE_PAUSE_MS") ?? 70,
            SentencePauseMs = ReadInt("SENTENCE_PAUSE_MS") ?? 220,
            BmoVoice = ReadBool("BMO") ?? true,
            BmoPitch = ReadFloat("PITCH") ?? BmoVoiceStyler.DefaultPitchFactor,
            NumThreads = ReadInt("THREADS") ?? Math.Clamp(processors / 2, 1, 8),
            EnableSapiFallback = ReadBool("SAPI") ?? true
        };
    }

    /// <summary>
    /// Returns a copy with a different preferred engine. Used by the VIEW panel
    /// and by tests without rebuilding every path by hand.
    /// </summary>
    public VoiceOptions WithPreferredBackend(VoiceEnginePreference preference)
    {
        VoiceOptions clone = (VoiceOptions)MemberwiseClone();
        clone.PreferredBackend = preference;
        return clone;
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
/// Missing Piper/Kokoro files degrade through the remaining engines and finally
/// to a silent no-op, so the workstation always starts.
/// </summary>
public sealed class VoiceSynthesisEngine : IDisposable
{
    private VoiceOptions _options;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _alarmSync = new();
    private readonly byte[] _alarmWavBytes;

    private OfflineTts? _tts;
    private PiperTtsEngine? _piper;
    private object? _sapiVoice;
    private int _resolvedSpeakerId = -1;
    private string _sapiVoiceName = string.Empty;
    private bool _sapiSsmlSupported = true;
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
        PiperExePath = LocalPathResolver.Resolve(_options.PiperExePath, baseDirectory);
        PiperModelPath = LocalPathResolver.Resolve(_options.PiperModelPath, baseDirectory);
        PiperConfigPath = string.IsNullOrWhiteSpace(_options.PiperConfigPath)
            ? PiperModelPath + ".json"
            : LocalPathResolver.Resolve(_options.PiperConfigPath, baseDirectory);
        AlarmWavPath = LocalPathResolver.Resolve(_options.AlarmWavPath, baseDirectory);

        _alarmWavBytes = LoadAlarmClip(AlarmWavPath);
        InitializeBackend();
    }

    public VoiceOptions Options => _options;

    public VoiceBackend Backend { get; private set; }

    /// <summary>Requested engine; may differ from <see cref="Backend"/> after a fallback.</summary>
    public VoiceEnginePreference PreferredBackend => _options.PreferredBackend;

    /// <summary>Human-readable description of the active voice, e.g. "KOKORO · NATURAL #2".</summary>
    public string VoiceProfileName { get; private set; } = "SILENT";

    public bool IsAvailable => Backend != VoiceBackend.None;

    public string? LastError { get; private set; }

    public string KokoroModelPath { get; }

    public string KokoroTokensPath { get; }

    public string KokoroDataDir { get; }

    public string KokoroLexiconPath { get; }

    public string KokoroDictDir { get; }

    public string PiperExePath { get; }

    public string PiperModelPath { get; }

    public string PiperConfigPath { get; }

    /// <summary>True when the Piper executable, model and JSON are all on disk.</summary>
    public bool HasPiperVoice => PiperTtsEngine.IsAvailable(PiperExePath, PiperModelPath, PiperConfigPath);

    public string AlarmWavPath { get; }

    public bool HasAlarmClip => _alarmWavBytes.Length > 44;

    /// <summary>
    /// Switches the live engine without recreating the coordinator. Waits for any
    /// utterance already playing, then releases the old backend and selects the
    /// requested one (falling back when its files are missing).
    /// </summary>
    public async Task<bool> SwitchBackendAsync(
        VoiceEnginePreference preference,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DisposeBackends();
            _options = _options.WithPreferredBackend(preference);
            InitializeBackend();
            return Backend != VoiceBackend.None;
        }
        finally
        {
            _speechGate.Release();
        }
    }

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

    /// <summary>Speaks one complete sentence or paragraph with Kokoro (preferred) or SAPI.</summary>
    public async Task<bool> SpeakSentenceAsync(string sentence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return false;

        ObjectDisposedException.ThrowIf(_disposed, this);

        string spoken = SpeechTextNormalizer.Normalize(sentence);
        if (spoken.Length == 0)
            return false;

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Backend switch
            {
                VoiceBackend.Piper => await SpeakPiperAsync(spoken, cancellationToken).ConfigureAwait(false),
                VoiceBackend.Kokoro => await SpeakKokoroAsync(spoken, cancellationToken).ConfigureAwait(false),
                VoiceBackend.Sapi => await SpeakSapiAsync(spoken, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// Speaks several sentences as one utterance. Joining them keeps the voice
    /// continuous instead of restarting the audio device between sentences.
    /// </summary>
    public Task<bool> SpeakParagraphAsync(IEnumerable<string> sentences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sentences);

        var builder = new StringBuilder(320);
        foreach (string sentence in sentences)
        {
            string trimmed = sentence?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                continue;

            if (builder.Length > 0)
            {
                char last = builder[^1];
                if (last is not '.' and not '!' and not '?' and not ';' and not ':')
                    builder.Append('.');

                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.Length == 0
            ? Task.FromResult(false)
            : SpeakSentenceAsync(builder.ToString(), cancellationToken);
    }

    private void InitializeBackend()
    {
        LastError = null;
        var notes = new List<string>(3);

        foreach (VoiceEnginePreference candidate in GetBackendOrder(_options.PreferredBackend))
        {
            switch (candidate)
            {
                case VoiceEnginePreference.Piper:
                    if (TryCreatePiper())
                    {
                        Backend = VoiceBackend.Piper;
                        VoiceProfileName = BuildPiperProfileName();
                        FinalizeNotes(notes);
                        return;
                    }

                    CollectNote(notes);
                    break;

                case VoiceEnginePreference.Kokoro:
                    if (TryCreateKokoro())
                    {
                        Backend = VoiceBackend.Kokoro;
                        _resolvedSpeakerId = ResolveSpeakerId(_tts!);
                        VoiceProfileName = _options.BmoVoice
                            ? $"KOKORO · NATURAL #{_resolvedSpeakerId}"
                            : $"KOKORO #{_resolvedSpeakerId}";
                        FinalizeNotes(notes);
                        return;
                    }

                    CollectNote(notes);
                    break;

                case VoiceEnginePreference.Sapi:
                    if (!_options.EnableSapiFallback)
                    {
                        notes.Add("Windows SAPI fallback is disabled.");
                        break;
                    }

                    if (TryCreateSapi())
                    {
                        Backend = VoiceBackend.Sapi;
                        string name = _sapiVoiceName.Length == 0 ? "WINDOWS SAPI" : _sapiVoiceName;
                        VoiceProfileName = _options.BmoVoice ? $"SAPI · NATURAL / {name}" : name;
                        FinalizeNotes(notes);
                        return;
                    }

                    CollectNote(notes);
                    break;
            }
        }

        Backend = VoiceBackend.None;
        VoiceProfileName = "SILENT";

        if (notes.Count == 0)
            notes.Add("No speech engine is available.");

        FinalizeNotes(notes);
    }

    private static IReadOnlyList<VoiceEnginePreference> GetBackendOrder(VoiceEnginePreference preference) =>
        preference switch
        {
            VoiceEnginePreference.Piper => new[] { VoiceEnginePreference.Piper, VoiceEnginePreference.Kokoro, VoiceEnginePreference.Sapi },
            VoiceEnginePreference.Kokoro => new[] { VoiceEnginePreference.Kokoro, VoiceEnginePreference.Piper, VoiceEnginePreference.Sapi },
            VoiceEnginePreference.Sapi => new[] { VoiceEnginePreference.Sapi, VoiceEnginePreference.Piper, VoiceEnginePreference.Kokoro },
            _ => new[] { VoiceEnginePreference.Piper, VoiceEnginePreference.Kokoro, VoiceEnginePreference.Sapi }
        };

    private void CollectNote(List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(LastError))
            return;

        notes.Add(LastError!);
        LastError = null;
    }

    private void FinalizeNotes(List<string> notes) =>
        LastError = notes.Count == 0 ? null : string.Join(" ", notes);

    private bool TryCreatePiper()
    {
        if (!PiperTtsEngine.IsAvailable(PiperExePath, PiperModelPath, PiperConfigPath))
        {
            LastError =
                $"Piper voice files not found. Expected '{PiperModelPath}' and '{PiperExePath}'.";
            return false;
        }

        _piper = new PiperTtsEngine(PiperExePath, PiperModelPath, PiperConfigPath);
        return true;
    }

    private string BuildPiperProfileName()
    {
        string voice = _piper?.VoiceName ?? Path.GetFileNameWithoutExtension(PiperModelPath);
        return $"PIPER / {voice}";
    }

    private bool TryCreateKokoro()
    {
        bool assetsPresent =
            File.Exists(KokoroModelPath)
            && File.Exists(KokoroTokensPath)
            && Directory.Exists(KokoroDataDir);

        if (!assetsPresent)
        {
            LastError = $"Kokoro TTS files not found. Expected '{KokoroModelPath}'.";
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
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _tts?.Dispose();
            _tts = null;
            LastError = $"Kokoro TTS could not load ({ex.Message}).";
            return false;
        }
    }

    /// <summary>
    /// -1/auto selects a natural adult speaker, otherwise the first voice. An
    /// explicit BREADBOARD_VOICE_SPEAKER always wins.
    /// </summary>
    private int ResolveSpeakerId(OfflineTts tts)
    {
        int available = Math.Max(1, tts.NumSpeakers);
        int requested = _options.SpeakerId;

        if (requested < 0)
            requested = BmoVoiceStyler.PreferredSpeakerId;

        return Math.Clamp(requested, 0, available - 1);
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
            if (_sapiVoice is null)
                return false;

            SelectSapiVoice(_sapiVoice);
            SetSapiProperty(_sapiVoice, "Volume", 95);
            SetSapiProperty(_sapiVoice, "Rate", 0);

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = $"Windows SAPI is unavailable ({ex.Message}).";
            _sapiVoice = null;
            return false;
        }
    }

    /// <summary>
    /// Picks the most natural installed en-US voice. Aria, Jenny, Samantha,
    /// Hazel and Zira are preferred; child and novelty voices are rejected.
    /// </summary>
    private void SelectSapiVoice(object voice)
    {
        try
        {
            object? tokens = voice.GetType().InvokeMember(
                "GetVoices",
                BindingFlags.InvokeMethod,
                binder: null,
                target: voice,
                args: null);

            if (tokens is null)
                return;

            int count = Convert.ToInt32(
                tokens.GetType().InvokeMember(
                    "Count",
                    BindingFlags.GetProperty,
                    binder: null,
                    target: tokens,
                    args: null),
                System.Globalization.CultureInfo.InvariantCulture);

            object? bestToken = null;
            string bestName = string.Empty;
            int bestScore = int.MinValue;

            for (int i = 0; i < count; i++)
            {
                object? token = tokens.GetType().InvokeMember(
                    "Item",
                    BindingFlags.InvokeMethod | BindingFlags.GetProperty,
                    binder: null,
                    target: tokens,
                    args: new object[] { i });

                if (token is null)
                    continue;

                string description = token.GetType().InvokeMember(
                    "GetDescription",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: token,
                    args: null) as string ?? string.Empty;

                int score = ScoreSapiVoice(description);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestToken = token;
                bestName = description;
            }

            if (bestToken is null)
                return;

            voice.GetType().InvokeMember(
                "Voice",
                BindingFlags.SetProperty,
                binder: null,
                target: voice,
                args: new object[] { bestToken });

            _sapiVoiceName = bestName;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Voice enumeration is an optimisation: the default voice still speaks.
            _sapiVoiceName = string.Empty;
        }
    }

    private static int ScoreSapiVoice(string description)
    {
        string value = description.ToLowerInvariant();
        int score = 0;

        if (value.Contains("aria", StringComparison.Ordinal)
            || value.Contains("jenny", StringComparison.Ordinal)
            || value.Contains("samantha", StringComparison.Ordinal)
            || value.Contains("hazel", StringComparison.Ordinal))
            score += 100;

        if (value.Contains("zira", StringComparison.Ordinal)
            || value.Contains("eva", StringComparison.Ordinal))
            score += 70;

        if (value.Contains("female", StringComparison.Ordinal))
            score += 10;

        if (value.Contains("child", StringComparison.Ordinal)
            || value.Contains("kid", StringComparison.Ordinal)
            || value.Contains("novelty", StringComparison.Ordinal))
            score -= 120;

        if (value.Contains("male", StringComparison.Ordinal)
            && !value.Contains("female", StringComparison.Ordinal))
            score -= 100;

        if (value.Contains("english (united states)", StringComparison.Ordinal)
            || value.Contains("en-us", StringComparison.Ordinal))
            score += 20;

        return score;
    }

    private static void SetSapiProperty(object voice, string property, object value)
    {
        try
        {
            voice.GetType().InvokeMember(
                property,
                BindingFlags.SetProperty,
                binder: null,
                target: voice,
                args: new[] { value });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Volume and rate are cosmetic; speaking still works without them.
        }
    }

    private async Task<bool> SpeakPiperAsync(string text, CancellationToken cancellationToken)
    {
        PiperTtsEngine? piper = _piper;
        if (piper is null)
            return false;

        var settings = new PiperSynthesisSettings(
            _options.PiperSpeakerId,
            _options.PiperLengthScale,
            _options.PiperNoiseScale,
            _options.PiperNoiseW,
            _options.PiperSentenceSilenceMs);

        byte[] wav = await piper.SynthesizeWavAsync(text, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!PiperTtsEngine.TryReadPcm16Mono(wav, out byte[] pcm, out int sampleRate))
        {
            LastError = "Piper returned audio that is not 16-bit mono PCM.";
            return false;
        }

        // The Piper model already carries its own voice; the DSP pitch lift is
        // intentionally skipped on this backend.
        return await PlayPcmAsync(pcm, sampleRate, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SpeakKokoroAsync(string text, CancellationToken cancellationToken)
    {
        OfflineTts? tts = _tts;
        if (tts is null)
            return false;

        IReadOnlyList<string> chunks = SpeechTextNormalizer.SplitForProsody(text, normalize: false);
        if (chunks.Count == 0)
            return false;

        float speed = Math.Clamp(_options.Speed, 0.5f, 2.0f);
        float pitchFactor = _options.BmoVoice
            ? Math.Clamp(_options.BmoPitch, 0.85f, 1.35f)
            : 1f;
        int sentencePauseMs = Math.Clamp(_options.SentencePauseMs, 0, 800);
        int commaPauseMs = Math.Clamp(_options.ClausePauseMs, 0, 400);
        int clausePauseMs = Math.Max(commaPauseMs, 65);
        int speakerId = _resolvedSpeakerId >= 0
            ? _resolvedSpeakerId
            : ResolveSpeakerId(tts);

        return await Task.Run(async () =>
        {
            // Synthesize each clause, shape it, join the PCM with punctuation-
            // aware silence and play once. Restarting WaveOutEvent per sentence
            // is what makes synthesized tutors sound choppy and robotic.
            var speech = new List<float>(24000 * 4);
            int sampleRate = 24000;
            string? previousChunk = null;

            foreach (string chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                OfflineTtsGeneratedAudio audio;
                try
                {
                    audio = tts.Generate(chunk, speed, speakerId);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && speakerId != 0)
                {
                    // Older Kokoro exports only expose a handful of speakers.
                    // Fall back to the first voice once instead of losing speech.
                    speakerId = 0;
                    _resolvedSpeakerId = 0;
                    audio = tts.Generate(chunk, speed, speakerId);
                }

                try
                {
                    if (audio.NumSamples <= 0)
                        continue;

                    sampleRate = audio.SampleRate;
                    float[] styled = BmoVoiceStyler.Apply(audio.Samples, sampleRate, pitchFactor);
                    if (styled.Length == 0)
                        continue;

                    if (speech.Count > 0 && previousChunk is not null)
                    {
                        int pauseMs = BmoVoiceStyler.PauseAfter(
                            previousChunk,
                            sentencePauseMs,
                            commaPauseMs,
                            clausePauseMs);

                        AppendSilence(speech, sampleRate, pauseMs);
                    }

                    speech.AddRange(styled);
                    previousChunk = chunk;
                }
                finally
                {
                    audio.Dispose();
                }
            }

            if (speech.Count == 0)
                return false;

            BmoVoiceStyler.NormalizeForPlayback(speech, sampleRate: sampleRate);
            byte[] pcm = ConvertToPcm16(speech);
            return await PlayPcmAsync(pcm, sampleRate, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PlayPcmAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
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

    private static void AppendSilence(List<float> destination, int sampleRate, int milliseconds)
    {
        if (milliseconds <= 0)
            return;

        int samples = Math.Max(1, sampleRate * milliseconds / 1000);
        for (int i = 0; i < samples; i++)
            destination.Add(0f);
    }

    private Task<bool> SpeakSapiAsync(string sentence, CancellationToken cancellationToken)
    {
        object? voice = _sapiVoice;
        if (voice is null)
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_options.BmoVoice && _sapiSsmlSupported)
            {
                try
                {
                    // SVSFParseSsml (256). Neutral pitch and a slightly slower
                    // rate keep Zira/Aria conversational instead of clipped.
                    voice.GetType().InvokeMember(
                        "Speak",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: voice,
                        args: new object[] { BuildSapiSsml(sentence), 256 });

                    return true;
                }
                catch (TargetInvocationException)
                {
                    _sapiSsmlSupported = false;
                }
                catch (COMException)
                {
                    _sapiSsmlSupported = false;
                }
            }

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

    /// <summary>
    /// Wraps one normalized utterance in the small SSML subset Windows SAPI
    /// accepts. Public so the exact pitch/rate and escaping are testable without
    /// an audio device.
    /// </summary>
    public static string BuildSapiSsml(string text, int pitchPercent = 0, int ratePercent = -3)
    {
        ArgumentNullException.ThrowIfNull(text);

        string escaped = EscapeXml(text);
        string pitch = FormatSapiPercent(pitchPercent);
        string rate = FormatSapiPercent(ratePercent);

        return string.Concat(
            "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">",
            "<prosody pitch=\"",
            pitch,
            "%\" rate=\"",
            rate,
            "%\">",
            escaped,
            "</prosody></speak>");
    }

    private static string FormatSapiPercent(int value)
    {
        int clamped = Math.Clamp(value, -20, 20);
        string sign = clamped >= 0 ? "+" : string.Empty;
        return sign + clamped.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EscapeXml(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\"'") < 0)
            return value;

        var builder = new StringBuilder(value.Length + 16);
        foreach (char character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static byte[] ConvertToPcm16(List<float> samples)
    {
        if (samples.Count == 0)
            return Array.Empty<byte>();

        byte[] pcm = new byte[samples.Count * sizeof(short)];
        for (int i = 0; i < samples.Count; i++)
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

    private void DisposeBackends()
    {
        _tts?.Dispose();
        _tts = null;
        _piper = null;

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

        _resolvedSpeakerId = -1;
        _sapiVoiceName = string.Empty;
        _sapiSsmlSupported = true;
        Backend = VoiceBackend.None;
        VoiceProfileName = "SILENT";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisposeBackends();
        _speechGate.Dispose();
    }
}
