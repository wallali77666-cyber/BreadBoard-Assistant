// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/GoogleOAuthClient.cs
// PURPOSE: Native "Sign in with Google" for a desktop app: OAuth 2.0
//          authorization-code flow with PKCE and a loopback redirect, matching
//          Google's guidance for installed apps. No Google client library and no
//          extra NuGet package is required, so the app still builds offline.
//
// NOTE: the callback uses a raw TcpListener instead of HttpListener. Loopback
//          HttpListener prefixes can require an http.sys URL ACL, and the app
//          must link accounts on an ordinary non-elevated laptop.
//
// SECURITY: the refresh token never leaves the machine. It is written to
//          %LOCALAPPDATA%\BreadboardAOI with the user profile's own ACLs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>OAuth client registration and Google endpoints.</summary>
public sealed class GoogleOAuthOptions
{
    public const string EnvironmentPrefix = "BREADBOARD_GOOGLE_";
    public const string DefaultScopes = "openid email profile https://www.googleapis.com/auth/drive.file";

    public string ClientId { get; init; } = string.Empty;

    /// <summary>Desktop ("installed app") clients ship a non-secret; optional with PKCE.</summary>
    public string? ClientSecret { get; init; }

    public string Scopes { get; init; } = DefaultScopes;

    public string AuthorizationEndpoint { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";

    public string UserInfoEndpoint { get; init; } = "https://openidconnect.googleapis.com/v1/userinfo";

    public string RevokeEndpoint { get; init; } = "https://oauth2.googleapis.com/revoke";

    /// <summary>How long the loopback callback waits for the user.</summary>
    public double AuthorizationTimeoutSeconds { get; init; } = 300d;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public GoogleOAuthOptions WithClient(string? clientId, string? clientSecret) => new()
    {
        ClientId = string.IsNullOrWhiteSpace(clientId) ? ClientId : clientId.Trim(),
        ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? ClientSecret : clientSecret.Trim(),
        Scopes = Scopes,
        AuthorizationEndpoint = AuthorizationEndpoint,
        TokenEndpoint = TokenEndpoint,
        UserInfoEndpoint = UserInfoEndpoint,
        RevokeEndpoint = RevokeEndpoint,
        AuthorizationTimeoutSeconds = AuthorizationTimeoutSeconds
    };

    public static GoogleOAuthOptions FromEnvironment()
    {
        string? clientId = Read("CLIENT_ID");
        string? clientSecret = Read("CLIENT_SECRET");
        string? scopes = Read("SCOPES");

        return new GoogleOAuthOptions
        {
            ClientId = clientId ?? string.Empty,
            ClientSecret = clientSecret,
            Scopes = scopes ?? DefaultScopes
        };

        static string? Read(string suffix)
        {
            string? value = Environment.GetEnvironmentVariable(EnvironmentPrefix + suffix);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        }
    }
}

/// <summary>Everything needed to call Google APIs, cached on disk.</summary>
public sealed record GoogleTokenSet
{
    public string AccessToken { get; init; } = string.Empty;

    public string? RefreshToken { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Scope { get; init; }

    public string? TokenType { get; init; } = "Bearer";

    public string? Email { get; init; }

    public string? DisplayName { get; init; }

    public DateTimeOffset ObtainedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);

    public bool IsExpired(DateTimeOffset now, TimeSpan? skew = null) =>
        now + (skew ?? TimeSpan.FromMinutes(2)) >= ExpiresAtUtc;

    public string DescribeAccount()
    {
        if (string.IsNullOrWhiteSpace(Email))
            return "GOOGLE ACCOUNT LINKED";

        return string.IsNullOrWhiteSpace(DisplayName)
            ? Email!
            : $"{DisplayName} <{Email}>";
    }
}

/// <summary>Authorization-code + PKCE client for installed apps.</summary>
public sealed class GoogleOAuthClient : IDisposable
{
    private const string CallbackPath = "/oauth2/callback";

