// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/QwenMoEService.cs
// PURPOSE: Manages llama-server.exe for Qwen3-VL-30B-A3B, streams OpenAI-style
//          SSE tokens over loopback HTTP, and falls back to a deterministic
//          rule-based tutor (simulated at ~20 tokens/sec) whenever the server,
//          the NVIDIA GPU, or the 19 GB model files are missing.
//
// RUNTIME OVERRIDES (any PC):
//   BREADBOARD_QWEN_ENDPOINT, _MODEL, _MMPROJ, _SERVER, _PORT, _CTX,
//   _GPU_LAYERS, _CPU_MOE, _THREADS, _THREADS_BATCH, _AUTO_LAUNCH, _MLOCK,
//   _FLASH_ATTN, _STARTUP_TIMEOUT, _REQUEST_TIMEOUT, _FALLBACK_TPS,
//   _MODEL_ALIAS, _API_KEY
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

namespace BreadboardAOI.Intelligence;

public enum QwenResponseMode
{
    Server = 0,
    RuleBasedFallback = 1
}

/// <summary>Configuration for the Qwen MoE service. Use <see cref="FromEnvironment"/> to honour per-PC overrides.</summary>
public sealed class QwenMoEOptions
{
    public const string EnvironmentPrefix = "BREADBOARD_QWEN_";
    public const int DefaultPort = 8089;
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
/// Owns the llama-server child process and exposes an <see cref="IAsyncEnumerable{T}"/>
/// of tutor tokens. Never blocks the 60 FPS reflex loop.
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

    /// <summary>Which path produced the most recent answer.</summary>
    public QwenResponseMode LastMode { get; private set; } = QwenResponseMode.RuleBasedFallback;

    /// <summary>
    /// When true and Qwen is offline, yields deterministic rule-based advice.
    /// When false, yields an explicit offline message explaining how to connect Qwen.
    /// </summary>
    public bool AllowRuleBasedFallback { get; set; } = true;

    public string? LastError { get; private set; }

    /// <summary>
    /// Streams the tutor answer for a DRC snapshot. Tries the GPU server first,
    /// then transparently switches to the deterministic rule-based tutor if enabled.
    /// </summary>
    public async IAsyncEnumerable<string> StreamDiagnosticAsync(
        DrcStatePacket state,
        string? prompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(_disposed, this);

        prompt ??= DiagnosticPromptBuilder.Build(state);

        bool serverReady = await TryEnsureServerAsync(cancellationToken).ConfigureAwait(false);
        if (serverReady)
        {
            bool emittedAny = false;
            bool streamFailed = false;

            await using (IAsyncEnumerator<string> enumerator =
                StreamServerAsync(prompt, state.MicroCropPngBytes, cancellationToken).GetAsyncEnumerator(cancellationToken))
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
                    yield return enumerator.Current;
                }
            }

            if (emittedAny)
            {
                LastMode = QwenResponseMode.Server;
                LastError = null;
                yield break;
            }

            if (!streamFailed)
                LastError = "Qwen server returned an empty answer; checking fallback policy.";
        }

        LastMode = QwenResponseMode.RuleBasedFallback;
        if (!AllowRuleBasedFallback)
        {
            yield return "[Qwen AI is offline. Connect via the AI Routing tab (Local Qwen / Colab / LM Studio / Ollama), or enable Rule-Based Advice in the Customization tab.]";
            yield break;
        }

        await foreach (string token in StreamFallbackAsync(state, cancellationToken).ConfigureAwait(false))
            yield return token;
    }

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
        // A remote/custom endpoint may already be serving the model even when the
        // 19 GB GGUF is not on this PC.
        if (await IsServerReachableAsync(cancellationToken).ConfigureAwait(false))
            return true;

        if (!_options.AutoLaunchServer)
        {
            LastError = "llama-server is not reachable and auto-launch is disabled; using the rule-based fallback tutor.";
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
        // The 30B expert weights are streamed by the AI mask; on small laptops the
        // portable plan simply uses the lower half of the available CPUs.
        try
        {
            process.ProcessorAffinity = (IntPtr)(nint)CpuAffinityManager.Plan.AiMask;
        }
        catch (Exception ex) when (ex is Win32Exception
                                       or InvalidOperationException
                                       or NotSupportedException
                                       or PlatformNotSupportedException
                                       or ArgumentException)
        {
            // Affinity is an optimization; llama-server still runs when rejected.
        }
    }

    private async Task<bool> IsServerReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));

            using HttpResponseMessage response = await _httpClient
                .GetAsync(BuildHealthUri(), HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
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
            return new UriBuilder(endpoint) { Path = "/health", Query = string.Empty }.Uri;

        return new Uri($"http://127.0.0.1:{_options.Port}/health");
    }

    private async IAsyncEnumerable<string> StreamServerAsync(
        string prompt,
        byte[]? imagePng,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(9)
        {
            ["model"] = _options.ModelAlias,
            ["stream"] = true,
            ["temperature"] = 0.2,
            ["max_tokens"] = 160,
            ["messages"] = new object[]
            {
                new { role = "system", content = "You are a calm, experienced breadboard tutor speaking across the bench. Use plain spoken English and contractions. Sound human and specific, never theatrical. Never use markdown or lists." },
                new { role = "user", content = BuildUserContent(prompt, imagePng) }
            },
            ["chat_template_kwargs"] = new { enable_thinking = false }
        };

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

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

            string? token = ExtractContentDelta(data);
            if (!string.IsNullOrEmpty(token))
                yield return token;
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

    private static string? ExtractContentDelta(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement choice = choices[0];

            if (choice.TryGetProperty("delta", out JsonElement delta)
                && delta.TryGetProperty("content", out JsonElement deltaContent)
                && deltaContent.ValueKind == JsonValueKind.String)
            {
                return deltaContent.GetString();
            }

            if (choice.TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("content", out JsonElement messageContent)
                && messageContent.ValueKind == JsonValueKind.String)
            {
                return messageContent.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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
