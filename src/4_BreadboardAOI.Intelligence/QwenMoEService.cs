// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/QwenMoEService.cs
// PURPOSE: Manages llama-server.exe for Qwen3-VL-30B-A3B, queries port 8081
//          (or configured port) with datasheet_netlist.gbnf grammar enforcement
//          to extract Golden Netlist JSON in <120 tokens, streams OpenAI-style
//          SSE tokens over loopback HTTP, and falls back to a deterministic
//          rule-based tutor (~20 tokens/sec) whenever the server, the NVIDIA
//          GPU, or the 19 GB model files are missing.
//
// RUNTIME OVERRIDES (any PC):
//   BREADBOARD_QWEN_ENDPOINT, _MODEL, _MMPROJ, _SERVER, _PORT, _NETLIST_PORT,
//   _GRAMMAR, _CTX, _GPU_LAYERS, _CPU_MOE, _THREADS, _THREADS_BATCH,
//   _AUTO_LAUNCH, _MLOCK, _FLASH_ATTN, _STARTUP_TIMEOUT, _REQUEST_TIMEOUT,
//   _FALLBACK_TPS, _MODEL_ALIAS, _API_KEY
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

public enum QwenResponseMode
{
    Server = 0,
    RuleBasedFallback = 1
}

/// <summary>Which part of a streaming model answer an event belongs to.</summary>
public enum QwenStreamKind
{
    Thinking = 0,
    Content = 1
}

/// <summary>
/// One streamed piece of an answer. Thinking is the model's reasoning trace;
/// content is the final spoken/text answer. Keeping them separate stops raw
/// &lt;think&gt; tags from leaking into the DRC transcript.
/// </summary>
public readonly record struct QwenStreamEvent(QwenStreamKind Kind, string Text);

/// <summary>One pin-to-net assignment in a Golden Netlist extracted from a datasheet schematic.</summary>
public sealed record GoldenNetConnection(string Pin, string Net);

/// <summary>Structured Golden Netlist extracted under <c>datasheet_netlist.gbnf</c> in &lt;120 tokens.</summary>
public sealed record GoldenNetlistResult(
    string Ic,
    IReadOnlyList<GoldenNetConnection> Connections,
    string RawJson,
    int TokenEstimate);

/// <summary>Configuration for the Qwen MoE service. Use <see cref="FromEnvironment"/> to honour per-PC overrides.</summary>
public sealed class QwenMoEOptions
{
    public const string EnvironmentPrefix = "BREADBOARD_QWEN_";
    public const int DefaultPort = 8089;
    public const int DefaultNetlistPort = 8081;
    public const int MaxGoldenNetlistTokens = 118;
    public const string DefaultModelFile = "Qwen3-VL-30B-A3B-Instruct-Q4_K_M.gguf";
    public const string DefaultMmprojFile = "mmproj-Qwen3-VL-30B-f16.gguf";
    public const string DefaultGrammarFile = "datasheet_netlist.gbnf";

    /// <summary>
    /// Built-in GBNF grammar matching <c>datasheet_netlist.gbnf</c> so Golden Netlist
    /// extraction remains strictly constrained (<c>&lt;120 tokens</c>) even when the external
    /// <c>assets/grammars/datasheet_netlist.gbnf</c> file is absent.
    /// </summary>
    public const string DefaultDatasheetNetlistGbnf = """
        root   ::= "{" ws "\"ic\":" ws string "," ws "\"nets\":" ws "[" ws net ("," ws net)* ws "]" ws "}"
        net    ::= "{" ws "\"pin\":" ws string "," ws "\"net\":" ws string ws "}"
        string ::= "\"" ([A-Za-z0-9_+\-./ ])+ "\""
        ws     ::= [ \t\n]*
        """;

    public string EndpointUrl { get; init; } = $"http://127.0.0.1:{DefaultPort}/v1/chat/completions";

    /// <summary>Dedicated llama-server endpoint on port 8081 for GBNF-constrained Golden Netlist extraction.</summary>
    public string NetlistEndpointUrl { get; init; } = $"http://127.0.0.1:{DefaultNetlistPort}/v1/chat/completions";

    public string ServerExecutablePath { get; init; } = Path.Combine("tools", "llama-server.exe");

    public string ModelPath { get; init; } = Path.Combine("assets", "models", DefaultModelFile);

    public string MmprojPath { get; init; } = Path.Combine("assets", "models", DefaultMmprojFile);

