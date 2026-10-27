// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/GoogleTokenStore.cs
// PURPOSE: Persists the Google grant and the OAuth client registration under
//          %LOCALAPPDATA%\BreadboardAOI. Writes are atomic (temp + replace) and
//          malformed files are ignored, so a broken session never blocks startup.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Cached OAuth grant for the linked Google account.</summary>
public sealed class GoogleTokenStore
{
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
        try
        {
            if (!File.Exists(FilePath))
                return null;

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            GoogleTokenSet? tokens = JsonSerializer.Deserialize<GoogleTokenSet>(json, Options);
            return tokens is { AccessToken.Length: > 0 } ? tokens : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Save(GoogleTokenSet tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        try
        {
            string directory = Path.GetDirectoryName(FilePath) ?? GetDefaultDirectory();
            Directory.CreateDirectory(directory);

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(tokens, Options));

            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, destinationBackupFileName: null);
            else
                File.Move(temp, FilePath);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked token file is harmless; the next save replaces it.
        }
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
