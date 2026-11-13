// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/VoiceSynthesisEngine.cs
// PURPOSE: Tutor voice playback. ElevenLabs is the primary neural voice (voice
//          picked by the operator in the workstation), with Piper and Windows
//          SAPI as fully local fallbacks when there is no key or no network.
//          Text is synthesized to 24 kHz PCM and played through NAudio; nothing
//          is written to disk. A <10 ms pre-loaded dual-tone emergency alarm is
//          generated in memory (44.1 kHz, 16-bit PCM RIFF) so the critical-short
//          reflex never depends on a binary asset.
//
// NOTE: Only the vision-model answer is ever synthesized. DRC repair text
//       stays on screen; the coordinator decides that, not this engine.
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
using BreadboardAOI.Hardware;
using NAudio.Wave;

namespace BreadboardAOI.Intelligence;

public enum VoiceBackend
{
    None = 0,
    ElevenLabs = 1,
    Sapi = 2,
    Piper = 3
}

/// <summary>Per-machine voice configuration. Environment prefix: <c>BREADBOARD_VOICE_</c>.</summary>
public sealed class VoiceOptions
{
    public const int AlarmSampleRate = 44100;

    /// <summary>ElevenLabs returns PCM16 mono at this rate for <c>pcm_24000</c>.</summary>
    public const int ElevenLabsSampleRate = ElevenLabsVoiceClient.PcmSampleRate;

    /// <summary>Secret key. Empty means ElevenLabs is unavailable and local fallbacks run.</summary>
    public string ElevenLabsApiKey { get; set; } = string.Empty;

    /// <summary>Selected voice id from the account catalog.</summary>
    public string ElevenLabsVoiceId { get; set; } = string.Empty;

    /// <summary>Display name of the selected voice, for the status line only.</summary>
    public string ElevenLabsVoiceName { get; set; } = string.Empty;

    public string ElevenLabsModelId { get; set; } = ElevenLabsVoiceClient.DefaultModelId;

    public string ElevenLabsBaseUrl { get; init; } = ElevenLabsVoiceClient.DefaultBaseUrl;

    public double ElevenLabsStability { get; init; } = 0.45;

    public double ElevenLabsSimilarity { get; init; } = 0.80;

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

    public int SpeakerId => PiperSpeakerId;

    public string KokoroModelPath { get; init; } = string.Empty;
    public string KokoroTokensPath { get; init; } = string.Empty;
    public string KokoroDataDir { get; init; } = string.Empty;
    public string KokoroLexiconPath { get; init; } = string.Empty;
    public string KokoroDictDir { get; init; } = string.Empty;

    /// <summary>Piper length scale; above 1 speaks a little more slowly.</summary>
    public float PiperLengthScale { get; init; } = 1.04f;

    public float PiperNoiseScale { get; init; } = 0.667f;

    public float PiperNoiseW { get; init; } = 0.8f;

    /// <summary>Silence Piper inserts between its own sentence splits, in milliseconds.</summary>
    public int PiperSentenceSilenceMs { get; init; } = 280;

    public string AlarmWavPath { get; init; } = Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav");

    /// <summary>Speech rate multiplier. Applies to Piper and to ElevenLabs v2.5 models.</summary>
    public float Speed { get; init; } = 0.97f;

    /// <summary>Silence inserted after a comma or an unpunctuated clause, in milliseconds.</summary>
    public int ClausePauseMs { get; init; } = 85;

