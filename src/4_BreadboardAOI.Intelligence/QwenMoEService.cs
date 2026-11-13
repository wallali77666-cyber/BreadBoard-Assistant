// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/QwenMoEService.cs
// PURPOSE: Minimal vision-tutor client. One request = one fixed instruction +
//          one raw PNG frame. No crops, no DRC text, no GBNF grammar, no
//          reasoning channel and no rule-based fallback: the model sees exactly
//          what a chat client would see.
//
// RUNTIME OVERRIDES (any PC):
//   BREADBOARD_QWEN_ENDPOINT, _MODEL, _MMPROJ, _SERVER, _PORT, _CTX,
//   _GPU_LAYERS, _CPU_MOE, _THREADS, _THREADS_BATCH, _AUTO_LAUNCH, _MLOCK,
//   _FLASH_ATTN, _STARTUP_TIMEOUT, _REQUEST_TIMEOUT, _MAX_TOKENS,
//   _TEMPERATURE, _MODEL_ALIAS, _API_KEY
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence.Remote;

namespace BreadboardAOI.Intelligence;

/// <summary>Configuration for the vision tutor. Use <see cref="FromEnvironment"/> for per-PC overrides.</summary>
public sealed class QwenMoEOptions
{
    public const string EnvironmentPrefix = "BREADBOARD_QWEN_";
    public const int DefaultPort = 8089;
    public const int DefaultMaxTokens = 1024;
    public const double DefaultTemperature = 0.7d;

    public const string DefaultModelFile = "Qwen3-VL-30B-A3B-Instruct-Q4_K_M.gguf";
    public const string DefaultMmprojFile = "mmproj-Qwen3-VL-30B-f16.gguf";

    public string EndpointUrl { get; init; } = $"http://127.0.0.1:{DefaultPort}/v1/chat/completions";

    public string ServerExecutablePath { get; init; } = Path.Combine("tools", "llama-server.exe");

    public string ModelPath { get; init; } = Path.Combine("assets", "models", DefaultModelFile);

    public string MmprojPath { get; init; } = Path.Combine("assets", "models", DefaultMmprojFile);

    public int Port { get; init; } = DefaultPort;

    public int ContextSize { get; init; } = 4096;

    public int GpuLayers { get; init; } = 99;

    /// <summary>Experts kept in DDR4. Set to null/0 for dense models that do not support --n-cpu-moe.</summary>
    public int? CpuMoeLayers { get; init; } = 22;

    public int Threads { get; init; } = DeriveThreads(Environment.ProcessorCount);

    public int ThreadsBatch { get; init; } = DeriveThreadsBatch(Environment.ProcessorCount);

    public bool AutoLaunchServer { get; init; } = true;

    public bool Mlock { get; init; } = true;

    public bool FlashAttention { get; init; } = true;

    public double ServerStartupTimeoutSeconds { get; init; } = 120d;

    public double RequestTimeoutSeconds { get; init; } = 120d;

    /// <summary>Answer budget for one tutor turn. Five sentences need far fewer.</summary>
    public int MaxTokens { get; init; } = DefaultMaxTokens;

    /// <summary>Stock chat sampling so the app and a chat client behave the same.</summary>
    public double Temperature { get; init; } = DefaultTemperature;

    public string ModelAlias { get; init; } = "qwen3-vl-30b-a3b";

    public string? ApiKey { get; init; }

    /// <summary>20 logical processors -> 12 batch threads, 6 decode threads (the locked workstation spec).</summary>
    public static int DeriveThreadsBatch(int logicalProcessors) => Math.Clamp(logicalProcessors, 1, 12);

    /// <summary>Half of <see cref="DeriveThreadsBatch"/>, never below one thread.</summary>
    public static int DeriveThreads(int logicalProcessors) => Math.Max(1, DeriveThreadsBatch(logicalProcessors) / 2);

