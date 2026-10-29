// ============================================================================
// FILE: src/5_BreadboardAOI.App/Workspace/VoiceSettingsStore.cs
// PURPOSE: Remembers the operator's selected voice engine between sessions.
//          BREADBOARD_VOICE_BACKEND wins over the saved value, so QA scripts and
//          a packaged shortcut can force a backend without editing JSON.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;
using BreadboardAOI.Intelligence;

namespace BreadboardAOI.App.Workspace;

/// <summary>Persisted speech-engine choice; <c>auto</c> is the safe default.</summary>
public sealed record VoiceSettingsState(string Engine = "auto");

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
            if (!File.Exists(FilePath))
                return VoiceEnginePreference.Auto;

            VoiceSettingsState? state = JsonSerializer.Deserialize<VoiceSettingsState>(
                File.ReadAllText(FilePath),
                Options);

            return VoiceEnginePreferences.Parse(state?.Engine);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            return VoiceEnginePreference.Auto;
        }
    }

    public static void Save(VoiceEnginePreference preference)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var state = new VoiceSettingsState(VoiceEnginePreferences.ToKey(preference));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            // A persistence failure must never block a voice change.
        }
    }
}