    /// <summary>Silence inserted after a full stop / question / exclamation mark, in milliseconds.</summary>
    public int SentencePauseMs { get; init; } = 240;

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
            ElevenLabsApiKey = ReadString("ELEVENLABS_KEY")
                               ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")?.Trim()
                               ?? string.Empty,
            ElevenLabsVoiceId = ReadString("ELEVENLABS_VOICE_ID") ?? string.Empty,
            ElevenLabsVoiceName = ReadString("ELEVENLABS_VOICE_NAME") ?? string.Empty,
            ElevenLabsModelId = ReadString("ELEVENLABS_MODEL_ID") ?? ElevenLabsVoiceClient.DefaultModelId,
            ElevenLabsBaseUrl = ReadString("ELEVENLABS_BASE_URL") ?? ElevenLabsVoiceClient.DefaultBaseUrl,
            ElevenLabsStability = ReadDouble("ELEVENLABS_STABILITY") ?? 0.45,
            ElevenLabsSimilarity = ReadDouble("ELEVENLABS_SIMILARITY") ?? 0.80,
            PiperExePath = ReadString("PIPER_EXE") ?? Path.Combine("assets", "models", "piper", "piper.exe"),
            PiperModelPath = ReadString("PIPER_MODEL") ?? Path.Combine("assets", "models", "piper", "bmo.onnx"),
            PiperConfigPath = ReadString("PIPER_CONFIG") ?? string.Empty,
            PreferredBackend = VoiceEnginePreferences.Parse(ReadString("BACKEND")),
            PiperSpeakerId = ReadInt("PIPER_SPEAKER") ?? -1,
            PiperLengthScale = ReadFloat("PIPER_LENGTH_SCALE") ?? 1.04f,
            PiperNoiseScale = ReadFloat("PIPER_NOISE_SCALE") ?? 0.667f,
            PiperNoiseW = ReadFloat("PIPER_NOISE_W") ?? 0.8f,
            PiperSentenceSilenceMs = ReadInt("PIPER_SENTENCE_SILENCE_MS") ?? 280,
            AlarmWavPath = ReadString("ALARM_WAV") ?? Path.Combine("assets", "audio_reflex", "alarm_vcc_gnd_short.wav"),
            Speed = ReadFloat("SPEED") ?? 0.97f,
            ClausePauseMs = ReadInt("CLAUSE_PAUSE_MS") ?? 85,
            SentencePauseMs = ReadInt("SENTENCE_PAUSE_MS") ?? 240,
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