    public static QwenMoEOptions FromEnvironment()
    {
        int port = ReadInt("PORT") ?? DefaultPort;
        int processors = Environment.ProcessorCount;

        return new QwenMoEOptions
        {
            EndpointUrl = ReadString("ENDPOINT") ?? $"http://127.0.0.1:{port}/v1/chat/completions",
            ServerExecutablePath = ReadString("SERVER") ?? Path.Combine("tools", "llama-server.exe"),
            ModelPath = ReadString("MODEL") ?? Path.Combine("assets", "models", DefaultModelFile),
            MmprojPath = ReadString("MMPROJ") ?? Path.Combine("assets", "models", DefaultMmprojFile),
            Port = port,
            ContextSize = ReadInt("CTX") ?? 4096,
            GpuLayers = ReadInt("GPU_LAYERS") ?? 99,
            CpuMoeLayers = ReadOptionalInt("CPU_MOE", 22),
            Threads = ReadInt("THREADS") ?? DeriveThreads(processors),
            ThreadsBatch = ReadInt("THREADS_BATCH") ?? DeriveThreadsBatch(processors),
            AutoLaunchServer = ReadBool("AUTO_LAUNCH") ?? true,
            Mlock = ReadBool("MLOCK") ?? true,
            FlashAttention = ReadBool("FLASH_ATTN") ?? true,
            ServerStartupTimeoutSeconds = ReadDouble("STARTUP_TIMEOUT") ?? 120d,
            RequestTimeoutSeconds = ReadDouble("REQUEST_TIMEOUT") ?? 120d,
            MaxTokens = ReadInt("MAX_TOKENS") ?? DefaultMaxTokens,
            Temperature = ReadDouble("TEMPERATURE") ?? DefaultTemperature,
            ModelAlias = ReadString("MODEL_ALIAS") ?? "qwen3-vl-30b-a3b",
            ApiKey = ReadString("API_KEY")
        };
    }

    /// <summary>
    /// Copy pointed at a remote OpenAI-compatible tunnel (Colab + cloudflared).
    /// Local auto-launch is disabled because the weights live on the runtime.
    /// </summary>
    public QwenMoEOptions WithRemoteEndpoint(string chatEndpoint, string? apiKey, string? modelAlias) => new()
    {
        EndpointUrl = chatEndpoint,
        ServerExecutablePath = ServerExecutablePath,
        ModelPath = ModelPath,
        MmprojPath = MmprojPath,
        Port = Port,
        ContextSize = ContextSize,
        GpuLayers = GpuLayers,
        CpuMoeLayers = CpuMoeLayers,
        Threads = Threads,
        ThreadsBatch = ThreadsBatch,
        AutoLaunchServer = false,
        Mlock = Mlock,
        FlashAttention = FlashAttention,
        ServerStartupTimeoutSeconds = ServerStartupTimeoutSeconds,
        RequestTimeoutSeconds = Math.Max(60d, RequestTimeoutSeconds),
        MaxTokens = MaxTokens,
        Temperature = Temperature,
        ModelAlias = string.IsNullOrWhiteSpace(modelAlias) ? ModelAlias : modelAlias,
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? ApiKey : apiKey
    };

    /// <summary>Builds the exact llama-server.exe command line (no shell escaping).</summary>
    public IReadOnlyList<string> BuildServerArguments(int port, string modelPath, string mmprojPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is required.", nameof(modelPath));

        var arguments = new List<string>(24)
        {
            "--model", modelPath,
            "--n-gpu-layers", GpuLayers.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", Math.Max(512, ContextSize).ToString(CultureInfo.InvariantCulture),
            "--threads", Math.Max(1, Threads).ToString(CultureInfo.InvariantCulture),
            "--threads-batch", Math.Max(1, ThreadsBatch).ToString(CultureInfo.InvariantCulture),
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(mmprojPath))
        {
            arguments.Add("--mmproj");
            arguments.Add(mmprojPath);
        }

        if (CpuMoeLayers is > 0)
        {
            arguments.Add("--n-cpu-moe");
            arguments.Add(CpuMoeLayers.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (FlashAttention)
            arguments.Add("--flash-attn");

        arguments.Add("--cache-type-k");
        arguments.Add("q8_0");
        arguments.Add("--cache-type-v");
        arguments.Add("q8_0");

        if (Mlock)
            arguments.Add("--mlock");

        return arguments;
    }

    private static string? ReadString(string suffix)
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentPrefix + suffix);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
    }

