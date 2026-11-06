// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/VoiceEnginePreferences.cs
// PURPOSE: The operator-facing choice of speech engine. "ElevenLabs" is the
//          cloud neural voice (voice picked in the workstation), "Piper" is a
//          local VITS model, and "Sapi" is the Windows fallback.
//          The enum is deliberately separate from VoiceBackend: a preference can
//          fall back at runtime, and the Hardware dock reports what actually ran.
// ============================================================================

using System;

namespace BreadboardAOI.Intelligence;

/// <summary>Requested speech engine; the engine may fall back if its files are missing.</summary>
public enum VoiceEnginePreference
{
    Auto = 0,
    Piper = 1,
    ElevenLabs = 2,
    Sapi = 3
}

public static class VoiceEnginePreferences
{
    /// <summary>
    /// Accepts the names used by the UI, the CLI and the documentation:
    /// <c>auto</c>, <c>elevenlabs</c> / <c>eleven</c>, <c>piper</c> / <c>bmo</c>
    /// and <c>sapi</c>. Legacy <c>kokoro</c> settings load as ElevenLabs so an
    /// old settings file does not silently mute the workstation.
    /// </summary>
    public static bool TryParse(string? value, out VoiceEnginePreference preference)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto":
            case "default":
                preference = VoiceEnginePreference.Auto;
                return true;
            case "elevenlabs":
            case "eleven-labs":
            case "eleven":
            case "11labs":
                preference = VoiceEnginePreference.ElevenLabs;
                return true;
            case "kokoro":
            case "sherpa":
            case "sherpa-onnx":
                preference = VoiceEnginePreference.ElevenLabs;
                return true;
            case "piper":
            case "bmo":
            case "bmo-piper":
            case "bmo_piper":
                preference = VoiceEnginePreference.Piper;
                return true;
            case "sapi":
            case "windows":
            case "windows-sapi":
                preference = VoiceEnginePreference.Sapi;
                return true;
            default:
                preference = VoiceEnginePreference.Auto;
                return false;
        }
    }

    public static VoiceEnginePreference Parse(
        string? value,
        VoiceEnginePreference fallback = VoiceEnginePreference.Auto) =>
        TryParse(value, out VoiceEnginePreference preference) ? preference : fallback;

    public static string ToKey(VoiceEnginePreference preference) => preference switch
    {
        VoiceEnginePreference.ElevenLabs => "elevenlabs",
        VoiceEnginePreference.Piper => "piper",
        VoiceEnginePreference.Sapi => "sapi",
        _ => "auto"
    };

    public static string ToDisplayName(VoiceEnginePreference preference) => preference switch
    {
        VoiceEnginePreference.ElevenLabs => "ELEVENLABS",
        VoiceEnginePreference.Piper => "PIPER",
        VoiceEnginePreference.Sapi => "WINDOWS SAPI",
        _ => "AUTO"
    };
}
