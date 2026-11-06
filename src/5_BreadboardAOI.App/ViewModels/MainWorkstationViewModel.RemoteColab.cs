// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.RemoteColab.cs
// PURPOSE: Partial class for Google OAuth, Google Drive Colab bridge,
//          Pair Code tunnel handshakes, and local AI provider links.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.Intelligence.Remote;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    // ---- remote AI / colab commands ---------------------------------------

    /// <summary>Public GitHub-hosted notebook the user opens in their own Colab.</summary>
    public string NotebookUrl =>
        "https://colab.research.google.com/github/wallali77666-cyber/BreadBoard-Assistant/blob/main/assets/colab/BreadboardAOI_Qwen.ipynb";

    [RelayCommand]
    private async Task LinkGoogleAsync()
    {
        if (IsRemoteBusy)
            return;

        string clientId = GoogleClientId.Trim();
        if (clientId.Length == 0)
            clientId = GoogleOAuthOptions.FromEnvironment().ClientId;

        if (clientId.Length == 0)
        {
            RemoteAiStatus = "Paste the OAuth client ID (Desktop app) from Google Cloud Console, then press LINK GOOGLE again.";
            return;
        }

        IsRemoteBusy = true;
        try
        {
            string? secret = NullIfBlank(GoogleClientSecret);
            _clientStore.Save(new GoogleClientConfig(clientId, secret));

            _oauth?.Dispose();
            _oauth = new GoogleOAuthClient(
                GoogleOAuthOptions.FromEnvironment().WithClient(clientId, secret));

            RemoteAiStatus = "Opening the Google consent page in your browser...";

            GoogleTokenSet tokens = await _oauth.AuthorizeAsync(url =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    RemoteAiStatus = "Open this URL manually: " + url;
                }

                return Task.CompletedTask;
            });

            _googleTokens = tokens;
            _tokenStore.Save(tokens);
            GoogleAccountStatus = "LINKED · " + tokens.DescribeAccount();
            RemoteAiStatus = $"Signed in as {tokens.DescribeAccount()}. Press PUBLISH SESSION to hand a key to Colab.";
            _remoteTimer.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Link failed: " + ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlinkGoogleAsync()
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            GoogleTokenSet? tokens = _googleTokens;
            GoogleOAuthClient? oauth = _oauth;

            if (tokens is not null && oauth is not null)
                await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Revocation is best-effort; the local token is cleared regardless.
        }
        finally
        {
            IsRemoteBusy = false;
        }

        _googleTokens = null;
        _colabSession = null;
        _appliedRemoteTarget = null;
        _tokenStore.Clear();
        _remoteTimer.Stop();
        UseColabAi = false;
        _coordinator?.SetRemoteTutor(null);
        SelectedAiRoute = AiRouteMethod.LocalQwen;
        GoogleAccountStatus = "NOT LINKED";
        RemoteAiStatus = "Google account unlinked. Local Qwen remains active.";
    }

    [RelayCommand]
    private async Task PublishColabSessionAsync()
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            string? accessToken = await EnsureAccessTokenAsync(CancellationToken.None);
            if (accessToken is null)
            {
                RemoteAiStatus = "Link a Google account first; the session key is published through Drive.";
                return;
            }

            ColabSession session = await _drive.RequestSessionAsync(accessToken, CreateSessionKey(), NotebookUrl);
            _colabSession = session;
            UseColabAi = true;
            _remoteTimer.Start();
            RemoteAiStatus = "Session key published to Drive. Open the Colab notebook in the same account and press Run all.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Publish failed: " + ex.Message;
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    [RelayCommand]
    private void OpenColabNotebook()
    {
        try
        {
            Process.Start(new ProcessStartInfo(NotebookUrl) { UseShellExecute = true })?.Dispose();
            RemoteAiStatus = "Opened the BreadboardAOI Colab notebook in your browser.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RemoteAiStatus = "Could not open the browser. Notebook URL: " + NotebookUrl;
        }
    }

    [RelayCommand]
    private async Task RefreshColabAsync()
    {
        await PollColabAsync(force: true);
    }

    [RelayCommand]
    private async Task ApplyManualEndpointAsync()
    {
        SelectedAiRoute = AiRouteMethod.ManualEndpoint;

        string? endpoint = ColabSession.NormalizeBaseUrl(ManualEndpoint);
        if (endpoint is null)
        {
            RemoteAiStatus = "Enter a tunnel URL such as https://random-words.trycloudflare.com or a local server such as http://127.0.0.1:1234.";
            return;
        }

        if (!ColabEndpoints.IsSecureEndpoint(endpoint))
        {
            RemoteAiStatus =
                "Refused: a remote tutor URL must use https so frames are not sent in cleartext. " +
                "Plain http is only accepted for 127.0.0.1 / localhost.";
            return;
        }

        if (!ColabEndpoints.IsPublicHost(endpoint))
        {
            RemoteAiStatus =
                "Refused: that URL points at a private or link-local address. Use the public https tunnel " +
                "printed by Colab, or 127.0.0.1 for a provider on this PC.";
            return;
        }

        string? key = NullIfBlank(ManualApiKey);

        // Model discovery happens inside ConnectResolvedAsync for every loopback
        // target, so the preset buttons, pair codes and this URL share one path.
        await ConnectEndpointAsync(
            new RemoteTutorTarget(endpoint, key, null),
            persist: true,
            confirmCustomHost: true).ConfigureAwait(true);
    }

    /// <summary>Connects from the single line printed by the Colab notebook.</summary>
    [RelayCommand]
    private async Task ConnectPairCodeAsync()
    {
        if (IsRemoteBusy)
            return;

        SelectedAiRoute = AiRouteMethod.ColabTunnel;

        if (!ColabPairCode.TryParse(PairCode, out RemoteTutorTarget target, out string? rejection))
        {
            RemoteAiStatus = rejection
                ?? "Paste the whole line printed by Colab: BBAOI1|https://....trycloudflare.com|key";
            return;
        }

        await ConnectEndpointAsync(target, persist: true, confirmCustomHost: true).ConfigureAwait(true);
    }

    /// <summary>Returns the cognitive loop to llama-server on this PC.</summary>
    [RelayCommand]
    private void UseLocalTutor()
    {
        _savedEndpoint = null;
        _appliedRemoteTarget = null;
        _endpointStore.Clear();
        _remoteTimer.Stop();
        _coordinator?.SetRemoteTutor(null);

        UseColabAi = false;
        SelectedAiRoute = AiRouteMethod.LocalQwen;
        RemoteRouteLabel = "TUTOR ROUTE: LOCAL";
        RemoteAiStatus = "Local Qwen on this PC is active. Remote routing is off.";
    }

    /// <summary>Drops the pair code and returns the cognitive loop to this PC.</summary>
    [RelayCommand]
    private void DisconnectPairCode()
    {
        PairCode = string.Empty;
        UseLocalTutor();
        RemoteAiStatus = "Pair code cleared. Local Qwen is active again.";
    }

    /// <summary>Probes a target, then swaps the tutor and remembers it for the next start.</summary>
    private async Task ConnectEndpointAsync(RemoteTutorTarget target, bool persist, bool confirmCustomHost = false)
    {
        if (IsRemoteBusy)
            return;

        IsRemoteBusy = true;
        try
        {
            await ConnectResolvedAsync(target, persist, providerLabel: null, confirmCustomHost).ConfigureAwait(true);
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    /// <summary>
    /// Shared connect path. The caller owns <see cref="IsRemoteBusy"/> so a preset
    /// probe can reuse it without tripping the re-entrancy guard.
    /// </summary>
    private async Task ConnectResolvedAsync(
        RemoteTutorTarget target,
        bool persist,
        string? providerLabel,
        bool confirmCustomHost = false,
        bool force = false)
    {
        if (!ColabEndpoints.IsSecureEndpoint(target.EndpointUrl))
        {
            RemoteAiStatus =
                $"Refused {target.DisplayHost}: the tutor endpoint must use https (only loopback may use http).";
            return;
        }

        if (!ColabEndpoints.IsPublicHost(target.EndpointUrl))
        {
            RemoteAiStatus =
                $"Refused {target.DisplayHost}: the endpoint resolves to a private or link-local network.";
            return;
        }

        if (confirmCustomHost
            && !target.IsLoopback
            && !ColabEndpoints.IsKnownTunnelHost(target.EndpointUrl))
        {
            MessageBoxResult answer = MessageBox.Show(
                $"This will send camera crops and DRC reports to:\n\n{target.DisplayHost}\n\n" +
                "Only continue if you recognise and trust this domain.",
                "Custom tutor host",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                RemoteAiStatus = "Connection cancelled: the custom tutor host was not confirmed.";
                return;
            }
        }

        RemoteAiStatus = providerLabel is null
            ? $"Probing {target.DisplayHost}..."
            : $"Probing {providerLabel} on {target.DisplayHost}...";

        try
        {
            // A loopback server names its own models. Resolve the alias here so a
            // preset, a pair code and a typed URL all send a model the server has.
            if (target.IsLoopback)
            {
                IReadOnlyList<string> models = await LocalAiProviders
                    .ListModelsAsync(_probeHttp, target.EndpointUrl, target.ApiKey)
                    .ConfigureAwait(true);

                if (models.Count == 0)
                {
                    RemoteAiStatus =
                        $"No model answered on {target.EndpointUrl}. Start the provider, load a model, then connect again. Nothing was installed by this app.";
                    LocalProviderStatus = "LOCAL AI: NOT ANSWERING";
                    return;
                }

                string? resolved = LocalAiProviders.ChooseModel(models, target.ModelAlias);
                target = target with { ModelAlias = resolved };
                LocalProviderStatus = $"LOCAL AI: {target.DisplayHost} \u00b7 {resolved}";
            }

            RemoteProbeResult probe = await RemoteEndpointProbe
                .ProbeAsync(_probeHttp, target.EndpointUrl, target.ApiKey)
                .ConfigureAwait(true);

            if (!probe.IsHealthy)
            {
                RemoteAiStatus =
                    $"{providerLabel ?? "The tutor"} is not answering yet ({probe.Detail}). Start it, then connect again.";
                return;
            }

            // UseColabAi means "prefer the Colab tunnel"; a loopback provider must
            // not arm the Drive poller or it would replace the local model later.
            if (target.IsLoopback)
            {
                if (UseColabAi)
                    UseColabAi = false;

                ApplyRemoteTarget(target, force: force || target.IsLoopback);
            }
            else
            {
                ApplyRemoteTarget(target, force: force);

                if (!UseColabAi)
                    UseColabAi = true;
            }

            if (persist)
            {
                _savedEndpoint = target;
                _endpointStore.Save(target);
                PairCode = ColabPairCode.Format(target);
            }

            RemoteAiStatus = providerLabel is null
                ? $"Connected to {target.DisplayHost} ({probe.LatencyMs:0} ms). Diagnoses stream from {(target.IsLoopback ? "the local model" : "Colab")}.{CustomHostNote(target)}"
                : $"{providerLabel} connected on {target.DisplayHost} ({probe.LatencyMs:0} ms){ModelSuffix(target)}.{CustomHostNote(target)}";

            await RefreshTutorModelsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException sockEx } &&
                sockEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                RemoteAiStatus = $"Cannot connect to {(providerLabel ?? target.DisplayHost)} at {target.EndpointUrl}: connection refused. Please ensure {(providerLabel ?? "the server")} is running with Server Status ON (green toggle) and the model has finished loading.";
            }
            else
            {
                RemoteAiStatus = "Connect failed: " + ex.Message;
            }
        }
    }

    private static string ModelSuffix(RemoteTutorTarget target) =>
        string.IsNullOrWhiteSpace(target.ModelAlias) ? string.Empty : " · " + target.ModelAlias;

    /// <summary>
    /// A custom public domain is accepted because the operator pasted it, but the
    /// status line says so: only tunnel providers and loopback are recognised.
    /// </summary>
    private static string CustomHostNote(RemoteTutorTarget target) =>
        target.IsLoopback || ColabEndpoints.IsKnownTunnelHost(target.EndpointUrl)
            ? string.Empty
            : " Custom host: confirm this domain is yours before sending frames.";

    /// <summary>
    /// Probes LM Studio / Ollama that the operator already runs on this PC.
    /// Nothing is installed or launched: an offline provider just leaves the
    /// local llama.cpp / rule-based tutor in charge.
    /// </summary>
    [RelayCommand]
    private async Task ConnectLocalProviderAsync(string? presetId)
    {
        LocalAiPreset? preset = LocalAiProviders.Find(presetId);
        if (preset is null)
        {
            LocalProviderStatus = "LOCAL AI: UNKNOWN PROVIDER";
            return;
        }

        if (IsRemoteBusy)
            return;

        SelectedAiRoute = AiRouteMethod.LocalProvider;
        IsRemoteBusy = true;
        LocalProviderStatus = $"LOCAL AI: PROBING {preset.BaseUrl} ...";

        try
        {
            // ConnectResolvedAsync discovers the loaded model and reports a clear
            // "not answering" status when the provider is not running.
            await ConnectResolvedAsync(
                new RemoteTutorTarget(preset.BaseUrl, preset.ApiKey, null),
                persist: false,
                providerLabel: preset.Label,
                force: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException sockEx } &&
                sockEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                LocalProviderStatus = $"LOCAL AI: {preset.Label} NOT RUNNING";
                RemoteAiStatus = $"Cannot connect to {preset.Label} at {preset.BaseUrl}: connection refused. Please ensure {preset.Label} is running with Server Status ON (green toggle) and the model has finished loading.";
            }
            else
            {
                LocalProviderStatus = $"LOCAL AI: {preset.Label} PROBE FAILED";
                RemoteAiStatus = "Local provider probe failed: " + ex.Message;
            }
        }
        finally
        {
            IsRemoteBusy = false;
        }
    }

    // ---- remote AI helpers -------------------------------------------------

    private void LoadGoogleSettings()
    {
        GoogleClientConfig? config = _clientStore.Load();
        GoogleOAuthOptions environment = GoogleOAuthOptions.FromEnvironment();

        GoogleClientId = !string.IsNullOrWhiteSpace(config?.ClientId) ? config!.ClientId : environment.ClientId;
        GoogleClientSecret = NullIfBlank(config?.ClientSecret) ?? NullIfBlank(environment.ClientSecret) ?? string.Empty;

        _googleTokens = _tokenStore.Load();
        if (_googleTokens is not null)
        {
            GoogleAccountStatus = "LINKED · " + _googleTokens.DescribeAccount();
            RemoteAiStatus = "Cached Google grant loaded. Press PUBLISH SESSION or refresh the Colab endpoint.";
        }

        string? manual = Environment.GetEnvironmentVariable("BREADBOARD_COLAB_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(manual))
        {
            ManualEndpoint = manual.Trim();
            ManualApiKey = Environment.GetEnvironmentVariable("BREADBOARD_COLAB_KEY") ?? string.Empty;
        }

        RemoteTutorTarget? saved = _endpointStore.Load();
        if (saved is not null)
        {
            _savedEndpoint = saved;
            PairCode = ColabPairCode.Format(saved);
            RemoteAiStatus = "Saved Colab endpoint found. Probing it now...";
        }
    }

    private async Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        GoogleTokenSet? tokens = _googleTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return null;

        if (!tokens.IsExpired(DateTimeOffset.UtcNow))
            return tokens.AccessToken;

        if (!tokens.HasRefreshToken)
            return null;

        _oauth ??= new GoogleOAuthClient(
            GoogleOAuthOptions.FromEnvironment().WithClient(GoogleClientId, NullIfBlank(GoogleClientSecret)));

        GoogleTokenSet refreshed = await _oauth.RefreshAsync(tokens.RefreshToken!, cancellationToken);
        _googleTokens = refreshed;
        _tokenStore.Save(refreshed);
        return refreshed.AccessToken;
    }

    /// <summary>Reads the shared Drive session, probes the tunnel and swaps the tutor if healthy.</summary>
    private async Task PollColabAsync(bool force)
    {
        if (Interlocked.CompareExchange(ref _remotePolling, 1, 0) != 0)
            return;

        try
        {
            if (!UseColabAi && !force)
                return;

            if (_googleTokens is null)
            {
                if (force)
                    RemoteAiStatus = "No Google account linked. Press LINK GOOGLE first.";
                return;
            }

            string? accessToken = await EnsureAccessTokenAsync(CancellationToken.None);
            if (accessToken is null)
            {
                RemoteAiStatus = "Google session expired. Link the account again.";
                return;
            }

            ColabSession? session = await _drive.ReadSessionAsync(accessToken);
            _colabSession = session;

            if (session is null)
            {
                RemoteAiStatus = "No Colab session file in Drive yet. Press PUBLISH SESSION.";
                return;
            }

            if (!session.IsServing)
            {
                RemoteAiStatus = string.IsNullOrWhiteSpace(session.Detail)
                    ? $"Colab session is '{session.Status}'."
                    : $"Colab session is '{session.Status}': {session.Detail}";
                return;
            }

            RemoteProbeResult probe = await RemoteEndpointProbe.ProbeAsync(
                _probeHttp,
                session.EndpointUrl,
                session.ApiKey);

            if (!probe.IsHealthy)
            {
                RemoteAiStatus = $"Colab published a tunnel but it is not answering: {probe.Detail}.";
                return;
            }

            RemoteTutorTarget? target = RemoteTutorTarget.FromSession(session);
            if (target is null)
            {
                RemoteAiStatus = "Colab session file has no usable chat endpoint.";
                return;
            }

            if (!ColabEndpoints.IsSecureEndpoint(target.EndpointUrl))
            {
                RemoteAiStatus =
                    $"Refused the Drive session: {target.DisplayHost} is plain http. Re-run the notebook so it publishes an https tunnel.";
                return;
            }

            ApplyRemoteTarget(target);
            SelectedAiRoute = AiRouteMethod.ColabTunnel;
            PairCode = ColabPairCode.Format(target);
            RemoteAiStatus =
                $"Serving {session.Model ?? "Qwen"} on {target.DisplayHost} · {probe.LatencyMs:0} ms{(string.IsNullOrWhiteSpace(session.Gpu) ? string.Empty : " · " + session.Gpu)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RemoteAiStatus = "Colab refresh failed: " + ex.Message;
        }
        finally
        {
            Volatile.Write(ref _remotePolling, 0);
        }
    }

    private void ApplyRemoteTarget(RemoteTutorTarget target, bool force = false)
    {
        if (!force
            && _appliedRemoteTarget is not null
            && string.Equals(_appliedRemoteTarget.EndpointUrl, target.EndpointUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_appliedRemoteTarget.ApiKey, target.ApiKey, StringComparison.Ordinal)
            && string.Equals(_appliedRemoteTarget.ModelAlias, target.ModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appliedRemoteTarget = target;
        _coordinator?.SetRemoteTutor(target, force);
        RemoteRouteLabel = DescribeRoute(target);
        _ = RefreshQwenConnectionAsync();
    }

    /// <summary>Loopback providers are labelled LOCAL AI so nobody looks for a Colab tunnel.</summary>
    private static string DescribeRoute(RemoteTutorTarget target) =>
        target.IsLoopback
            ? $"TUTOR ROUTE: LOCAL AI \u00b7 {target.DisplayHost}"
            : $"TUTOR ROUTE: COLAB \u00b7 {target.DisplayHost}";

    private static string CreateSessionKey()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "bbaoi_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}