    public string GrammarPath { get; init; } = Path.Combine("assets", "grammars", DefaultGrammarFile);

    public int Port { get; init; } = DefaultPort;

    public int NetlistPort { get; init; } = DefaultNetlistPort;

    public int ContextSize { get; init; } = 4096;

    public int GpuLayers { get; init; } = 99;

    /// <summary>Experts kept in DDR4. Set to null/0 for dense models that do not support --n-cpu-moe.</summary>
    public int? CpuMoeLayers { get; init; } = 22;

    /// <summary>Half of the P-core batch width on the 20-thread target; scales down on smaller CPUs.</summary>
    public int Threads { get; init; } = DeriveThreads(Environment.ProcessorCount);

    public int ThreadsBatch { get; init; } = DeriveThreadsBatch(Environment.ProcessorCount);

    public bool AutoLaunchServer { get; init; } = true;

    public bool Mlock { get; init; } = true;

    public bool FlashAttention { get; init; } = true;

    public double ServerStartupTimeoutSeconds { get; init; } = 120d;

    public double RequestTimeoutSeconds { get; init; } = 120d;

    /// <summary>Fallback token pacing; 20 tokens/sec is the target MoE decode speed.</summary>
    public double FallbackTokensPerSecond { get; init; } = 20d;

    public string ModelAlias { get; init; } = "qwen3-vl-30b-a3b";

    public string? ApiKey { get; init; }

    /// <summary>20 logical processors -> 12 batch threads, 6 decode threads (the locked workstation spec).</summary>
    public static int DeriveThreadsBatch(int logicalProcessors) => Math.Clamp(logicalProcessors, 1, 12);

    /// <summary>Half of <see cref="DeriveThreadsBatch"/>, never below one thread.</summary>
    public static int DeriveThreads(int logicalProcessors) => Math.Max(1, DeriveThreadsBatch(logicalProcessors) / 2);

    public static QwenMoEOptions FromEnvironment()
    {
        int port = ReadInt("PORT") ?? DefaultPort;
        int netlistPort = ReadInt("NETLIST_PORT") ?? DefaultNetlistPort;
        int processors = Environment.ProcessorCount;

        return new QwenMoEOptions
        {
            EndpointUrl = ReadString("ENDPOINT") ?? $"http://127.0.0.1:{port}/v1/chat/completions",
            NetlistEndpointUrl = ReadString("NETLIST_ENDPOINT") ?? $"http://127.0.0.1:{netlistPort}/v1/chat/completions",
            ServerExecutablePath = ReadString("SERVER") ?? Path.Combine("tools", "llama-server.exe"),
            ModelPath = ReadString("MODEL") ?? Path.Combine("assets", "models", DefaultModelFile),
            MmprojPath = ReadString("MMPROJ") ?? Path.Combine("assets", "models", DefaultMmprojFile),
            GrammarPath = ReadString("GRAMMAR") ?? Path.Combine("assets", "grammars", DefaultGrammarFile),
            Port = port,
            NetlistPort = netlistPort,
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
            FallbackTokensPerSecond = ReadDouble("FALLBACK_TPS") ?? 20d,
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
        NetlistEndpointUrl = chatEndpoint,
        ServerExecutablePath = ServerExecutablePath,
        ModelPath = ModelPath,
        MmprojPath = MmprojPath,
        GrammarPath = GrammarPath,
        Port = Port,
        NetlistPort = NetlistPort,
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
        FallbackTokensPerSecond = FallbackTokensPerSecond,
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
/// Owns the llama-server child process, queries port 8081 with <c>datasheet_netlist.gbnf</c>
/// for &lt;120-token Golden Netlist JSON extraction, and exposes an <see cref="IAsyncEnumerable{T}"/>
/// of tutor tokens. Never blocks the 60 FPS reflex loop.
/// </summary>
public sealed class QwenMoEService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] ReasoningPropertyNames =
    {
        "reasoning_content",
        "reasoning",
        "thinking",
        "thought",
        "analysis"
    };

    private string? _discoveredModelAlias;

    /// <summary>Operator-selected model; wins over auto-discovery for any OpenAI-compatible server.</summary>
    private volatile string? _modelOverride;

    /// <summary>
    /// Minimal role anchor. The task itself lives in the user prompt, and the
    /// image is the authority, so no style or detection rules are injected here.
    /// </summary>
    private const string TutorSystemPrompt = "You are an electronics tutor examining a breadboard.";

    private const string GoldenNetlistSystemPrompt =
        "Extract the Typical Application Golden Netlist for the target IC as compact JSON matching the grammar.";

    private readonly QwenMoEOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private readonly object _processSync = new();

    private Process? _serverProcess;
    private string? _launchError;
    private string? _lastServerLog;
    private bool _disposed;

    public QwenMoEService(QwenMoEOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? QwenMoEOptions.FromEnvironment();
        BaseDirectory = AppContext.BaseDirectory;

        ModelPath = LocalPathResolver.Resolve(_options.ModelPath, BaseDirectory);
        MmprojPath = LocalPathResolver.Resolve(_options.MmprojPath, BaseDirectory);
        GrammarPath = LocalPathResolver.Resolve(_options.GrammarPath, BaseDirectory);
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

    public string GrammarPath { get; }

    public string ServerExecutablePath { get; }

    public bool HasLocalModel => File.Exists(ModelPath);

    public bool HasVisionProjector => File.Exists(MmprojPath);

    /// <summary>True when this process launched llama-server.exe and must terminate it on dispose.</summary>
    public bool IsServerManaged { get; private set; }

    /// <summary>Which path produced the most recent answer.</summary>
    public QwenResponseMode LastMode { get; private set; } = QwenResponseMode.RuleBasedFallback;

    /// <summary>
    /// When true and Qwen is offline, yields deterministic rule-based advice.
    /// When false, yields an explicit offline message explaining how to connect Qwen.
    /// </summary>
    public bool AllowRuleBasedFallback { get; set; } = true;

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
                return "QWEN";

            return endpoint.Port switch
            {
                1234 => "LM STUDIO",
                11434 => "OLLAMA",
                QwenMoEOptions.DefaultPort or QwenMoEOptions.DefaultNetlistPort => "LOCAL QWEN",
                _ => endpoint.IsLoopback ? "LOCAL AI" : "REMOTE QWEN"
            };
        }
    }

