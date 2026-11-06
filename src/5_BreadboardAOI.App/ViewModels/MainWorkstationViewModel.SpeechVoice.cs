// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.SpeechVoice.cs
// PURPOSE: Partial class for TTS voice engine selection, ElevenLabs client
//          integration, voice testing, and audio routing.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.App.Workspace;
using BreadboardAOI.Intelligence;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    /// <summary>
    /// Switches the live tutor voice. The view-model persists the choice, but
    /// BREADBOARD_VOICE_BACKEND still wins on the next launch.
    /// </summary>
    [RelayCommand]
    private async Task SelectVoiceEngineAsync(string? preference)
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "VOICE: the pipeline is not ready yet.";
            return;
        }

        if (!VoiceEnginePreferences.TryParse(preference, out VoiceEnginePreference requested))
            return;

        IsVoiceEngineBusy = true;
        try
        {
            if (requested == VoiceEnginePreference.ElevenLabs
                && (!ElevenLabsVoiceClient.HasApiKey(ElevenLabsApiKey) || SelectedElevenLabsVoice is null))
            {
                // No key/voice yet: explain instead of silently falling back to SAPI.
                StatusLine = "VOICE: paste an ElevenLabs key and pick a voice first.";
                VoiceSetupNote = "ElevenLabs needs an API key and a selected voice. Use REFRESH VOICES after pasting the key.";
                return;
            }

            StatusLine = $"VOICE: switching to {VoiceEnginePreferences.ToDisplayName(requested)}...";
            bool switched = await coordinator.SetVoiceBackendAsync(requested);

            VoiceSettingsStore.Save(requested);
            VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";

            BreadboardAOI.Intelligence.VoiceBackend active = coordinator.ActiveVoiceBackend;
            bool fellBack = requested switch
            {
                VoiceEnginePreference.Piper => active != BreadboardAOI.Intelligence.VoiceBackend.Piper,
                VoiceEnginePreference.ElevenLabs => active != BreadboardAOI.Intelligence.VoiceBackend.ElevenLabs,
                VoiceEnginePreference.Sapi => active != BreadboardAOI.Intelligence.VoiceBackend.Sapi,
                _ => false
            };

            StatusLine = switched
                ? fellBack
                    ? $"VOICE: {coordinator.VoiceBackendDescription} active (fallback)."
                    : $"VOICE: {coordinator.VoiceBackendDescription} active."
                : $"VOICE: no engine could speak. {coordinator.VoiceNote}".TrimEnd();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"VOICE: switch failed - {ex.Message}";
        }
        finally
        {
            IsVoiceEngineBusy = false;
        }
    }

    /// <summary>Auditions the active engine with a short natural line.</summary>
    [RelayCommand]
    private async Task TestVoiceAsync()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "VOICE: the pipeline is not ready yet.";
            return;
        }

        IsVoiceEngineBusy = true;
        try
        {
            StatusLine = $"VOICE: speaking with {coordinator.VoiceBackendDescription}...";
            bool spoken = await coordinator.SpeakTestLineAsync();

            VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";
            StatusLine = spoken
                ? $"VOICE: test line played by {coordinator.VoiceBackendDescription}."
                : $"VOICE: test failed. {coordinator.VoiceNote}".TrimEnd();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"VOICE: test failed - {ex.Message}";
        }
        finally
        {
            IsVoiceEngineBusy = false;
        }
    }

    /// <summary>
    /// Loads the voice catalog for the pasted key. Selecting a voice applies it
    /// immediately, so the operator hears the exact voice they picked.
    /// </summary>
    [RelayCommand]
    private async Task RefreshVoicesAsync()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
        {
            VoiceCatalogNote = "The pipeline is still starting.";
            return;
        }

        if (!ElevenLabsVoiceClient.HasApiKey(ElevenLabsApiKey))
        {
            VoiceCatalogNote = "Paste your ElevenLabs API key first.";
            return;
        }

        IsVoiceCatalogBusy = true;
        VoiceCatalogNote = "Loading voices from ElevenLabs...";

        try
        {
            IReadOnlyList<ElevenLabsVoice> voices = await coordinator
                .ListElevenLabsVoicesAsync(ElevenLabsApiKey.Trim())
                .ConfigureAwait(true);

            ElevenLabsVoice? previous = SelectedElevenLabsVoice;
            _voiceSelectionSilent = true;
            ElevenLabsVoices.Clear();
            foreach (ElevenLabsVoice voice in voices)
                ElevenLabsVoices.Add(voice);
            _voiceSelectionSilent = false;

            if (ElevenLabsVoices.Count == 0)
            {
                VoiceCatalogNote = coordinator.VoiceCatalogError
                    ?? "No voices returned. Check the key and the account.";
                return;
            }

            ElevenLabsVoice? match = previous is null
                ? null
                : ElevenLabsVoices.FirstOrDefault(v => v.VoiceId == previous.VoiceId);

            SelectedElevenLabsVoice = match ?? ElevenLabsVoices[0];
            VoiceCatalogNote = $"{ElevenLabsVoices.Count} voice(s) available. Picked {SelectedElevenLabsVoice.DisplayName}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            VoiceCatalogNote = $"Voice list failed: {ex.Message}";
        }
        finally
        {
            IsVoiceCatalogBusy = false;
        }
    }

    /// <summary>Persists and activates the current ElevenLabs key + voice + model.</summary>
    [RelayCommand]
    private async Task ApplyVoiceAsync()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null || SelectedElevenLabsVoice is null)
        {
            VoiceCatalogNote = "Pick a voice first.";
            return;
        }

        IsVoiceEngineBusy = true;
        try
        {
            var settings = new ElevenLabsSettings(
                ElevenLabsApiKey.Trim(),
                SelectedElevenLabsVoice.VoiceId,
                SelectedElevenLabsVoice.Name,
                ElevenLabsModelId);

            bool ok = await coordinator.ApplyElevenLabsVoiceAsync(
                settings.ApiKey,
                settings.VoiceId,
                settings.ModelId,
                settings.VoiceName);

            if (ok)
                VoiceSettingsStore.SaveElevenLabs(settings);

            VoiceSettingsStore.Save(VoiceEnginePreference.ElevenLabs);
            VoiceBackend = $"VOICE: {coordinator.VoiceBackendDescription}";
            VoiceSetupNote = coordinator.VoiceNote;
            VoiceCatalogNote = ok
                ? $"{coordinator.VoiceBackendDescription} is speaking."
                : coordinator.VoiceNote;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            VoiceCatalogNote = $"Voice failed: {ex.Message}";
        }
        finally
        {
            IsVoiceEngineBusy = false;
        }
    }

    private void PopulateSavedVoice(ElevenLabsSettings settings)
    {
        _voiceSelectionSilent = true;
        ElevenLabsVoices.Clear();
        foreach (ElevenLabsVoice v in ElevenLabsVoiceClient.DefaultVoices)
            ElevenLabsVoices.Add(v);

        if (settings.HasVoice)
        {
            ElevenLabsVoice? match = ElevenLabsVoices.FirstOrDefault(v => v.VoiceId == settings.VoiceId);
            if (match is null)
            {
                match = new ElevenLabsVoice(
                    settings.VoiceId,
                    settings.VoiceName.Length > 0 ? settings.VoiceName : settings.VoiceId,
                    "account",
                    string.Empty);
                ElevenLabsVoices.Insert(0, match);
            }
            SelectedElevenLabsVoice = match;
            if (settings.HasKey)
                VoiceCatalogNote = $"Selected voice: {match.DisplayName}. Refresh to load account catalog.";
        }
        else if (ElevenLabsVoices.Count > 0)
        {
            SelectedElevenLabsVoice = ElevenLabsVoices[0];
        }
        _voiceSelectionSilent = false;
    }

    partial void OnSelectedElevenLabsVoiceChanged(ElevenLabsVoice? value)
    {
        if (_voiceSelectionSilent || value is null || _coordinator is null)
            return;

        _ = ApplyVoiceAsync();
    }

    partial void OnElevenLabsModelIdChanged(string value)
    {
        if (_voiceSelectionSilent || _coordinator is null || SelectedElevenLabsVoice is null)
            return;

        _ = ApplyVoiceAsync();
    }

}