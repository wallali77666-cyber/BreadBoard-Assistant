// ============================================================================
// FILE: src/5_BreadboardAOI.App/Workspace/VoiceSettingsStore.cs
// PURPOSE: Remembers the operator's selected voice engine, ElevenLabs voice and
//          model between sessions. The API key is stored encrypted with Windows
//          DPAPI (CurrentUser), never in plain text. BREADBOARD_VOICE_BACKEND and
//          BREADBOARD_VOICE_ELEVENLABS_KEY still win, so QA scripts and a packaged
//          shortcut can force a configuration without editing JSON.
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;

namespace BreadboardAOI.App.Workspace;

/// <summary>Persisted speech configuration; <c>auto</c> is the safe default.</summary>
public sealed record VoiceSettingsState(
    string Engine = "auto",
    string? ElevenLabsVoiceId = null,
    string? ElevenLabsVoiceName = null,
    string? ElevenLabsModelId = null,
    string? ElevenLabsKey = null);

/// <summary>The operator's ElevenLabs identity, resolved from disk or environment.</summary>
public sealed record ElevenLabsSettings(string ApiKey, string VoiceId, string VoiceName, string ModelId)
{
    public bool HasKey => ElevenLabsVoiceClient.HasApiKey(ApiKey);

    public bool HasVoice => !string.IsNullOrWhiteSpace(VoiceId);
}

/// <summary>
/// Small JSON file under %LOCALAPPDATA%\BreadboardAOI. Voice selection is a
/// convenience, so a read/write failure must never stop the workstation.
/// </summary>
public static class VoiceSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BreadboardAOI",
        "voice-settings.json");

    /// <summary>True when BREADBOARD_VOICE_BACKEND is set to a valid engine key.</summary>
    public static bool IsEnvironmentLocked =>
        VoiceEnginePreferences.TryParse(
            Environment.GetEnvironmentVariable("BREADBOARD_VOICE_BACKEND"),
            out _);

    /// <summary>
    /// Precedence: environment variable, then the saved file, then AUTO. An
    /// invalid or missing value never throws.
    /// </summary>
    public static VoiceEnginePreference Load()
    {
        if (VoiceEnginePreferences.TryParse(
                Environment.GetEnvironmentVariable("BREADBOARD_VOICE_BACKEND"),
                out VoiceEnginePreference fromEnvironment))
        {
            return fromEnvironment;
        }

        try
        {
            VoiceSettingsState? state = ReadState();
            return VoiceEnginePreferences.Parse(state?.Engine);
        }
        catch
        {
            return VoiceEnginePreference.Auto;
        }
    }

    /// <summary>
    /// ElevenLabs key + voice + model. Environment wins over the saved file, and
    /// a key encrypted on another machine simply decrypts to an empty string.
    /// </summary>
    public static ElevenLabsSettings LoadElevenLabs()
    {
        VoiceSettingsState? state = ReadState();

        string envKey =
            Environment.GetEnvironmentVariable("BREADBOARD_VOICE_ELEVENLABS_KEY")?.Trim()
            ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")?.Trim()
            ?? string.Empty;

        string key = envKey.Length > 0 ? envKey : Unprotect(state?.ElevenLabsKey);

        string voiceId =
            Environment.GetEnvironmentVariable("BREADBOARD_VOICE_ELEVENLABS_VOICE_ID")?.Trim()
            ?? state?.ElevenLabsVoiceId
            ?? string.Empty;

        string voiceName = state?.ElevenLabsVoiceName ?? string.Empty;

        string modelId =
            Environment.GetEnvironmentVariable("BREADBOARD_VOICE_ELEVENLABS_MODEL_ID")?.Trim()
            ?? state?.ElevenLabsModelId
            ?? ElevenLabsVoiceClient.DefaultModelId;

        return new ElevenLabsSettings(key, voiceId, voiceName, modelId);
    }

    public static void Save(VoiceEnginePreference preference)
    {
        VoiceSettingsState? current = ReadState();
        SaveState(current, state => state with
        {
            Engine = VoiceEnginePreferences.ToKey(preference)
        });
    }

    public static void SaveElevenLabs(ElevenLabsSettings settings)
    {
        VoiceSettingsState? current = ReadState();
        SaveState(current, state => state with
        {
            ElevenLabsVoiceId = settings.VoiceId,
            ElevenLabsVoiceName = settings.VoiceName,
            ElevenLabsModelId = settings.ModelId,
            ElevenLabsKey = Protect(settings.ApiKey)
        });
    }

    private static VoiceSettingsState? ReadState()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            return JsonSerializer.Deserialize<VoiceSettingsState>(
                File.ReadAllText(FilePath),
                Options);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            return null;
        }
    }

    private static void SaveState(
        VoiceSettingsState? current,
        Func<VoiceSettingsState, VoiceSettingsState> update)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            VoiceSettingsState state = update(current ?? new VoiceSettingsState());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            // A persistence failure must never block a voice change.
        }
    }

    private static string? Protect(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return string.Empty;

        byte[]? blob = DpapiProtector.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()));
        return blob is { Length: > 0 } ? Convert.ToBase64String(blob) : null;
    }

    private static string Unprotect(string? protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
            return string.Empty;

        try
        {
            byte[] blob = Convert.FromBase64String(protectedKey);
            byte[]? plain = DpapiProtector.Unprotect(blob);
            return plain is { Length: > 0 } ? Encoding.UTF8.GetString(plain) : string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