    /// <summary>Provider + loaded model alias, for the studio badge and status line.</summary>
    public string EndpointSummary => $"{EndpointProvider} · {ActiveModelAlias}";

    /// <summary>Model name currently sent on every chat request.</summary>
    public string ActiveModelAlias => _modelOverride ?? _discoveredModelAlias ?? _options.ModelAlias;

    /// <summary>
    /// Pins the chat payload to an exact model id. Pass null or blank to return
    /// to server-driven discovery. This links any LM Studio, Ollama, llama.cpp
    /// or vLLM model without editing configuration or restarting the app.
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
    /// Loads the <c>datasheet_netlist.gbnf</c> grammar from disk if present, otherwise returns
    /// <see cref="QwenMoEOptions.DefaultDatasheetNetlistGbnf"/>.
    /// </summary>
    public string LoadDatasheetNetlistGrammar()
    {
        if (!string.IsNullOrWhiteSpace(GrammarPath) && File.Exists(GrammarPath))
        {
            try
            {
                string content = File.ReadAllText(GrammarPath, Encoding.UTF8).Trim();
                if (content.Length > 0)
                    return content;
            }
            catch (IOException)
            {
                // Fall back to embedded grammar.
            }
        }

        return QwenMoEOptions.DefaultDatasheetNetlistGbnf;
    }