    private readonly GoogleOAuthOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GoogleOAuthClient(GoogleOAuthOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? GoogleOAuthOptions.FromEnvironment();
        _ownsHttp = handler is null;
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public GoogleOAuthOptions Options => _options;

    /// <summary>RFC 7636 S256 challenge (public so tests can pin the RFC vector).</summary>
    public static string CreateCodeChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>Builds the exact consent URL the browser should open.</summary>
    public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge)
    {
        var builder = new StringBuilder(_options.AuthorizationEndpoint);
        builder.Append(_options.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        builder.Append("client_id=").Append(Uri.EscapeDataString(_options.ClientId));
        builder.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        builder.Append("&response_type=code");
        builder.Append("&scope=").Append(Uri.EscapeDataString(_options.Scopes));
        builder.Append("&state=").Append(Uri.EscapeDataString(state));
        builder.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        builder.Append("&code_challenge_method=S256");
        builder.Append("&access_type=offline");
        builder.Append("&prompt=consent");
        builder.Append("&include_granted_scopes=true");
        return builder.ToString();
    }

    /// <summary>
    /// Runs the full interactive flow: opens the browser, waits on a loopback
    /// socket for the redirect, then exchanges the code. Set
    /// <paramref name="openBrowser"/> to control how the URL is launched.
    /// </summary>
    public async Task<GoogleTokenSet> AuthorizeAsync(
        Func<string, Task>? openBrowser = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Google sign-in is not configured. Create an OAuth client (type: Desktop app) in Google Cloud Console and paste its client ID first.");
        }

        string state = RandomToken(24);
        string verifier = RandomToken(48);
        string challenge = CreateCodeChallenge(verifier);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
            string authorizationUrl = BuildAuthorizationUrl(redirectUri, state, challenge);

            if (openBrowser is not null)
                await openBrowser(authorizationUrl).ConfigureAwait(false);
            else
                OpenBrowser(authorizationUrl);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30d, _options.AuthorizationTimeoutSeconds)));

            string code = await WaitForAuthorizationCodeAsync(listener, state, timeout.Token).ConfigureAwait(false);
            return await ExchangeCodeAsync(code, redirectUri, verifier, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // Already closed.
            }
        }
    }

    /// <summary>Exchanges a refresh token for a fresh access token.</summary>
    public async Task<GoogleTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A refresh token is required.", nameof(refreshToken));

        var form = new Dictionary<string, string>(5)
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret!;

        GoogleTokenSet refreshed = await PostTokenRequestAsync(form, cancellationToken).ConfigureAwait(false);

        // Google only returns a refresh token on the first consent; keep the old one.
        return refreshed with { RefreshToken = refreshed.RefreshToken ?? refreshToken };
    }

    /// <summary>Revokes the grant so the account can be unlinked cleanly.</summary>
    public async Task<bool> RevokeAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>(1)
            {
                ["token"] = token!
            });

            using HttpResponseMessage response = await _http
                .PostAsync(_options.RevokeEndpoint, content, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<GoogleTokenSet> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string verifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(6)
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        };

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
            form["client_secret"] = _options.ClientSecret!;

        GoogleTokenSet tokens = await PostTokenRequestAsync(form, cancellationToken).ConfigureAwait(false);
        return await WithUserInfoAsync(tokens, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleTokenSet> PostTokenRequestAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await _http
            .PostAsync(_options.TokenEndpoint, content, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Google token request failed ({(int)response.StatusCode}): {Summarize(body)}"));
        }

        using JsonDocument document = ParseJson(body, "token");
        JsonElement root = document.RootElement;

        string accessToken = ReadString(root, "access_token")
            ?? throw new InvalidOperationException("Google token response did not contain an access token.");

        int expiresIn = ReadInt(root, "expires_in") ?? 3600;

        return new GoogleTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = ReadString(root, "refresh_token"),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
            Scope = ReadString(root, "scope"),
            TokenType = ReadString(root, "token_type") ?? "Bearer",
            ObtainedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task<GoogleTokenSet> WithUserInfoAsync(GoogleTokenSet tokens, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return tokens;

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = ParseJson(body, "profile");
            JsonElement root = document.RootElement;

            return tokens with
            {
                Email = ReadString(root, "email"),
                DisplayName = ReadString(root, "name")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Profile enrichment is cosmetic; the grant itself already succeeded.
            return tokens;
        }
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Google sign-in timed out or was cancelled before the browser redirect arrived.");
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Loopback sign-in listener failed: {ex.Message}", ex);
            }

            using (client)
            {
                client.ReceiveTimeout = 5000;

                using NetworkStream stream = client.GetStream();
                string requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);

                // Browsers open speculative connections and request /favicon.ico;
                // ignore anything that is not our callback path.
                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                // Consume the remaining headers. Closing a socket that still holds
                // unread bytes makes Windows send a TCP reset, which would destroy
                // the response page in front of the user.
                await DrainRequestHeadersAsync(stream, cancellationToken).ConfigureAwait(false);

                string target = parts[1];
                int queryStart = target.IndexOf('?', StringComparison.Ordinal);
                string path = queryStart < 0 ? target : target[..queryStart];
                string query = queryStart < 0 ? string.Empty : target[(queryStart + 1)..];

                if (!path.Equals(CallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 404, "Not found",
                        "This loopback listener only serves the Google sign-in callback.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                Dictionary<string, string> parameters = ParseQuery(query);

                if (parameters.TryGetValue("error", out string? error) && !string.IsNullOrWhiteSpace(error))
                {
                    await WriteResponseAsync(stream, 400, "Sign-in cancelled",
                        "Google reported: " + WebUtility.HtmlEncode(error), cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException($"Google sign-in was not completed: {error}");
                }

                parameters.TryGetValue("state", out string? state);
                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 400, "Sign-in rejected",
                        "The OAuth state value did not match this request.", cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("Google sign-in failed state validation; start the link again.");
                }

                if (!parameters.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteResponseAsync(stream, 400, "Sign-in failed",
                        "Google did not return an authorization code.", cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("Google did not return an authorization code.");
                }

                await WriteResponseAsync(stream, 200, "BreadboardAOI linked",
                    "Google account connected. You can close this tab and return to the workstation.",
                    cancellationToken).ConfigureAwait(false);

                return code;
            }
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder(256);

        while (builder.Length < 2048)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            char current = (char)buffer[0];
            if (current == '\n')
                break;

            if (current != '\r')
                builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>Reads until the blank line that ends the request headers.</summary>
    private static async Task DrainRequestHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var seen = new StringBuilder(1024);
        int consumed = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            while (consumed < 8192)
            {
                int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                consumed += read;
                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));

                string text = seen.ToString();
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal)
                    || text.Contains("\n\n", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // No more header data arrived; respond anyway.
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length == 0)
            return result;

        string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pairs.Length; i++)
        {
            int equals = pairs[i].IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;

            string key = Uri.UnescapeDataString(pairs[i][..equals].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pairs[i][(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            _ => "Not Found"
        };

        string html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title></head>" +
            "<body style=\"margin:0;background:#141414;color:#e6e4df;font:13px 'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;height:100vh\">" +
            "<div style=\"border:1px solid #333337;background:#1e1e1e;padding:28px 32px;max-width:460px\">" +
            "<div style=\"width:4px;height:16px;background:#007acc;display:inline-block;vertical-align:-3px;margin-right:10px\"></div>" +
            "<strong style=\"font-size:12px;letter-spacing:.08em;text-transform:uppercase\">" + WebUtility.HtmlEncode(title) + "</strong>" +
            "<p style=\"color:#9d9d9d;line-height:1.6;margin:14px 0 0\">" + message + "</p>" +
            "</div></body></html>";

        byte[] body = Encoding.UTF8.GetBytes(html);
        string headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        byte[] head = Encoding.ASCII.GetBytes(headers);

        try
        {
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The browser may have closed the tab mid-write; the flow still completes.
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "Could not open the default browser. Copy the consent URL from the status line and open it manually.", ex);
        }
    }

    private static string RandomToken(int bytes)
    {
        Span<byte> buffer = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Base64Url(buffer);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonDocument ParseJson(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Google {what} response was not valid JSON.", ex);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out int value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) => value,
            _ => null
        };
    }

    private static string Summarize(string body)
    {
        string collapsed = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return collapsed.Length <= 240 ? collapsed : collapsed[..240] + "...";
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
