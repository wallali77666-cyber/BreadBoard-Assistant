// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/ElevenLabsVoiceClient.cs
// PURPOSE: Minimal, dependency-free ElevenLabs client for the tutor voice.
//          - GET  /v1/voices                     -> the operator's voice catalog
//          - POST /v1/text-to-speech/{voice_id}  -> raw 24 kHz PCM16 mono
//          The API key never leaves this process except in the xi-api-key header,
//          and no audio is written to disk: PCM goes straight to NAudio.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BreadboardAOI.Intelligence;

/// <summary>One selectable ElevenLabs voice from the account's catalog.</summary>
public sealed record ElevenLabsVoice(
    string VoiceId,
    string Name,
    string Category,
    string Accent)
{
    /// <summary>Label shown in the picker, e.g. "Rachel · american".</summary>
    public string DisplayName => BuildDisplayName(Name, Accent, VoiceId);

    public override string ToString() => DisplayName;

    internal static string BuildDisplayName(string? name, string? accent, string? voiceId)
    {
        string resolvedName = string.IsNullOrWhiteSpace(name) ? voiceId ?? string.Empty : name.Trim();
        if (string.IsNullOrWhiteSpace(resolvedName))
            resolvedName = "UNNAMED VOICE";

        return string.IsNullOrWhiteSpace(accent)
            ? resolvedName
            : string.Create(CultureInfo.InvariantCulture, $"{resolvedName} \u00B7 {accent.Trim()}");
    }
}

/// <summary>Raw mono PCM returned by ElevenLabs, ready for the NAudio path.</summary>
public readonly record struct ElevenLabsAudio(byte[] Pcm, int SampleRate);

