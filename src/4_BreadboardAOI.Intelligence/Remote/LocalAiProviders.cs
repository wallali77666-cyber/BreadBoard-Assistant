// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/LocalAiProviders.cs
// PURPOSE: The cognitive loop does not have to run in the cloud. LM Studio and
//          Ollama both expose an OpenAI-compatible /v1 surface on localhost, so
//          the same RemoteTutorTarget plumbing points at them without a tunnel,
//          a key, or any model download by this app.
//
// NOTE: nothing here launches or installs a provider. The operator runs their
//       own LM Studio / Ollama; the workstation only discovers the models that
//       are already loaded and then streams diagnoses from them.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>One local, OpenAI-compatible inference server.</summary>
public sealed record LocalAiPreset(string Id, string Label, string BaseUrl, string? ApiKey)
{
    /// <summary>Chat endpoint the pipeline streams from.</summary>
    public string ChatEndpoint => BaseUrl + "/v1/chat/completions";
}

public static class LocalAiProviders
{
    /// <summary>Presets for the providers the workstation knows how to probe.</summary>
    public static IReadOnlyList<LocalAiPreset> All { get; } = new[]
    {
        new LocalAiPreset("lmstudio", "LM STUDIO", "http://127.0.0.1:1234", null),
        new LocalAiPreset("ollama", "OLLAMA", "http://127.0.0.1:11434", "ollama"),
        new LocalAiPreset("llamacpp", "LLAMA.CPP", "http://127.0.0.1:8089", null)
    };

    public static LocalAiPreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        foreach (LocalAiPreset preset in All)
        {
            if (string.Equals(preset.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return null;
    }

    /// <summary>Reads the ids from an OpenAI-compatible <c>/v1/models</c> payload.</summary>
    public static IReadOnlyList<string> ParseModelIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var ids = new List<string>(8);
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    string? value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        ids.Add(value);
                }
            }

            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Lists the models the local server currently serves.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(
        HttpClient http,
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        string? normalized = ColabSession.NormalizeBaseUrl(baseUrl);
        if (normalized is null)
            return Array.Empty<string>();

        using var request = new HttpRequestMessage(HttpMethod.Get, normalized + "/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return Array.Empty<string>();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseModelIds(json);
    }

    /// <summary>Prefers the operator's model name, otherwise the first loaded model.</summary>
    public static string? ChooseModel(IReadOnlyList<string> models, string? preferred)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            string wanted = preferred.Trim();
            foreach (string model in models)
            {
                if (string.Equals(model, wanted, StringComparison.OrdinalIgnoreCase))
                    return model;
            }

            foreach (string model in models)
            {
                if (model.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    return model;
            }
        }

        return models.Count > 0 ? models[0] : null;
    }
}