    /// <summary>Returns a copy bound to another ElevenLabs key, voice or model.</summary>
    public VoiceOptions WithElevenLabs(string apiKey, string voiceId, string modelId, string voiceName = "")
    {
        VoiceOptions clone = (VoiceOptions)MemberwiseClone();
        clone.ElevenLabsApiKey = apiKey?.Trim() ?? string.Empty;
        clone.ElevenLabsVoiceId = voiceId?.Trim() ?? string.Empty;
        clone.ElevenLabsModelId = string.IsNullOrWhiteSpace(modelId)
            ? ElevenLabsVoiceClient.DefaultModelId
            : modelId.Trim();
        clone.ElevenLabsVoiceName = voiceName?.Trim() ?? string.Empty;
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

    private static double? ReadDouble(string suffix) =>
        double.TryParse(ReadString(suffix), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
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
/// ElevenLabs is preferred; Piper and SAPI keep the app speaking offline.
/// </summary>
public sealed class VoiceSynthesisEngine : IDisposable
{
    private VoiceOptions _options;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _alarmSync = new();
    private readonly byte[] _alarmWavBytes;

    private ElevenLabsVoiceClient? _elevenLabs;
    private PiperTtsEngine? _piper;
    private object? _sapiVoice;
    private string _sapiVoiceName = string.Empty;
    private bool _sapiSsmlSupported = true;
    private bool _disposed;

    public VoiceSynthesisEngine(VoiceOptions? options = null)
    {
        _options = options ?? VoiceOptions.FromEnvironment();
        string baseDirectory = AppContext.BaseDirectory;

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

    /// <summary>Human-readable description of the active voice, e.g. "ELEVENLABS / Rachel".</summary>
    public string VoiceProfileName { get; private set; } = "SILENT";

    public bool IsAvailable => Backend != VoiceBackend.None;

    public string? LastError { get; private set; }

    public string PiperExePath { get; }

    public string PiperModelPath { get; }

    public string PiperConfigPath { get; }

    /// <summary>True when the Piper executable, model and JSON are all on disk.</summary>
    public bool HasPiperVoice => PiperTtsEngine.IsAvailable(PiperExePath, PiperModelPath, PiperConfigPath);

    public string AlarmWavPath { get; }

    public bool HasAlarmClip => _alarmWavBytes.Length > 44;

    /// <summary>Lists the ElevenLabs voices for the supplied key (never throws).</summary>
    public Task<IReadOnlyList<ElevenLabsVoice>> ListVoicesAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        _elevenLabs ??= new ElevenLabsVoiceClient();
        return _elevenLabs.ListVoicesAsync(apiKey, _options.ElevenLabsBaseUrl, cancellationToken);
    }

    /// <summary>Last catalog/transport failure from ElevenLabs.</summary>
    public string? VoiceCatalogError => _elevenLabs?.LastError;

    /// <summary>True when a key and a voice are configured for the cloud voice.</summary>
    public bool HasElevenLabsVoice =>
        ElevenLabsVoiceClient.HasApiKey(_options.ElevenLabsApiKey)
        && !string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceId);

    /// <summary>
    /// Applies a new key/voice/model at runtime and immediately makes ElevenLabs
    /// the active engine when the configuration is complete.
    /// </summary>
    public async Task<bool> ApplyElevenLabsSettingsAsync(
        string apiKey,
        string voiceId,
        string modelId,
        string voiceName = "",
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _options = _options.WithElevenLabs(apiKey, voiceId, modelId, voiceName);

            bool complete = HasElevenLabsVoice;
            if (complete)
                _options = _options.WithPreferredBackend(VoiceEnginePreference.ElevenLabs);

            DisposeBackends();
            InitializeBackend();
            return Backend != VoiceBackend.None;
        }
        finally
        {
            _speechGate.Release();
        }
    }

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

    /// <summary>Speaks one complete sentence or paragraph with the active engine.</summary>
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
                VoiceBackend.ElevenLabs => await SpeakElevenLabsAsync(spoken, cancellationToken).ConfigureAwait(false),
                VoiceBackend.Piper => await SpeakPiperAsync(spoken, cancellationToken).ConfigureAwait(false),
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

                case VoiceEnginePreference.ElevenLabs:
                    if (TryCreateElevenLabs())
                    {
                        Backend = VoiceBackend.ElevenLabs;
                        VoiceProfileName = BuildElevenLabsProfileName();
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
            VoiceEnginePreference.Piper => new[] { VoiceEnginePreference.Piper, VoiceEnginePreference.ElevenLabs, VoiceEnginePreference.Sapi },
            VoiceEnginePreference.ElevenLabs => new[] { VoiceEnginePreference.ElevenLabs, VoiceEnginePreference.Piper, VoiceEnginePreference.Sapi },
            VoiceEnginePreference.Sapi => new[] { VoiceEnginePreference.Sapi, VoiceEnginePreference.Piper, VoiceEnginePreference.ElevenLabs },
            _ => new[] { VoiceEnginePreference.ElevenLabs, VoiceEnginePreference.Piper, VoiceEnginePreference.Sapi }
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

    private bool TryCreateElevenLabs()
    {
        if (!ElevenLabsVoiceClient.HasApiKey(_options.ElevenLabsApiKey))
        {
            LastError = "ElevenLabs API key is not set. Paste the key in AI VOICE to use the cloud voice.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceId))
        {
            LastError = "ElevenLabs is configured but no voice is selected. Refresh the voice list and pick one.";
            return false;
        }

        _elevenLabs ??= new ElevenLabsVoiceClient();
        return true;
    }

    private string BuildElevenLabsProfileName()
    {
        string voice = string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceName)
            ? _options.ElevenLabsVoiceId
            : _options.ElevenLabsVoiceName;

        string model = string.IsNullOrWhiteSpace(_options.ElevenLabsModelId)
            ? ElevenLabsVoiceClient.DefaultModelId
            : _options.ElevenLabsModelId;

        return $"ELEVENLABS / {voice} / {model}";
    }

    /// <summary>
    /// Speaks through ElevenLabs. A transport failure (offline laptop, expired
    /// quota) falls back to the local voice for that utterance so the operator
    /// still hears the answer; the error stays in <see cref="LastError"/>.
    /// </summary>
    private async Task<bool> SpeakElevenLabsAsync(string text, CancellationToken cancellationToken)
    {
        ElevenLabsVoiceClient? client = _elevenLabs;
        if (client is null)
            return false;

        try
        {
            ElevenLabsAudio audio = await client.SynthesizePcmAsync(
                _options.ElevenLabsApiKey,
                _options.ElevenLabsBaseUrl,
                _options.ElevenLabsVoiceId,
                _options.ElevenLabsModelId,
                text,
                speed: Math.Clamp(_options.Speed, 0.7f, 1.2f),
                stability: _options.ElevenLabsStability,
                similarityBoost: _options.ElevenLabsSimilarity,
                cancellationToken).ConfigureAwait(false);

            if (audio.Pcm.Length == 0)
            {
                LastError = "ElevenLabs returned no audio.";
                return false;
            }

            return await PlayPcmAsync(audio.Pcm, audio.SampleRate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ElevenLabsException ex)
        {
            LastError = ex.Message;
            return await SpeakLocalFallbackAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            LastError = $"ElevenLabs unavailable: {ex.Message}";
            return await SpeakLocalFallbackAsync(text, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Piper first, then SAPI. Never recurses into ElevenLabs.</summary>
    private async Task<bool> SpeakLocalFallbackAsync(string text, CancellationToken cancellationToken)
    {
        if (_piper is null && HasPiperVoice)
            _piper = new PiperTtsEngine(PiperExePath, PiperModelPath, PiperConfigPath);

        if (_piper is not null)
        {
            try
            {
                if (await SpeakPiperAsync(text, cancellationToken).ConfigureAwait(false))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                LastError = $"Piper fallback failed: {ex.Message}";
            }
        }

        if (_options.EnableSapiFallback)
        {
            if (_sapiVoice is null)
                TryCreateSapi();

            if (_sapiVoice is not null)
                return await SpeakSapiAsync(text, cancellationToken).ConfigureAwait(false);
        }

        return false;
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

        return await PlayPcmAsync(pcm, sampleRate, cancellationToken).ConfigureAwait(false);
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

    private Task<bool> SpeakSapiAsync(string sentence, CancellationToken cancellationToken)
    {
        object? voice = _sapiVoice;
        if (voice is null)
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            using var affinity = CpuAffinityManager.PinCurrentThreadFor(AffinityRole.Ai);
            cancellationToken.ThrowIfCancellationRequested();

            if (_options.BmoVoice && _sapiSsmlSupported)
            {
                try
                {
                    int sapiPitch = _options.BmoVoice ? -1 : 0;
                    int sapiRate = _options.BmoVoice ? -2 : 0;
                    voice.GetType().InvokeMember(
                        "Speak",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: voice,
                        args: new object[]
                        {
                            BuildSapiSsml(sentence, sapiPitch, sapiRate, _options.SentencePauseMs),
                            256
                        });

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
    /// accepts. Public so the exact contour and escaping are testable without an audio device.
    /// </summary>
    public static string BuildSapiSsml(
        string text,
        int pitchPercent = 0,
        int ratePercent = -3,
        int sentenceBreakMs = 240)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<string> sentences = SpeechTextNormalizer.SplitSentences(text);
        var builder = new StringBuilder(text.Length + 128);
        builder.Append("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">");

        if (sentences.Count == 0)
        {
            AppendSapiProsody(builder, EscapeXml(text), pitchPercent, ratePercent);
        }
        else
        {
            int breakMs = Math.Clamp(sentenceBreakMs, 0, 800);

            for (int i = 0; i < sentences.Count; i++)
            {
                string sentence = sentences[i];
                char terminator = sentence[^1];
                int pitch = pitchPercent + SapiPitchContour(i, sentences.Count, terminator);
                int rate = ratePercent;

                if (sentences.Count > 1 && i == sentences.Count - 1 && terminator != '?')
                    rate -= 1;

                AppendSapiProsody(builder, EscapeXml(sentence), pitch, rate);

                if (i < sentences.Count - 1 && breakMs > 0)
                    builder.Append("<break time=\"").Append(breakMs).Append("ms\"/>");
            }
        }

        builder.Append("</speak>");
        return builder.ToString();
    }

    private static void AppendSapiProsody(StringBuilder builder, string escaped, int pitch, int rate)
    {
        builder.Append("<prosody pitch=\"")
               .Append(FormatSapiPercent(pitch))
               .Append("%\" rate=\"")
               .Append(FormatSapiPercent(rate))
               .Append("%\">")
               .Append(escaped)
               .Append("</prosody>");
    }

    private static int SapiPitchContour(int index, int count, char terminator)
    {
        if (count <= 1)
            return 0;

        if (index == 0)
            return 1;

        if (index == count - 1)
            return terminator == '?' ? 1 : -1;

        return 0;
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
        _elevenLabs?.Dispose();
        _elevenLabs = null;
        _speechGate.Dispose();
    }
}
