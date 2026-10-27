// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/ColabSession.cs
// PURPOSE: The hand-off contract between the desktop app, the user's Google
//          Drive and the Colab notebook. The app writes a "waiting" session
//          (with a one-time key); the notebook overwrites it with the tunnel
//          URL once llama-server is live. No model weights or credentials ever
//          travel through this file beyond the one-time bearer key.
// ============================================================================

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>State published to (and read back from) the shared Drive file.</summary>
public sealed record ColabSession
{
    /// <summary>The app has asked for a runtime; the notebook has not answered yet.</summary>
    public const string WaitingStatus = "waiting";

    /// <summary>llama-server is reachable through the tunnel.</summary>
    public const string ServingStatus = "serving";

    /// <summary>The runtime stopped or the tunnel closed.</summary>
    public const string StoppedStatus = "stopped";

    public const string DefaultFileName = "breadboardaoi-colab.json";

    public string Status { get; init; } = WaitingStatus;

    /// <summary>Public tunnel root, e.g. https://random-words.trycloudflare.com.</summary>
    public string? EndpointUrl { get; init; }

    /// <summary>One-time bearer key the notebook must present on every request.</summary>
    public string? ApiKey { get; init; }

    public string? Model { get; init; }

    public string? Gpu { get; init; }

    public string? Detail { get; init; }

    public string? NotebookUrl { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True only when the notebook reported a serving endpoint.</summary>
    [JsonIgnore]
    public bool IsServing =>
        string.Equals(Status, ServingStatus, StringComparison.OrdinalIgnoreCase)
        && ChatEndpoint is not null;

    [JsonIgnore]
    public string? ChatEndpoint => ColabEndpoints.BuildChatEndpoint(EndpointUrl);

    /// <summary>
    /// Accepts "host:port", "https://host", a base URL or a full
    /// "/v1/chat/completions" URL and returns a canonical scheme://host[:port]/path
    /// with no trailing slash. Returns null when the value is not usable.
    /// </summary>
    public static string? NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string value = raw.Trim().Trim('"', '\'');
        if (value.Length == 0)
            return null;

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return null;

        bool http = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!http || uri.Host.Length == 0)
            return null;

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }
}

/// <summary>Pure URL arithmetic shared by the app, the probe and the unit tests.</summary>
public static class ColabEndpoints
{
    private const string ChatPath = "/v1/chat/completions";

    /// <summary>Canonical OpenAI-compatible chat endpoint for a tunnel root.</summary>
    public static string? BuildChatEndpoint(string? raw)
    {
        string? baseUrl = ColabSession.NormalizeBaseUrl(raw);
        if (baseUrl is null)
            return null;

        if (baseUrl.EndsWith(ChatPath, StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/chat/completions";

        return baseUrl + ChatPath;
    }

    /// <summary>llama-server health endpoint for a tunnel root.</summary>
    public static string? BuildHealthEndpoint(string? raw)
    {
        string? baseUrl = ColabSession.NormalizeBaseUrl(raw);
        if (baseUrl is null)
            return null;

        if (baseUrl.EndsWith(ChatPath, StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^ChatPath.Length];
        else if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];

        return baseUrl + "/health";
    }

    /// <summary>Short host label for the telemetry docks.</summary>
    public static string DescribeHost(string? raw)
    {
        string? baseUrl = ColabSession.NormalizeBaseUrl(raw);
        if (baseUrl is null)
            return "NO ENDPOINT";

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri.Authority : "NO ENDPOINT";
    }
}

/// <summary>JSON helpers so the app and the notebook agree on one format.</summary>
public static class ColabSessionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize(ColabSession session) => JsonSerializer.Serialize(session, Options);

    public static ColabSession? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ColabSession>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The endpoint the cognitive loop should stream from.</summary>
public sealed record RemoteTutorTarget(string EndpointUrl, string? ApiKey, string? ModelAlias)
{
    public string ChatEndpoint =>
        ColabEndpoints.BuildChatEndpoint(EndpointUrl)
        ?? throw new InvalidOperationException($"'{EndpointUrl}' is not a usable remote endpoint.");

    public string DisplayHost => ColabEndpoints.DescribeHost(EndpointUrl);

    public static RemoteTutorTarget? FromSession(ColabSession? session)
    {
        if (session is null || !session.IsServing)
            return null;

        return new RemoteTutorTarget(
            session.EndpointUrl!,
            string.IsNullOrWhiteSpace(session.ApiKey) ? null : session.ApiKey,
            string.IsNullOrWhiteSpace(session.Model) ? null : session.Model);
    }
}

/// <summary>Wire-level choice for the cognitive loop.</summary>
public enum RemoteTutorRoute
{
    /// <summary>Stream from the Colab tunnel.</summary>
    Remote = 0,

    /// <summary>Stream from a local llama-server (or its rule-based fallback).</summary>
    Local = 1,

    /// <summary>No tutor at all.</summary>
    Offline = 2
}

/// <summary>Deterministic backend selection, kept pure so it is unit-testable.</summary>
public static class RemoteTutorSelector
{
    public static RemoteTutorRoute Select(bool preferRemote, bool remoteServing, bool remoteHealthy, bool localAvailable)
    {
        if (preferRemote && remoteServing && remoteHealthy)
            return RemoteTutorRoute.Remote;

        return localAvailable ? RemoteTutorRoute.Local : RemoteTutorRoute.Offline;
    }
}