/// <summary>Carries the HTTP status so the UI can distinguish bad key, quota and network.</summary>
public sealed class ElevenLabsException : Exception
{
    public ElevenLabsException(string message, HttpStatusCode statusCode = 0, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// Talks to the ElevenLabs REST API. One instance is enough for the whole app;
/// it is thread-safe because <see cref="HttpClient"/> is.
/// </summary>
public sealed class ElevenLabsVoiceClient : IDisposable
{
    /// <summary>Built-in premade ElevenLabs voices available immediately without an initial network call.</summary>
    public static readonly IReadOnlyList<ElevenLabsVoice> DefaultVoices = new[]
    {
        new ElevenLabsVoice("21m00Tcm4TlvDq8ikWAM", "Rachel", "premade", "American, Calm"),
        new ElevenLabsVoice("pNInz6obpgDQGcFmaJgB", "Adam", "premade", "American, Deep"),
        new ElevenLabsVoice("ErXwobaYiN019PkySvjV", "Antoni", "premade", "American, Well-rounded"),
        new ElevenLabsVoice("IKne3meq5aSn9XLyUdCD", "Charlie", "premade", "Australian, Casual"),
        new ElevenLabsVoice("JBFqnCBsd6RMkjVDRZzb", "George", "premade", "British, Warm"),
        new ElevenLabsVoice("EXAVITQu4vr4xnSDxMaL", "Sarah", "premade", "American, Soft"),
        new ElevenLabsVoice("N2lVS1w4EtoT3dr4eOWO", "Callum", "premade", "American, Intense"),
        new ElevenLabsVoice("XB0fDUnXU5ikFXqc1WN4", "Charlotte", "premade", "English, Seductive"),
        new ElevenLabsVoice("Xb7hH8MSUJpSbSDYk0k2", "Alice", "premade", "British, Confident"),
        new ElevenLabsVoice("pqHfZKP75CvOlQylNhV4", "Bill", "premade", "American, Trustworthy"),
        new ElevenLabsVoice("nPczCjzI2devNBz1zQrb", "Brian", "premade", "American, Deep"),
        new ElevenLabsVoice("onwK4e9ZLuTAKqWW03F9", "Daniel", "premade", "British, Authoritative"),
        new ElevenLabsVoice("cjVigY5qzO86Huf0OWal", "Eric", "premade", "American, Conversational"),
        new ElevenLabsVoice("cgSgspJ2msm6clMCkdW9", "Jessica", "premade", "American, Expressive"),
        new ElevenLabsVoice("TX3LPaxmHKxFdv7VOQHJ", "Liam", "premade", "American, Articulate"),
        new ElevenLabsVoice("pFZP5JQG7iQjIQuC4Bku", "Lily", "premade", "British, Warm"),
        new ElevenLabsVoice("XrExE9yKIg1WjnnlVkGX", "Matilda", "premade", "American, Warm"),
        new ElevenLabsVoice("SAz9YHcvj6GT2YYXdXww", "River", "premade", "American, Relaxed"),
        new ElevenLabsVoice("bIHbv24MWmeRgasZH58o", "Will", "premade", "American, Friendly")
    };

    public const string DefaultBaseUrl = "https://api.elevenlabs.io";

    /// <summary>Fast, natural, and cheap enough for short tutor answers.</summary>
    public const string DefaultModelId = "eleven_turbo_v2_5";

    /// <summary>All requested PCM formats are 16-bit mono; this is the default.</summary>
    public const int PcmSampleRate = 24000;

    private const string OutputFormat = "pcm_24000";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private bool _disposed;

    public ElevenLabsVoiceClient(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BreadboardAOI/1.0");
    }

    /// <summary>True when the operator supplied something that looks like a key.</summary>
    public static bool HasApiKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && apiKey.Trim().Length >= 8;

    /// <summary>Last transport-level failure, for the status bar.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Lists the voices available to the key. Tries the stable v1 route first and
    /// falls back to v2 for accounts where v1 is retired. Returns an empty list
    /// (with <see cref="LastError"/> set) instead of throwing, so a refresh button
    /// can never crash the workstation.
    /// </summary>
    public async Task<IReadOnlyList<ElevenLabsVoice>> ListVoicesAsync(
        string apiKey,
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (!HasApiKey(apiKey))
        {
            LastError = "Enter an ElevenLabs API key first.";
            return Array.Empty<ElevenLabsVoice>();
        }

        string root = NormalizeBaseUrl(baseUrl);

        try
        {
            (HttpStatusCode status, string body) = await SendJsonAsync(
                HttpMethod.Get,
                root + "/v1/voices",
                apiKey,
                cancellationToken).ConfigureAwait(false);

            if (status == HttpStatusCode.NotFound)
            {
                (status, body) = await SendJsonAsync(
                    HttpMethod.Get,
                    root + "/v2/voices?page_size=100",
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!IsSuccess(status))
            {
                LastError = DescribeFailure(status);
                return Array.Empty<ElevenLabsVoice>();
            }

            return ParseVoices(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = $"Could not reach ElevenLabs: {ex.Message}";
            return Array.Empty<ElevenLabsVoice>();
        }
    }

    /// <summary>
    /// Synthesizes one utterance and returns 16-bit mono PCM. ElevenLabs answers
    /// with raw PCM at the requested rate; a RIFF header is accepted too so a
    /// proxy or a test double can reply with WAV.
    /// </summary>
    public async Task<ElevenLabsAudio> SynthesizePcmAsync(
        string apiKey,
        string baseUrl,
        string voiceId,
        string modelId,
        string text,
        double speed = 1.0,
        double stability = 0.45,
        double similarityBoost = 0.8,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(apiKey))
            throw new ElevenLabsException("ElevenLabs API key is not configured.");

        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ElevenLabsException("Choose an ElevenLabs voice first.");

        if (string.IsNullOrWhiteSpace(text))
            return new ElevenLabsAudio(Array.Empty<byte>(), PcmSampleRate);

        string root = NormalizeBaseUrl(baseUrl);
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"{root}/v1/text-to-speech/{Uri.EscapeDataString(voiceId.Trim())}?output_format={OutputFormat}");

        object voiceSettings = BuildVoiceSettings(speed, stability, similarityBoost, modelId);

        var payload = new Dictionary<string, object?>(4)
        {
            ["text"] = text.Trim(),
            ["model_id"] = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim(),
            ["voice_settings"] = voiceSettings
        };

        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("accept", "audio/pcm");

        try
        {
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new ElevenLabsException(
                    DescribeFailure(response.StatusCode, detail),
                    response.StatusCode);
            }

            byte[] audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (audio.Length == 0)
                throw new ElevenLabsException("ElevenLabs returned an empty audio stream.", response.StatusCode);

            if (LooksLikeRiff(audio))
            {
                if (!PiperTtsEngine.TryReadPcm16Mono(audio, out byte[] wavPcm, out int wavRate))
                    throw new ElevenLabsException("ElevenLabs returned a WAV the player cannot read.");

                return new ElevenLabsAudio(wavPcm, wavRate);
            }

            // Raw PCM16LE mono at 24 kHz. Drop a stray odd byte so NAudio never
            // misaligns the sample stream.
            int usable = audio.Length - (audio.Length % 2);
            if (usable != audio.Length)
                Array.Resize(ref audio, usable);

            return new ElevenLabsAudio(audio, PcmSampleRate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ElevenLabsException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ElevenLabsException($"Could not reach ElevenLabs: {ex.Message}", 0, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElevenLabsException("ElevenLabs timed out after 30 seconds.", 0, ex);
        }
    }

    private static object BuildVoiceSettings(
        double speed,
        double stability,
        double similarityBoost,
        string modelId)
    {
        // eleven_multilingual_v2 does not accept the speed field; v2.5/v3 do.
        bool supportsSpeed = modelId.Contains("v2_5", StringComparison.OrdinalIgnoreCase)
                             || modelId.Contains("v3", StringComparison.OrdinalIgnoreCase);

        var settings = new Dictionary<string, object>(5)
        {
            ["stability"] = Math.Clamp(stability, 0d, 1d),
            ["similarity_boost"] = Math.Clamp(similarityBoost, 0d, 1d),
            ["style"] = 0d,
            ["use_speaker_boost"] = true
        };

        if (supportsSpeed)
            settings["speed"] = Math.Clamp(speed, 0.7d, 1.2d);

        return settings;
    }

    private static IReadOnlyList<ElevenLabsVoice> ParseVoices(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("voices", out JsonElement voices)
            || voices.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ElevenLabsVoice>();
        }

        var parsed = new List<ElevenLabsVoice>(voices.GetArrayLength());
        foreach (JsonElement voice in voices.EnumerateArray())
        {
            string id = ReadString(voice, "voice_id");
            if (id.Length == 0)
                continue;

            string accent = ReadString(voice, "labels", "accent");
            string gender = ReadString(voice, "labels", "gender");
            string label = accent.Length > 0 && gender.Length > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{gender} {accent}")
                : accent.Length > 0 ? accent : gender;

            parsed.Add(new ElevenLabsVoice(
                id,
                ReadString(voice, "name"),
                ReadString(voice, "category"),
                label));
        }

        parsed.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return parsed;
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;

        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static string ReadString(JsonElement element, string parent, string property)
    {
        if (!element.TryGetProperty(parent, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return ReadString(nested, property);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendJsonAsync(
        HttpMethod method,
        string url,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("accept", "application/json");

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length <= 400 ? body : body[..400];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string DescribeFailure(HttpStatusCode status, string detail = "") => status switch
    {
        HttpStatusCode.Unauthorized => "ElevenLabs rejected the API key (401). Paste a valid key in AI VOICE.",
        HttpStatusCode.PaymentRequired => "ElevenLabs quota exhausted (402). Check the plan or the character budget.",
        HttpStatusCode.TooManyRequests => "ElevenLabs rate limit hit (429). Try again in a moment.",
        HttpStatusCode.UnprocessableEntity => $"ElevenLabs rejected the request (422). {TrimDetail(detail)}".TrimEnd(),
        HttpStatusCode.NotFound => "ElevenLabs voice or endpoint not found (404). Refresh the voice list.",
        _ => $"ElevenLabs error {(int)status}: {TrimDetail(detail)}".TrimEnd()
    };

    private static string TrimDetail(string detail)
    {
        string trimmed = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180];
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static bool LooksLikeRiff(byte[] audio) =>
        audio.Length >= 4
        && audio[0] == (byte)'R'
        && audio[1] == (byte)'I'
        && audio[2] == (byte)'F'
        && audio[3] == (byte)'F';

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        string root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        return root.TrimEnd('/');
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsClient)
            _http.Dispose();
    }
}
