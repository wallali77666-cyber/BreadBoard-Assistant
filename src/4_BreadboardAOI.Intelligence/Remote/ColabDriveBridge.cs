// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/ColabDriveBridge.cs
// PURPOSE: Uses the linked Google account as the control channel to Colab.
//          The app writes a one-time session key to a Drive file; the notebook
//          overwrites that file with its tunnel URL; the app polls it back.
//          Only the narrow drive.file scope is requested, so the app can see
//          files it created and nothing else in the user's Drive.
// ============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Drive file identity returned by the list call.</summary>
public sealed record ColabFileRef(string Id, string Name, DateTimeOffset? ModifiedUtc);

/// <summary>Reads and writes the shared Colab session file.</summary>
public sealed class ColabDriveBridge : IDisposable
{
    private const string DriveFiles = "https://www.googleapis.com/drive/v3/files";
    private const string DriveUpload = "https://www.googleapis.com/upload/drive/v3/files";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ColabDriveBridge(HttpMessageHandler? handler = null, string? fileName = null)
    {
        _ownsHttp = handler is null;
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        FileName = string.IsNullOrWhiteSpace(fileName) ? ColabSession.DefaultFileName : fileName.Trim();
    }

    public string FileName { get; }

    /// <summary>Finds the app-created session file, if the user has one.</summary>
    public async Task<ColabFileRef?> FindAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        string query = $"name='{FileName.Replace("'", "\\'", StringComparison.Ordinal)}' and trashed=false";
        string url =
            $"{DriveFiles}?q={Uri.EscapeDataString(query)}" +
            "&spaces=drive&pageSize=5&orderBy=modifiedTime%20desc" +
            "&fields=" + Uri.EscapeDataString("files(id,name,modifiedTime)");

        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, accessToken, content: null, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, "list Drive files");

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement file = files[0];
        string id = file.GetProperty("id").GetString() ?? string.Empty;
        string name = file.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? FileName : FileName;

        DateTimeOffset? modified = null;
        if (file.TryGetProperty("modifiedTime", out JsonElement modifiedElement)
            && modifiedElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(modifiedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            modified = parsed;
        }

        return string.IsNullOrEmpty(id) ? null : new ColabFileRef(id, name, modified);
    }

    /// <summary>Reads and parses the session file; null when absent or corrupt.</summary>
    public async Task<ColabSession?> ReadSessionAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ColabFileRef? file = await FindAsync(accessToken, cancellationToken).ConfigureAwait(false);
        if (file is null)
            return null;

        string url = $"{DriveFiles}/{Uri.EscapeDataString(file.Id)}?alt=media";
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, url, accessToken, content: null, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, body, "read the Colab session file");

        return ColabSessionJson.Deserialize(body);
    }

    /// <summary>Creates or overwrites the session file and returns what was written.</summary>
    public async Task<ColabSession> WriteSessionAsync(
        string accessToken,
        ColabSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        string json = ColabSessionJson.Serialize(session);
        ColabFileRef? existing = await FindAsync(accessToken, cancellationToken).ConfigureAwait(false);

        string fileId;

        if (existing is null)
        {
            string metadata = JsonSerializer.Serialize(
                new { name = FileName, mimeType = "application/json" },
                JsonOptions);

            using var createContent = new StringContent(metadata, Encoding.UTF8, "application/json");
            using HttpResponseMessage created = await SendAsync(
                    HttpMethod.Post,
                    $"{DriveFiles}?fields=id",
                    accessToken,
                    createContent,
                    cancellationToken)
                .ConfigureAwait(false);

            string createdBody = await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(created, createdBody, "create the Colab session file");

            using JsonDocument document = JsonDocument.Parse(createdBody);
            fileId = document.RootElement.GetProperty("id").GetString()
                     ?? throw new InvalidOperationException("Drive did not return a file id.");
        }
        else
        {
            fileId = existing.Id;
        }

        using var media = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage updated = await SendAsync(
                HttpMethod.Patch,
                $"{DriveUpload}/{Uri.EscapeDataString(fileId)}?uploadType=media",
                accessToken,
                media,
                cancellationToken)
            .ConfigureAwait(false);

        string updatedBody = await updated.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(updated, updatedBody, "write the Colab session file");

        return session;
    }

    /// <summary>Writes the "waiting" request the notebook polls for.</summary>
    public Task<ColabSession> RequestSessionAsync(
        string accessToken,
        string apiKey,
        string? notebookUrl,
        CancellationToken cancellationToken = default)
    {
        var session = new ColabSession
        {
            Status = ColabSession.WaitingStatus,
            ApiKey = apiKey,
            NotebookUrl = notebookUrl,
            Detail = "Waiting for the Colab notebook to publish its tunnel.",
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        return WriteSessionAsync(accessToken, session, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string accessToken,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string what)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 240)
            detail = detail[..240] + "...";

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture,
                $"Google Drive could not {what} ({(int)response.StatusCode}): {detail}"));
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>Result of a single tunnel health probe.</summary>
public sealed record RemoteProbeResult(bool IsHealthy, double LatencyMs, string Detail)
{
    public static RemoteProbeResult Offline(string detail) => new(false, 0d, detail);

    public string Describe() => IsHealthy
        ? string.Create(CultureInfo.InvariantCulture, $"ONLINE {LatencyMs:0} MS")
        : Detail;
}

/// <summary>Health check shared by the UI and the coordinator's startup path.</summary>
public static class RemoteEndpointProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<RemoteProbeResult> ProbeAsync(
        HttpClient http,
        string? endpointUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        string? healthUrl = ColabEndpoints.BuildHealthEndpoint(endpointUrl);
        if (healthUrl is null)
            return RemoteProbeResult.Offline("NO ENDPOINT");

        RemoteProbeResult? direct = await TryGetAsync(http, healthUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (direct is not null)
            return direct;

        // Some proxies expose only the OpenAI surface, so fall back to /v1/models.
        string? chat = ColabEndpoints.BuildChatEndpoint(endpointUrl);
        if (chat is null)
            return RemoteProbeResult.Offline("NO ENDPOINT");

        string models = chat[..^"/chat/completions".Length] + "/models";
        RemoteProbeResult? viaModels = await TryGetAsync(http, models, apiKey, cancellationToken).ConfigureAwait(false);
        return viaModels ?? RemoteProbeResult.Offline("TUNNEL UNREACHABLE");
    }

    private static async Task<RemoteProbeResult?> TryGetAsync(
        HttpClient http,
        string url,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            long start = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (response.IsSuccessStatusCode)
                return new RemoteProbeResult(true, elapsed, "ONLINE");

            // 401/403 means the tunnel is up but the key is stale: report reachable
            // so the UI shows the real problem instead of "offline".
            bool reachable = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden;

            return reachable
                ? new RemoteProbeResult(false, elapsed, $"KEY REJECTED ({(int)response.StatusCode})")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
