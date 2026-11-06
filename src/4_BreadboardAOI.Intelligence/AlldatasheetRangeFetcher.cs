// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/AlldatasheetRangeFetcher.cs
// PURPOSE: Bandwidth-aware datasheet retrieval for 2 Mbps field connections.
//          Checks the local SQLite cache first; on a cache miss, resolves the
//          component's datasheet URL on alldatasheet.com and downloads only the
//          first 250 KB (Pages 1-4) via HTTP Range: bytes=0-255999 headers.
// ============================================================================

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace BreadboardAOI.Intelligence;

/// <summary>Result of a datasheet range fetch or SQLite cache lookup.</summary>
public sealed record DatasheetFetchResult(
    string PartNumber,
    string SourceUrl,
    byte[] PdfPrefixBytes,
    string? CachedNetlistJson,
    byte[]? CachedSchematicPng,
    bool FromCache,
    DateTimeOffset FetchedAtUtc)
{
    public int ByteCount => PdfPrefixBytes?.Length ?? 0;

    public string Sha256Hex => PdfPrefixBytes is { Length: > 0 }
        ? Convert.ToHexString(SHA256.HashData(PdfPrefixBytes)).ToLowerInvariant()
        : string.Empty;
}

/// <summary>Options for the Alldatasheet HTTP Range fetcher and SQLite cache.</summary>
public sealed class AlldatasheetFetcherOptions
{
    public const int DefaultRangeBytes = 250 * 1024; // 250 KB (Pages 1-4)
    public const string DefaultSearchBaseUrl = "https://www.alldatasheet.com/view.jsp?Searchword=";

    public string CacheDatabasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BreadboardAOI",
        "datasheet_cache.sqlite");

    public int MaxRangeBytes { get; init; } = DefaultRangeBytes;

    public string SearchBaseUrl { get; init; } = DefaultSearchBaseUrl;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BreadboardAOI/1.0 (+https://www.alldatasheet.com)";
}

/// <summary>
/// SQLite-backed HTTP Range datasheet fetcher optimized for 2 Mbps links.
/// </summary>
public sealed class AlldatasheetRangeFetcher : IDisposable
{
    private static readonly Regex PdfHrefRegex = new(
        @"href\s*=\s*[""'](?<url>(?:https?:)?//[^""']*?\.pdf[^""']*|(?:https?:)?//pdf1\.alldatasheet\.com/[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PreviewHrefRegex = new(
        @"href\s*=\s*[""'](?<url>(?:https?:)?//www\.alldatasheet\.com/datasheet-pdf/pdf/[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AlldatasheetFetcherOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private bool _dbInitialized;
    private bool _disposed;

    public AlldatasheetRangeFetcher(AlldatasheetFetcherOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new AlldatasheetFetcherOptions();
        _ownsHttp = handler is null;

        HttpMessageHandler effectiveHandler = handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true
        };

        _http = new HttpClient(effectiveHandler, disposeHandler: _ownsHttp)
        {
            Timeout = _options.RequestTimeout
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
    }

    public AlldatasheetFetcherOptions Options => _options;

    public string DatabasePath => _options.CacheDatabasePath;

    /// <summary>
    /// Normalizes a part number (e.g., "NE555P" -> "NE555P") for deterministic SQLite primary keys.
    /// </summary>
    public static string NormalizePartKey(string partNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partNumber);
        Span<char> buffer = stackalloc char[Math.Min(partNumber.Length, 64)];
        int written = 0;

        foreach (char c in partNumber.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                buffer[written++] = char.ToUpperInvariant(c);
                if (written == buffer.Length)
                    break;
            }
        }