    private static int? ReadInt(string suffix)
    {
        string? raw = ReadString(suffix);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static int? ReadOptionalInt(string suffix, int defaultValue)
    {
        string? raw = ReadString(suffix);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (raw.Equals("off", StringComparison.OrdinalIgnoreCase) || raw == "0")
            return null;

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : defaultValue;
    }

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

    private static double? ReadDouble(string suffix)
    {
        string? raw = ReadString(suffix);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : null;
    }
}

/// <summary>
/// Owns the llama-server child process (when used locally) and turns a raw frame
/// plus the fixed prompt into a streamed answer. Callers only pass bytes in and
/// read text out; every transport detail stays here.
/// </summary>
public sealed class QwenMoEService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly QwenMoEOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private readonly object _processSync = new();

    private Process? _serverProcess;
    private string? _launchError;
    private string? _lastServerLog;
    private bool _disposed;

    private string? _discoveredModelAlias;

    /// <summary>Operator-selected model; wins over auto-discovery for any OpenAI-compatible server.</summary>
    private volatile string? _modelOverride;

    public QwenMoEService(QwenMoEOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? QwenMoEOptions.FromEnvironment();
        BaseDirectory = AppContext.BaseDirectory;

        ModelPath = LocalPathResolver.Resolve(_options.ModelPath, BaseDirectory);
        MmprojPath = LocalPathResolver.Resolve(_options.MmprojPath, BaseDirectory);
        ServerExecutablePath = LocalPathResolver.Resolve(_options.ServerExecutablePath, BaseDirectory);

        HttpMessageHandler messageHandler = handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _httpClient = new HttpClient(messageHandler, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1d, _options.RequestTimeoutSeconds))
        };
    }

    public QwenMoEOptions Options => _options;

    public string BaseDirectory { get; }

    public string ModelPath { get; }

    public string MmprojPath { get; }

    public string ServerExecutablePath { get; }

    public bool HasLocalModel => File.Exists(ModelPath);

    public bool HasVisionProjector => File.Exists(MmprojPath);

    /// <summary>True when this process launched llama-server.exe and must terminate it on dispose.</summary>
    public bool IsServerManaged { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>True after the tutor answered, or after a successful reachability probe.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>True only when the most recent answer came from the server with usable content.</summary>
    public bool LastAnswerUsable { get; private set; }

    /// <summary>Short provider label derived from the active endpoint (LM STUDIO, OLLAMA, ...).</summary>
    public string EndpointProvider
    {
        get
        {
            if (!Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out Uri? endpoint))
                return "VISION AI";

            return endpoint.Port switch
            {
                1234 => "LM STUDIO",
                11434 => "OLLAMA",
                var port when port == QwenMoEOptions.DefaultPort || port == _options.Port => "LOCAL QWEN",
                _ => endpoint.IsLoopback ? "LOCAL AI" : "REMOTE QWEN"
            };
        }
    }

    /// <summary>Provider + loaded model alias, for the studio badge and status line.</summary>
    public string EndpointSummary => $"{EndpointProvider} / {ActiveModelAlias}";

    /// <summary>Model name currently sent on every chat request.</summary>
    public string ActiveModelAlias => _modelOverride ?? _discoveredModelAlias ?? _options.ModelAlias;

    /// <summary>
    /// Pins the chat payload to an exact model id. Pass null or blank to return
    /// to server-driven discovery.
    /// </summary>
    public void SetModelAlias(string? modelAlias)
    {
        _modelOverride = string.IsNullOrWhiteSpace(modelAlias) ? null : modelAlias.Trim();
        _discoveredModelAlias = _modelOverride;
    }

    /// <summary>Lists every model the active OpenAI-compatible server advertises.</summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(4));

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri());
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            string json = await response.Content.ReadAsStringAsync(probeTimeout.Token).ConfigureAwait(false);
            IReadOnlyList<string> models = LocalAiProviders.ParseModelIds(json);
            if (models.Count > 0 && _modelOverride is null)
                _discoveredModelAlias = LocalAiProviders.ChooseModel(models, _options.ModelAlias);

            return models;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Streams the tutor answer for a DRC snapshot. The only inputs are the fixed
    /// instruction and <see cref="DrcStatePacket.RawImagePngBytes"/>.
    /// </summary>
    public async IAsyncEnumerable<string> StreamDiagnosticAsync(
        DrcStatePacket state,
        string? prompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string effectivePrompt = prompt ?? DiagnosticPromptBuilder.Build(state);

        await foreach (string token in StreamTokensAsync(
                           effectivePrompt,
                           state.RawImagePngBytes,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return token;
        }
    }

    /// <summary>
    /// Streams a free-form inspection. The uploaded photo or live frame is the
    /// authoritative input; no DRC packet is required.
    /// </summary>
    public async IAsyncEnumerable<string> StreamImageAsync(
        byte[] pngBytes,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pngBytes.Length == 0)
        {
            LastAnswerUsable = false;
            yield return "[No image was supplied to the vision model. Upload a PNG or JPG and press ANALYZE AGAIN.]";
            yield break;
        }

        await foreach (string token in StreamTokensAsync(prompt, pngBytes, cancellationToken).ConfigureAwait(false))
            yield return token;
    }

    /// <summary>
    /// One server call, two inputs. On any failure the caller gets a single
    /// explicit offline message instead of fabricated advice.
    /// </summary>
    private async IAsyncEnumerable<string> StreamTokensAsync(
        string prompt,
        byte[]? imagePng,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastAnswerUsable = false;

        if (await TryEnsureServerAsync(cancellationToken).ConfigureAwait(false))
        {
            bool emittedAny = false;
            bool streamFailed = false;

            await using (IAsyncEnumerator<string> enumerator =
                StreamServerAsync(prompt, imagePng, _options.MaxTokens, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool moved;

                    // The try/catch deliberately contains no yield so the iterator stays legal.
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRecoverable(ex))
                    {
                        LastError = $"Vision server stream failed: {ex.Message}";
                        streamFailed = true;
                        break;
                    }

                    if (!moved)
                        break;

                    if (string.IsNullOrEmpty(enumerator.Current))
                        continue;

                    emittedAny = true;
                    yield return enumerator.Current;
                }
            }

            if (emittedAny)
            {
                IsConnected = true;
                LastAnswerUsable = true;
                LastError = null;
                yield break;
            }

            if (!streamFailed)
                LastError = "The vision server returned an empty answer.";
        }

        LastAnswerUsable = false;
        yield return BuildOfflineMessage();
    }

    private string BuildOfflineMessage()
    {
        string detail = string.IsNullOrWhiteSpace(LastError)
            ? "No OpenAI-compatible vision endpoint answered."
            : LastError!;

        string hint = EndpointProvider switch
        {
            "LM STUDIO" =>
                "Start LM Studio, load a vision-capable model, enable the local server on port 1234, and connect it under AI ROUTING > LOCAL AI > LM STUDIO.",
            "OLLAMA" =>
                "Start Ollama, pull a vision model, and connect it under AI ROUTING > LOCAL AI > OLLAMA.",
            "LOCAL QWEN" =>
                "Start llama-server, or connect LM Studio / Ollama under AI ROUTING > LOCAL AI.",
            _ =>
                "Connect a vision-capable OpenAI-compatible endpoint under AI ROUTING."
        };

        return $"[Vision model offline] {detail} {hint}";
    }

    private async Task<bool> TryEnsureServerAsync(CancellationToken cancellationToken)
    {
        if (await IsServerReachableAsync(cancellationToken).ConfigureAwait(false))
            return true;

        if (!_options.AutoLaunchServer)
        {
            LastError = $"{EndpointProvider} is not reachable at {_options.EndpointUrl} and auto-launch is disabled.";
            return false;
        }

        if (!HasLocalModel)
        {
            LastError = $"Vision model not found at '{ModelPath}'.";
            return false;
        }

        if (!File.Exists(ServerExecutablePath))
        {
            LastError = $"llama-server.exe not found at '{ServerExecutablePath}'.";
            return false;
        }

        await _serverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsServerReachableAsync(cancellationToken).ConfigureAwait(false))
                return true;

            if (_serverProcess is not { HasExited: false })
                StartServerProcess();

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1d, _options.ServerStartupTimeoutSeconds)));

            while (!startupTimeout.IsCancellationRequested)
            {
                if (await IsServerReachableAsync(startupTimeout.Token).ConfigureAwait(false))
                    return true;

                if (_serverProcess is { HasExited: true })
                    break;

                try
                {
                    await Task.Delay(500, startupTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _serverGate.Release();
        }

        LastError = _launchError
            ?? _lastServerLog
            ?? "llama-server did not become ready in time.";
        return false;
    }

    private void StartServerProcess()
    {
        IReadOnlyList<string> arguments = _options.BuildServerArguments(_options.Port, ModelPath, MmprojPath);
        string? workingDirectory = Path.GetDirectoryName(ServerExecutablePath);

        var startInfo = new ProcessStartInfo(ServerExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? BaseDirectory : workingDirectory
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _lastServerLog = eventArgs.Data;
        };

        try
        {
            if (!process.Start())
            {
                _launchError = "llama-server.exe could not be started.";
                process.Dispose();
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            TryPinServerProcess(process);

            lock (_processSync)
                _serverProcess = process;

            IsServerManaged = true;
            _launchError = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            _launchError = $"Failed to launch llama-server.exe: {ex.Message}";
            process.Dispose();
        }
    }

    private static void TryPinServerProcess(Process process)
    {
        CpuAffinityManager.TryPinProcess(process, AffinityRole.Ai, out _);
    }

    /// <summary>
    /// Non-blocking health probe used by the connection light. Checks
    /// <c>/health</c> first and then the OpenAI <c>/v1/models</c> surface.
    /// </summary>
    public async Task<bool> ProbeConnectionAsync(CancellationToken cancellationToken = default)
    {
        bool reachable = await IsServerReachableAsync(cancellationToken).ConfigureAwait(false);
        if (reachable)
            _ = await ResolveModelAliasAsync(cancellationToken).ConfigureAwait(false);

        IsConnected = reachable;
        return reachable;
    }

    private async Task<string> ResolveModelAliasAsync(CancellationToken cancellationToken)
    {
        // An operator-picked model is exact: never silently swap it for a
        // different model the server happens to advertise.
        if (_modelOverride is { Length: > 0 })
            return _modelOverride;

        try
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri());
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(probeTimeout.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                    && data.ValueKind == JsonValueKind.Array
                    && data.GetArrayLength() > 0)
                {
                    var loadedIds = new List<string>();
                    foreach (JsonElement entry in data.EnumerateArray())
                    {
                        if (entry.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                        {
                            string id = idEl.GetString()!;
                            if (!string.IsNullOrWhiteSpace(id))
                                loadedIds.Add(id);
                        }
                    }

                    if (loadedIds.Count > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(_options.ModelAlias))
                        {
                            foreach (string id in loadedIds)
                            {
                                if (string.Equals(id, _options.ModelAlias, StringComparison.OrdinalIgnoreCase))
                                {
                                    _discoveredModelAlias = id;
                                    return id;
                                }
                            }
                        }

                        foreach (string id in loadedIds)
                        {
                            if (id.Contains("30b", StringComparison.OrdinalIgnoreCase) || id.Contains("qwen3", StringComparison.OrdinalIgnoreCase))
                            {
                                _discoveredModelAlias = id;
                                return id;
                            }
                        }

                        foreach (string id in loadedIds)
                        {
                            if (id.Contains("qwen", StringComparison.OrdinalIgnoreCase) || id.Contains("vl", StringComparison.OrdinalIgnoreCase))
                            {
                                _discoveredModelAlias = id;
                                return id;
                            }
                        }

                        _discoveredModelAlias = loadedIds[0];
                        return loadedIds[0];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the configured default.
        }

        return !string.IsNullOrWhiteSpace(_discoveredModelAlias)
            ? _discoveredModelAlias
            : (!string.IsNullOrWhiteSpace(_options.ModelAlias) ? _options.ModelAlias : "default");
    }

    private async Task<bool> IsServerReachableAsync(CancellationToken cancellationToken)
    {
        if (IsLlamaCppEndpoint())
        {
            if (await ProbeAsync(BuildHealthUri(), cancellationToken).ConfigureAwait(false))
                return true;
        }

        return await ProbeAsync(BuildModelsUri(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       or TaskCanceledException
                                       or InvalidOperationException
                                       or UriFormatException)
        {
            return false;
        }
    }

    private Uri BuildHealthUri()
    {
        if (Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out Uri? endpoint))
            return BuildSiblingUri(endpoint, "/health");

        return new Uri($"http://127.0.0.1:{_options.Port}/health");
    }

    private Uri BuildModelsUri()
    {
        if (Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out Uri? endpoint))
            return BuildSiblingUri(endpoint, "/v1/models");

        return new Uri($"http://127.0.0.1:{_options.Port}/v1/models");
    }

    /// <summary>Keeps any proxy path prefix (for example /team/api/v1/chat/completions).</summary>
    private static Uri BuildSiblingUri(Uri endpoint, string suffix)
    {
        string path = endpoint.AbsolutePath;
        int v1Index = path.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
        string prefix = v1Index > 0 ? path[..v1Index] : string.Empty;

        return new UriBuilder(endpoint)
        {
            Path = prefix + suffix,
            Query = string.Empty
        }.Uri;
    }

    private bool IsLlamaCppEndpoint()
    {
        if (!Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out Uri? endpoint))
            return false;

        return endpoint.Port == QwenMoEOptions.DefaultPort || endpoint.Port == _options.Port;
    }

    /// <summary>
    /// Posts one chat completion with the prompt and the raw image, then relays
    /// content deltas. Sampling matches a stock chat client so the app and a
    /// desktop chat window see the same picture and the same instruction.
    /// </summary>
    private async IAsyncEnumerable<string> StreamServerAsync(
        string prompt,
        byte[]? imagePng,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string activeModel = await ResolveModelAliasAsync(cancellationToken).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(5)
        {
            ["model"] = activeModel,
            ["stream"] = true,
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = maxTokens,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = BuildUserContent(prompt, imagePng)
                }
            }
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                response.StatusCode);
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!TryExtractJsonResponse(body, out string? content) || string.IsNullOrWhiteSpace(content))
                throw new JsonException("The provider returned JSON without an assistant message.");

            yield return content;
            yield break;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        bool insideThinkTag = false;

        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            string data = line.AsSpan(5).Trim().ToString();
            if (data.Length == 0)
                continue;

            if (data == "[DONE]")
                yield break;

            if (!TryExtractStreamDelta(data, out string? content) || string.IsNullOrEmpty(content))
                continue;

            string text = content;

            // Reasoning models sometimes wrap their trace in <think ...> tags.
            // The tutor only speaks the final answer, so those blocks are dropped.
            while (text.Length > 0)
            {
                if (!insideThinkTag)
                {
                    int thinkStart = text.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
                    if (thinkStart >= 0)
                    {
                        int tagEnd = text.IndexOf('>', thinkStart);
                        if (tagEnd < 0)
                            break;

                        if (thinkStart > 0)
                            yield return text[..thinkStart];

                        insideThinkTag = true;
                        text = text[(tagEnd + 1)..];
                    }
                    else
                    {
                        yield return text;
                        break;
                    }
                }
                else
                {
                    int thinkEnd = text.IndexOf("</think", StringComparison.OrdinalIgnoreCase);
                    if (thinkEnd < 0)
                        break;

                    int tagEnd = text.IndexOf('>', thinkEnd);
                    if (tagEnd < 0)
                        break;

                    insideThinkTag = false;
                    text = text[(tagEnd + 1)..];
                }
            }
        }
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return body.Length <= 500 ? body : body[..500] + "\u2026";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return "(response body unavailable)";
        }
    }

    private static object BuildUserContent(string prompt, byte[]? imagePng)
    {
        if (imagePng is not { Length: > 0 })
            return prompt;

        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(imagePng);
        return new object[]
        {
            new { type = "text", text = prompt },
            new { type = "image_url", image_url = new { url = dataUrl } }
        };
    }

    private static bool TryExtractJsonResponse(string json, out string? content)
    {
        content = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement choice = choices[0];
            bool extracted = false;

            if (choice.TryGetProperty("message", out JsonElement message))
                extracted = TryExtractContent(message, out content);
            else if (choice.TryGetProperty("delta", out JsonElement delta))
                extracted = TryExtractContent(delta, out content);
            else if (choice.TryGetProperty("text", out JsonElement text)
                     && text.ValueKind == JsonValueKind.String)
            {
                content = text.GetString();
                extracted = content is not null;
            }

            content = StripThinkTags(content);
            return extracted;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractStreamDelta(string json, out string? content)
    {
        content = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                content = "[Vision provider error] " + ExtractErrorText(error);
                return true;
            }

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement choice = choices[0];
            if (choice.TryGetProperty("delta", out JsonElement delta))
                return TryExtractContent(delta, out content);

            if (choice.TryGetProperty("message", out JsonElement message))
                return TryExtractContent(message, out content);

            if (choice.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String)
            {
                content = text.GetString();
                return content is not null;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryExtractContent(JsonElement element, out string? content)
    {
        content = element.TryGetProperty("content", out JsonElement contentElement)
            ? ExtractTextContent(contentElement)
            : null;

        return !string.IsNullOrEmpty(content);
    }

    private static string? ExtractTextContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (JsonElement part in element.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
            }
            else if (part.ValueKind == JsonValueKind.Object
                     && part.TryGetProperty("text", out JsonElement text)
                     && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? StripThinkTags(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var builder = new StringBuilder(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            int start = text.IndexOf("<think", index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);

            int openEnd = text.IndexOf('>', start);
            if (openEnd < 0)
                break;

            int end = text.IndexOf("</think", openEnd, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                break;

            int closeEnd = text.IndexOf('>', end);
            if (closeEnd < 0)
                break;

            index = closeEnd + 1;
        }

        string result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static string ExtractErrorText(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "unknown error";

        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "unknown error";
        }

        return error.ToString();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is HttpRequestException
            or TaskCanceledException
            or IOException
            or JsonException
            or InvalidOperationException
            or UriFormatException;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
        _serverGate.Dispose();

        Process? process;
        lock (_processSync)
        {
            process = _serverProcess;
            _serverProcess = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The child already exited or cannot be signalled; nothing else to release.
        }
        finally
        {
            process.Dispose();
            IsServerManaged = false;
        }
    }
}

/// <summary>Walks up from the executable directory so relative asset paths work from bin/ and from tests.</summary>
internal static class LocalPathResolver
{
    public static string Resolve(string configured, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return string.Empty;

        if (Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);

        DirectoryInfo? directory = new(baseDirectory);

        for (int depth = 0; depth <= 10 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, configured);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, configured));
    }

    public static string ResolveDirectory(string configured, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return string.Empty;

        if (Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);

        DirectoryInfo? directory = new(baseDirectory);

        for (int depth = 0; depth <= 10 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, configured);
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, configured));
    }
}
