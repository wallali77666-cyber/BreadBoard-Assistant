// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/GoogleTokenStore.cs
// PURPOSE: Persists the Google grant and the OAuth client registration under
//          %LOCALAPPDATA%\BreadboardAOI. Writes are atomic (temp + replace) and
//          malformed files are ignored, so a broken session never blocks startup.
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Cached OAuth grant for the linked Google account.</summary>
public sealed class GoogleTokenStore
{
    /// <summary>Marker written in front of the DPAPI blob so the file is self-describing.</summary>
    private static readonly byte[] DpapiHeader = "BBAOIDP1"u8.ToArray();

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(3);

    // Token refresh and the UI status pump touch this file from different threads;
    // one process-wide gate keeps File.Replace from colliding with itself.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public GoogleTokenStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultPath() : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public static string GetDefaultDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();

        return Path.Combine(root, "BreadboardAOI");
    }

    public static string GetDefaultPath() => Path.Combine(GetDefaultDirectory(), "google-session.json");

    public GoogleTokenSet? Load()
    {
        if (!Gate.Wait(GateTimeout))
            return null;

        try
        {
            if (!File.Exists(FilePath))
                return null;

            byte[] raw = File.ReadAllBytes(FilePath);
            if (raw.Length == 0)
                return null;

            byte[] json = raw;
            if (HasDpapiHeader(raw))
            {
                byte[]? plaintext = DpapiProtector.Unprotect(raw[DpapiHeader.Length..]);
                if (plaintext is null || plaintext.Length == 0)
                {
                    // Encrypted for a different Windows account (or tampered with):
                    // treat it as "not linked" instead of crashing the startup path.
                    return null;
                }

                json = plaintext;
            }

            string text = Encoding.UTF8.GetString(json);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            GoogleTokenSet? tokens = JsonSerializer.Deserialize<GoogleTokenSet>(text, Options);
            return tokens is { AccessToken.Length: > 0 } ? tokens : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    public bool Save(GoogleTokenSet tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (!Gate.Wait(GateTimeout))
            return false;

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(tokens, Options);

            // DPAPI is Windows-only; off Windows the grant is written as plain JSON
            // so the cross-platform tests still exercise the round trip.
            byte[] fileBytes = DpapiProtector.Protect(json) is { Length: > 0 } cipher
                ? ComposeDpapiFile(cipher)
                : json;

            string directory = Path.GetDirectoryName(FilePath) ?? GetDefaultDirectory();
            Directory.CreateDirectory(directory);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string temp = FilePath + ".tmp";
                    File.WriteAllBytes(temp, fileBytes);

                    if (File.Exists(FilePath))
                        File.Replace(temp, FilePath, destinationBackupFileName: null);
                    else
                        File.Move(temp, FilePath);

                    return true;
                }
                catch (IOException) when (attempt < 2)
                {
                    // A scanner or another thread held the file briefly; retry.
                    Thread.Sleep(30);
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    public void Clear()
    {
        if (!Gate.Wait(GateTimeout))
            return;

        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked token file is harmless; the next save replaces it.
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool HasDpapiHeader(ReadOnlySpan<byte> data) =>
        data.Length > DpapiHeader.Length && data[..DpapiHeader.Length].SequenceEqual(DpapiHeader);

    private static byte[] ComposeDpapiFile(byte[] cipher)
    {
        var file = new byte[DpapiHeader.Length + cipher.Length];
        DpapiHeader.CopyTo(file, 0);
        cipher.CopyTo(file, DpapiHeader.Length);
        return file;
    }
}

/// <summary>OAuth client registration entered once in the workstation.</summary>
public sealed record GoogleClientConfig(string ClientId, string? ClientSecret);

/// <summary>Small JSON file for the client ID/secret, kept apart from the grant.</summary>
public sealed class GoogleClientConfigStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public GoogleClientConfigStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(GoogleTokenStore.GetDefaultDirectory(), "google-client.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public GoogleClientConfig? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            GoogleClientConfig? config = JsonSerializer.Deserialize<GoogleClientConfig>(File.ReadAllText(FilePath), Options);
            return string.IsNullOrWhiteSpace(config?.ClientId) ? null : config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Save(GoogleClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? GoogleTokenStore.GetDefaultDirectory());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