        return written == 0 ? partNumber.Trim().ToUpperInvariant() : new string(buffer[..written]);
    }

    /// <summary>
    /// Checks SQLite cache first; on miss, fetches the first 250 KB (Pages 1-4) of the PDF
    /// from alldatasheet.com (or an explicit PDF URL) using HTTP Range headers and caches it.
    /// </summary>
    public async Task<DatasheetFetchResult> FetchOrGetCachedAsync(
        string partNumber,
        string? directPdfUrl = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string key = NormalizePartKey(partNumber);

        await EnsureDatabaseInitializedAsync(cancellationToken).ConfigureAwait(false);

        DatasheetFetchResult? cached = await TryReadCacheAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null && cached.PdfPrefixBytes.Length > 0)
            return cached;

        string targetUrl = !string.IsNullOrWhiteSpace(directPdfUrl)
            ? directPdfUrl.Trim()
            : await ResolveAlldatasheetPdfUrlAsync(key, cancellationToken).ConfigureAwait(false);

        byte[] pdfPrefix = await DownloadRangeBytesAsync(targetUrl, _options.MaxRangeBytes, cancellationToken)
            .ConfigureAwait(false);

        var result = new DatasheetFetchResult(
            PartNumber: key,
            SourceUrl: targetUrl,
            PdfPrefixBytes: pdfPrefix,
            CachedNetlistJson: null,
            CachedSchematicPng: null,
            FromCache: false,
            FetchedAtUtc: DateTimeOffset.UtcNow);

        await UpsertCacheAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Stores the cropped Typical Application schematic PNG and/or extracted Golden Netlist JSON
    /// alongside the cached 250 KB PDF prefix so subsequent inspections require zero network or VLM calls.
    /// </summary>
    public async Task SaveDerivedArtifactsAsync(
        string partNumber,
        byte[]? schematicPng,
        string? goldenNetlistJson,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string key = NormalizePartKey(partNumber);

        await EnsureDatabaseInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE datasheet_cache
                SET schematic_png = COALESCE($schematic, schematic_png),
                    netlist_json  = COALESCE($netlist, netlist_json),
                    updated_utc   = $updated
                WHERE part_number = $part;
                """;
            command.Parameters.AddWithValue("$part", key);
            command.Parameters.AddWithValue("$schematic", (object?)schematicPng ?? DBNull.Value);
            command.Parameters.AddWithValue("$netlist", (object?)goldenNetlistJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>
    /// Downloads only the first <paramref name="maxBytes"/> (default 250 KB) of a PDF using
    /// HTTP <c>Range: bytes=0-255999</c>. If the origin server ignores Range and streams a
    /// 200 OK response, aborts the stream cleanly at <paramref name="maxBytes"/> so a 2 Mbps
    /// connection never stalls on multi-megabyte datasheets.
    /// </summary>
    public async Task<byte[]> DownloadRangeBytesAsync(
        string pdfUrl,
        int maxBytes = AlldatasheetFetcherOptions.DefaultRangeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfUrl);
        int cap = Math.Clamp(maxBytes, 16 * 1024, 2 * 1024 * 1024);

        using var request = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
        request.Headers.Range = new RangeHeaderValue(0, cap - 1);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            throw new HttpRequestException(
                $"Datasheet range request to '{pdfUrl}' returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null,
                response.StatusCode);
        }

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var memory = new MemoryStream(cap);
            int totalRead = 0;

            while (totalRead < cap)
            {
                int toRead = Math.Min(buffer.Length, cap - totalRead);
                int read = await contentStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                    break;

                memory.Write(buffer, 0, read);
                totalRead += read;
            }

            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> ResolveAlldatasheetPdfUrlAsync(string partNumber, CancellationToken cancellationToken)
    {
        string searchUrl = _options.SearchBaseUrl + Uri.EscapeDataString(partNumber);

        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Match directPdf = PdfHrefRegex.Match(html);
        if (directPdf.Success)
            return NormalizeProtocolRelativeUrl(directPdf.Groups["url"].Value);

        Match previewPage = PreviewHrefRegex.Match(html);
        if (previewPage.Success)
        {
            string previewUrl = NormalizeProtocolRelativeUrl(previewPage.Groups["url"].Value);
            using var previewReq = new HttpRequestMessage(HttpMethod.Get, previewUrl);
            using HttpResponseMessage previewRes = await _http
                .SendAsync(previewReq, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (previewRes.IsSuccessStatusCode)
            {
                string previewHtml = await previewRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Match nestedPdf = PdfHrefRegex.Match(previewHtml);
                if (nestedPdf.Success)
                    return NormalizeProtocolRelativeUrl(nestedPdf.Groups["url"].Value);
            }

            // Rewrite /datasheet-pdf/pdf/<id>/<mfg>/<part>.html -> direct pdf1 endpoint fallback
            return previewUrl.Replace("/datasheet-pdf/pdf/", "/datasheet-pdf/download/", StringComparison.OrdinalIgnoreCase);
        }

        return $"https://pdf1.alldatasheet.com/datasheet-pdf/view/{Uri.EscapeDataString(partNumber)}.pdf";
    }

    private static string NormalizeProtocolRelativeUrl(string rawUrl)
    {
        string trimmed = WebUtility.HtmlDecode(rawUrl).Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "https:" + trimmed;
        if (trimmed.StartsWith("/", StringComparison.Ordinal))
            return "https://www.alldatasheet.com" + trimmed;
        return trimmed;
    }

    private async Task EnsureDatabaseInitializedAsync(CancellationToken cancellationToken)
    {
        if (_dbInitialized)
            return;

        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_dbInitialized)
                return;

            string? directory = Path.GetDirectoryName(_options.CacheDatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS datasheet_cache (
                    part_number   TEXT PRIMARY KEY NOT NULL,
                    source_url    TEXT NOT NULL,
                    pdf_prefix    BLOB NOT NULL,
                    schematic_png BLOB NULL,
                    netlist_json  TEXT NULL,
                    updated_utc   TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _dbInitialized = true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task<DatasheetFetchResult?> TryReadCacheAsync(string partNumber, CancellationToken cancellationToken)
    {
        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_url, pdf_prefix, schematic_png, netlist_json, updated_utc
                FROM datasheet_cache
                WHERE part_number = $part
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$part", partNumber);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            string sourceUrl = reader.GetString(0);
            byte[] pdfBytes = (byte[])reader.GetValue(1);
            byte[]? schematicPng = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
            string? netlistJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            string updatedRaw = reader.GetString(4);

            if (!DateTimeOffset.TryParse(updatedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset updatedUtc))
                updatedUtc = DateTimeOffset.UtcNow;

            return new DatasheetFetchResult(
                PartNumber: partNumber,
                SourceUrl: sourceUrl,
                PdfPrefixBytes: pdfBytes,
                CachedNetlistJson: netlistJson,
                CachedSchematicPng: schematicPng,
                FromCache: true,
                FetchedAtUtc: updatedUtc);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task UpsertCacheAsync(DatasheetFetchResult item, CancellationToken cancellationToken)
    {
        await _dbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO datasheet_cache (part_number, source_url, pdf_prefix, schematic_png, netlist_json, updated_utc)
                VALUES ($part, $url, $pdf, $schematic, $netlist, $updated)
                ON CONFLICT(part_number) DO UPDATE SET
                    source_url    = excluded.source_url,
                    pdf_prefix    = excluded.pdf_prefix,
                    schematic_png = COALESCE(excluded.schematic_png, datasheet_cache.schematic_png),
                    netlist_json  = COALESCE(excluded.netlist_json, datasheet_cache.netlist_json),
                    updated_utc   = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$part", item.PartNumber);
            command.Parameters.AddWithValue("$url", item.SourceUrl);
            command.Parameters.AddWithValue("$pdf", item.PdfPrefixBytes);
            command.Parameters.AddWithValue("$schematic", (object?)item.CachedSchematicPng ?? DBNull.Value);
            command.Parameters.AddWithValue("$netlist", (object?)item.CachedNetlistJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", item.FetchedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.CacheDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsHttp)
            _http.Dispose();
        _dbGate.Dispose();
    }
}