    /// <summary>
    /// Queries <c>llama-server</c> on port 8081 (or the active endpoint if 8081 is redirected)
    /// with <c>datasheet_netlist.gbnf</c> grammar strictly enforced and <c>max_tokens</c> capped
    /// below 120 tokens to extract the Golden Netlist JSON from a cropped Typical Application schematic.
    /// </summary>
    public async Task<GoldenNetlistResult> ExtractGoldenNetlistAsync(
        string partNumber,
        byte[] schematicPngBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partNumber);
        ArgumentNullException.ThrowIfNull(schematicPngBytes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string normalizedPart = AlldatasheetRangeFetcher.NormalizePartKey(partNumber);
        string grammar = LoadDatasheetNetlistGrammar();
        int maxTokens = Math.Min(118, QwenMoEOptions.MaxGoldenNetlistTokens);

        string prompt =
            $"Part: {normalizedPart}. Extract the Typical Application pin-to-net connections from this schematic.";

        string targetEndpoint = await SelectNetlistEndpointAsync(cancellationToken).ConfigureAwait(false);
        string activeModel = await ResolveModelAliasAsync(cancellationToken).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(10)
        {
            ["model"] = activeModel,
            ["stream"] = false,
            ["temperature"] = 0.0,
            ["max_tokens"] = maxTokens,
            ["n_predict"] = maxTokens,
            ["grammar"] = grammar,
            ["chat_template_kwargs"] = new { enable_thinking = false },
            ["messages"] = new object[]
            {
                new { role = "system", content = GoldenNetlistSystemPrompt },
                new { role = "user", content = BuildUserContent(prompt, schematicPngBytes) }
            }
        };

        try
        {
            string requestJson = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, targetEndpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (TryExtractJsonResponse(body, out _, out string? content)
                    && !string.IsNullOrWhiteSpace(content)
                    && TryParseGoldenNetlistJson(content, normalizedPart, out GoldenNetlistResult? parsed))
                {
                    LastMode = QwenResponseMode.Server;
                    IsConnected = true;
                    LastAnswerUsable = true;
                    LastError = null;
                    return parsed!;
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRecoverable(ex))
        {
            LastError = $"Golden Netlist extraction on {targetEndpoint} fell back to deterministic profile: {ex.Message}";
        }

        LastMode = QwenResponseMode.RuleBasedFallback;
        return BuildDeterministicGoldenNetlist(normalizedPart);
    }

    /// <summary>Parses a GBNF-constrained Golden Netlist JSON payload.</summary>
    public static bool TryParseGoldenNetlistJson(
        string? rawJson,
        string fallbackPartNumber,
        out GoldenNetlistResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(rawJson))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string ic = root.TryGetProperty("ic", out JsonElement icEl) && icEl.ValueKind == JsonValueKind.String
                ? icEl.GetString() ?? fallbackPartNumber
                : fallbackPartNumber;

            var connections = new List<GoldenNetConnection>();
            if (root.TryGetProperty("nets", out JsonElement netsEl) && netsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in netsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    string? pin = item.TryGetProperty("pin", out JsonElement pinEl) && pinEl.ValueKind == JsonValueKind.String
                        ? pinEl.GetString()
                        : null;
                    string? net = item.TryGetProperty("net", out JsonElement netEl) && netEl.ValueKind == JsonValueKind.String
                        ? netEl.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(pin) && !string.IsNullOrWhiteSpace(net))
                        connections.Add(new GoldenNetConnection(pin, net));
                }
            }

            if (connections.Count == 0)
                return false;

            string compact = rawJson.Trim();
            int estimatedTokens = Math.Min(119, Math.Max(1, (compact.Length + 3) / 4));
            result = new GoldenNetlistResult(ic, connections, compact, estimatedTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deterministic reference Golden Netlist (<c>&lt;120 tokens</c>) when llama-server is offline.
    /// </summary>
    public static GoldenNetlistResult BuildDeterministicGoldenNetlist(string partNumber)
    {
        string key = AlldatasheetRangeFetcher.NormalizePartKey(partNumber);
        IReadOnlyList<GoldenNetConnection> connections = key switch
        {
            var p when p.Contains("555", StringComparison.OrdinalIgnoreCase) =>
            [
                new("1", "GND"),
                new("2", "TRIG"),
                new("3", "OUT"),
                new("4", "VCC"),
                new("5", "CTRL"),
                new("6", "THRES"),
                new("7", "DISCH"),
                new("8", "VCC")
            ],
            var p when p.Contains("358", StringComparison.OrdinalIgnoreCase) || p.Contains("741", StringComparison.OrdinalIgnoreCase) =>
            [
                new("1", "OUT1"),
                new("2", "IN1-"),
                new("3", "IN1+"),
                new("4", "GND"),
                new("8", "VCC")
            ],
            var p when p.Contains("7805", StringComparison.OrdinalIgnoreCase) || p.Contains("1117", StringComparison.OrdinalIgnoreCase) =>
            [
                new("1", "VIN"),
                new("2", "GND"),
                new("3", "VOUT")
            ],
            _ =>
            [
                new("1", "GND"),
                new("2", "IN"),
                new("3", "OUT"),
                new("4", "VCC")
            ]
        };

        var sb = new StringBuilder(192);
        sb.Append("{\"ic\":\"").Append(key).Append("\",\"nets\":[");
        for (int i = 0; i < connections.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"pin\":\"").Append(connections[i].Pin)
              .Append("\",\"net\":\"").Append(connections[i].Net).Append("\"}");
        }
        sb.Append("]}");

        string json = sb.ToString();
        int tokenEstimate = Math.Min(119, Math.Max(1, (json.Length + 3) / 4));
        return new GoldenNetlistResult(key, connections, json, tokenEstimate);
    }

    private async Task<string> SelectNetlistEndpointAsync(CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(_options.NetlistEndpointUrl, UriKind.Absolute, out Uri? netlistUri))
        {
            Uri healthUri = BuildSiblingUri(netlistUri, "/health");
            if (await ProbeAsync(healthUri, cancellationToken).ConfigureAwait(false))
                return _options.NetlistEndpointUrl;
        }

        if (await TryEnsureServerAsync(cancellationToken).ConfigureAwait(false))
            return _options.EndpointUrl;

        return _options.NetlistEndpointUrl;
    }

    /// <summary>
    /// Streams the tutor answer for a DRC snapshot as content tokens only. This is
    /// the workstation transcript path: reasoning is discarded so raw thinking
    /// tags never leak into the DRC explanation.
    /// </summary>
    public async IAsyncEnumerable<string> StreamDiagnosticAsync(
        DrcStatePacket state,
        string? prompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (QwenStreamEvent streamEvent in StreamDiagnosticDetailedAsync(
                           state,
                           prompt,
                           allowThinking: false,
                           cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent.Kind == QwenStreamKind.Content && !string.IsNullOrEmpty(streamEvent.Text))
                yield return streamEvent.Text;
        }
    }

    /// <summary>
    /// Streams both the model's reasoning trace and its final answer for the AI
    /// Live Studio. The caller decides which channel to display and speak.
    /// </summary>
    public async IAsyncEnumerable<QwenStreamEvent> StreamDiagnosticDetailedAsync(
        DrcStatePacket state,
        string? prompt = null,
        bool allowThinking = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string effectivePrompt = prompt ?? DiagnosticPromptBuilder.Build(state);
        byte[]? visualPng = state.FullBoardPngBytes ?? state.MicroCropPngBytes;

        await foreach (QwenStreamEvent streamEvent in StreamDetailedAsync(
                           state,
                           effectivePrompt,
                           visualPng,
                           maxTokens: allowThinking ? 2048 : 512,
                           allowThinking,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Streams a free-form photo inspection. Used by the studio's UPLOAD IMAGE
    /// path, where the uploaded photo is the authoritative vision input and no
    /// DRC packet is required.
    /// </summary>
    public async IAsyncEnumerable<QwenStreamEvent> StreamImageDetailedAsync(
        byte[] pngBytes,
        string prompt,
        bool allowThinking = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pngBytes.Length == 0)
        {
            LastAnswerUsable = false;
            yield return new QwenStreamEvent(
                QwenStreamKind.Content,
                "[No image was supplied to the vision model. Upload a PNG or JPG and press ANALYZE AGAIN.]");
            yield break;
        }

        await foreach (QwenStreamEvent streamEvent in StreamDetailedAsync(
                           state: null,
                           prompt,
                           pngBytes,
                           maxTokens: 2048,
                           allowThinking,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return streamEvent;
        }
    }

    /// <summary>
    /// Shared server/fallback pipeline. When a server answers, reasoning and
    /// content events pass through untouched. When it does not, a deterministic
    /// DRC answer is used if one is available; otherwise an honest offline
    /// message is emitted. No reasoning is fabricated in either case.
    /// </summary>
    private async IAsyncEnumerable<QwenStreamEvent> StreamDetailedAsync(
        DrcStatePacket? state,
        string prompt,
        byte[]? imagePng,
        int maxTokens,
        bool allowThinking,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastAnswerUsable = false;

        bool serverReady = await TryEnsureServerAsync(cancellationToken).ConfigureAwait(false);
        if (serverReady)
        {
            bool emittedAny = false;
            bool emittedContent = false;
            bool streamFailed = false;

            await using (IAsyncEnumerator<QwenStreamEvent> enumerator =
                StreamServerAsync(
                        prompt,
                        imagePng,
                        maxTokens,
                        allowThinking,
                        cancellationToken)
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
                        LastError = $"Qwen server stream failed: {ex.Message}";
                        streamFailed = true;
                        break;
                    }

                    if (!moved)
                        break;

                    emittedAny = true;
                    QwenStreamEvent streamEvent = enumerator.Current;
                    if (streamEvent.Kind == QwenStreamKind.Content && !string.IsNullOrEmpty(streamEvent.Text))
                        emittedContent = true;

                    yield return streamEvent;
                }
            }

            if (emittedContent)
            {
                LastMode = QwenResponseMode.Server;
                IsConnected = true;
                LastAnswerUsable = true;
                LastError = null;
                yield break;
            }

            if (emittedAny)
            {
                LastMode = QwenResponseMode.Server;
                IsConnected = true;
                LastError = "Qwen streamed a reasoning trace but no final answer.";
                yield return new QwenStreamEvent(
                    QwenStreamKind.Content,
                    "[Qwen finished thinking but returned no final answer. Raise the token budget in LM Studio / the provider, then press ANALYZE AGAIN.]");
                yield break;
            }

            if (!streamFailed)
                LastError = "Qwen server returned an empty answer.";
        }

        LastMode = QwenResponseMode.RuleBasedFallback;

        if (state is null || !AllowRuleBasedFallback)
        {
            yield return new QwenStreamEvent(QwenStreamKind.Content, BuildOfflineMessage());
            yield break;
        }

        await foreach (string token in StreamFallbackAsync(state, cancellationToken).ConfigureAwait(false))
            yield return new QwenStreamEvent(QwenStreamKind.Content, token);
    }

    private string BuildOfflineMessage()
    {
        string detail = string.IsNullOrWhiteSpace(LastError)
            ? "No OpenAI-compatible endpoint answered."
            : LastError!;

        string hint = EndpointProvider switch
        {
            "LM STUDIO" =>
                "Start LM Studio, load a vision-capable Qwen model, enable the local server on port 1234, and connect it under AI ROUTING > LOCAL AI > LM STUDIO.",
            "OLLAMA" =>
                "Start Ollama, pull a vision model, and connect it under AI ROUTING > LOCAL AI > OLLAMA.",
            "LOCAL QWEN" =>
                "Start llama-server, or connect LM Studio / Ollama under AI ROUTING > LOCAL AI.",
            _ =>
                "Connect a vision-capable OpenAI-compatible endpoint under AI ROUTING."
        };

        return $"[Qwen AI is offline] {detail} {hint}";
    }

    /// <summary>
    /// The only gate for synthesized speech: the answer must come from the model
    /// itself. Rule-based / DRC text is displayed but never spoken.
    /// </summary>
    public static bool ShouldSpeakAnswer(
        QwenResponseMode mode,
        bool voiceMuted,
        bool hasVoiceEngine,
        int sentenceCount) =>
        !voiceMuted
        && hasVoiceEngine
        && sentenceCount > 0
        && mode == QwenResponseMode.Server;

    /// <summary>
    /// Deterministic tutor sentence built directly from the highest-priority
    /// violation. Public so the UI can display it without any model installed.
    /// </summary>
    public static string BuildRuleBasedDiagnostic(DrcStatePacket state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Violations.Count == 0)
        {
            return state.Components.Count == 0
                ? "No components are visible on the board yet, so place a part and let the frame settle."
                : "The board passes every electrical rule and the detected circuit matches the target netlist.";
        }

        DrcViolation violation = state.Violations[0];
        string component = string.IsNullOrWhiteSpace(violation.ComponentId) ? "component" : violation.ComponentId;
        string badHole = string.IsNullOrWhiteSpace(violation.BadHole.HoleId)
            ? $"strip {violation.OffendingStripId}"
            : violation.BadHole.HoleId;
        string targetHole = string.IsNullOrWhiteSpace(violation.SuggestedTargetHole.HoleId)
            ? string.Empty
            : violation.SuggestedTargetHole.HoleId;

        (string action, string why, string check) = violation.Code switch
        {
            ViolationCode.ErcVccGndDirectShort => (
                $"Remove the jumper from {badHole} to the ground rail, as it ties VCC straight to GND and creates a dead short.",
                "If you apply power, the supply will dump its whole current through that wire and can overheat the board or the supply.",
                "Pull that jumper before you power anything, then re-check the rail colours so VCC only reaches VCC."),

            ViolationCode.ErcSameStripShort when targetHole.Length > 0 => (
                $"Move lead 2 of {component} from {badHole} to {targetHole}, as both leads sit in the same metal clip.",
                "The five holes in a column are one piece of metal, so the part is bypassed and will never do its job.",
                $"{targetHole} is the next free column; press the lead in there and let the DRC re-check the board."),

            ViolationCode.ErcSameStripShort => (
                $"Move one lead of {component} from {badHole} to an empty column, as both leads sit in the same metal clip.",
                "A single clip is one node, so the part is bypassed and the circuit cannot work.",
                "Leave at least one empty column between the legs, then reseat the part."),

            ViolationCode.ErcFloatingLead => (
                $"Move the free end of {component} from {badHole} to the target net, as {badHole} is the only terminal on its strip.",
                "A part only conducts when both of its ends reach a complete loop, so this lead carries no current.",
                "Run a jumper to the ground rail or the net named in the target netlist, then re-check the pin inspector."),

            ViolationCode.ErcRedundantJumper => (
                $"Remove the jumper from {badHole} to {targetHole}, as both holes already share one metal clip.",
                "The strip is already one node, so the wire is spare copper that only makes the board harder to read.",
                "Move one end to a net that still needs power if you meant to bridge two strips."),

            ViolationCode.ErcHoleCollision => (
                $"Move one lead out of {badHole} to the next free hole, as too many leads share one spring clip.",
                "A clip that is overfilled grips nothing firmly, so the joint will go intermittent when the board warms up.",
                "Reseat the remaining leads so every clip holds no more than two."),

            ViolationCode.DrcSchematicNetMismatch => (
                $"Add a jumper from {targetHole} to {badHole}, as the target net is open between them.",
                "An open net means the signal or power never reaches the part, so the circuit stays dead even though every part is placed.",
                "Confirm the new net in the pin inspector before moving on."),

            ViolationCode.DrcUnexpectedNetLink => (
                $"Remove the jumper that joins {targetHole} and {badHole}, as they belong to two different target nets.",
                "Bridging two nets mixes signals and can make one part drive another one backwards.",
                "Check the two nets separately in the pin inspector so they stay isolated."),

            ViolationCode.ErcReversedPolarity => (
                $"Refit {component} at {badHole} the other way round, as it looks reversed.",
                "An LED or capacitor fitted backwards will not work and can be damaged when power is applied.",
                "Check the flat side or the shorter lead, then press the part back in."),

            _ => (
                $"Recheck the connection at {badHole}, as the board reports {violation.Code}.",
                "That rule exists because this connection does not behave like a finished circuit.",
                "Compare it with the target netlist and reseat the leads.")
        };

        return $"{action} {why} {check}";
    }

    private async IAsyncEnumerable<string> StreamFallbackAsync(
        DrcStatePacket state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string answer = BuildRuleBasedDiagnostic(state);
        double tokensPerSecond = _options.FallbackTokensPerSecond;
        int delayMilliseconds = tokensPerSecond > 0
            ? Math.Max(1, (int)Math.Round(1000d / tokensPerSecond))
            : 0;

        foreach (string token in TokenizeForSpeech(answer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);

            yield return token;
        }
    }

    private static IEnumerable<string> TokenizeForSpeech(string sentence)
    {
        string[] words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            yield return i == words.Length - 1 ? words[i] : words[i] + " ";
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
            LastError = $"Qwen model not found at '{ModelPath}'; using the rule-based fallback tutor.";
            return false;
        }

        if (!File.Exists(ServerExecutablePath))
        {
            LastError = $"llama-server.exe not found at '{ServerExecutablePath}'; using the rule-based fallback tutor.";
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
            ?? "llama-server did not become ready in time; using the rule-based fallback tutor.";
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
    /// <c>/health</c> first and then the OpenAI <c>/v1/models</c> surface, so a
    /// connected LM Studio, Ollama, llama-server, or tunnel turns the light green
    /// even before the first diagnosis is requested.
    /// </summary>
    public async Task<bool> ProbeConnectionAsync(CancellationToken cancellationToken = default)
    {
        bool reachable = await IsServerReachableAsync(cancellationToken).ConfigureAwait(false);
        if (reachable)
        {
            _ = await ResolveModelAliasAsync(cancellationToken).ConfigureAwait(false);
        }
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
            // Keep configured default
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

    private async IAsyncEnumerable<QwenStreamEvent> StreamServerAsync(
        string prompt,
        byte[]? imagePng,
        int maxTokens,
        bool allowThinking,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string activeModel = await ResolveModelAliasAsync(cancellationToken).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(9)
        {
            ["model"] = activeModel,
            ["stream"] = true,
            ["temperature"] = 0.6,
            ["max_tokens"] = maxTokens,
            ["messages"] = new object[]
            {

                new { role = "user", content = BuildUserContent(prompt, imagePng) }
            }
        };

        // Ask every OpenAI-compatible server to expose reasoning separately so
        // the studio can show it in THINKING instead of losing it. Servers that
        // reject the option are retried below without it.


        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                response.StatusCode);
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!TryExtractJsonResponse(body, out string? thinking, out string? content))
                throw new JsonException("The provider returned JSON without an assistant message.");

            if (!string.IsNullOrEmpty(thinking))
                yield return new QwenStreamEvent(QwenStreamKind.Thinking, thinking);
            if (!string.IsNullOrEmpty(content))
                yield return new QwenStreamEvent(QwenStreamKind.Content, content);
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

            if (!TryExtractStreamDelta(data, out string? thinking, out string? content))
                continue;

            if (!string.IsNullOrEmpty(thinking))
                yield return new QwenStreamEvent(QwenStreamKind.Thinking, thinking);

            if (!string.IsNullOrEmpty(content))
            {
                string text = content;
                while (text.Length > 0)
                {
                    if (!insideThinkTag)
                    {
                        int thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                        if (thinkStart >= 0)
                        {
                            if (thinkStart > 0)
                                yield return new QwenStreamEvent(QwenStreamKind.Content, text[..thinkStart]);

                            insideThinkTag = true;
                            text = text[(thinkStart + 7)..];
                        }
                        else
                        {
                            yield return new QwenStreamEvent(QwenStreamKind.Content, text);
                            break;
                        }
                    }
                    else
                    {
                        int thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                        if (thinkEnd >= 0)
                        {
                            if (thinkEnd > 0)
                                yield return new QwenStreamEvent(QwenStreamKind.Thinking, text[..thinkEnd]);

                            insideThinkTag = false;
                            text = text[(thinkEnd + 8)..];
                        }
                        else
                        {
                            yield return new QwenStreamEvent(QwenStreamKind.Thinking, text);
                            break;
                        }
                    }
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

    private bool IsLlamaCppEndpoint()
    {
        if (!Uri.TryCreate(_options.EndpointUrl, UriKind.Absolute, out Uri? endpoint))
            return false;

        return endpoint.Port == 8089
               || endpoint.Port == 8081
               || endpoint.Port == _options.Port
               || endpoint.Port == _options.NetlistPort;
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

    private static bool TryExtractJsonResponse(
        string json,
        out string? thinking,
        out string? content)
    {
        thinking = null;
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
                extracted = TryExtractMessageParts(message, out thinking, out content);
            else if (choice.TryGetProperty("delta", out JsonElement delta))
                extracted = TryExtractMessageParts(delta, out thinking, out content);
            else if (choice.TryGetProperty("text", out JsonElement text)
                     && text.ValueKind == JsonValueKind.String)
            {
                content = text.GetString();
                extracted = content is not null;
            }

            if (extracted && !string.IsNullOrEmpty(content) && string.IsNullOrEmpty(thinking))
            {
                int tStart = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                int tEnd = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (tStart >= 0 && tEnd > tStart)
                {
                    thinking = content.Substring(tStart + 7, tEnd - tStart - 7).Trim();
                    content = (content.Substring(0, tStart) + content.Substring(tEnd + 8)).Trim();
                }
            }

            return extracted;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractStreamDelta(
        string json,
        out string? thinking,
        out string? content)
    {
        thinking = null;
        content = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                content = "[Qwen provider error] " + ExtractErrorText(error);
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
                return TryExtractMessageParts(delta, out thinking, out content);

            if (choice.TryGetProperty("message", out JsonElement message))
                return TryExtractMessageParts(message, out thinking, out content);

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

    private static bool TryExtractMessageParts(
        JsonElement element,
        out string? thinking,
        out string? content)
    {
        thinking = null;
        content = null;

        foreach (string name in ReasoningPropertyNames)
        {
            if (!element.TryGetProperty(name, out JsonElement reasoning))
                continue;

            thinking = ExtractTextContent(reasoning);
            if (!string.IsNullOrEmpty(thinking))
                break;
        }

        if (element.TryGetProperty("content", out JsonElement contentElement))
            content = ExtractTextContent(contentElement);

        return !string.IsNullOrEmpty(thinking) || !string.IsNullOrEmpty(content);
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
