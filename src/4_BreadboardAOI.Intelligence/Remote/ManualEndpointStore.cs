// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/ManualEndpointStore.cs
// PURPOSE: Remembers the last accepted Colab pair code so a restart reconnects
//          without another paste. The bearer key stays on this machine under
//          %LOCALAPPDATA%\BreadboardAOI; writes are atomic and a corrupt file is
//          ignored rather than blocking startup.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Persists the manually paired tunnel endpoint and its bearer key.</summary>
public sealed class ManualEndpointStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ManualEndpointStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(GoogleTokenStore.GetDefaultDirectory(), "remote-endpoint.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public RemoteTutorTarget? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            ManualEndpointFile? file = JsonSerializer.Deserialize<ManualEndpointFile>(json, Options);
            if (file is null || string.IsNullOrWhiteSpace(file.EndpointUrl))
                return null;

            string? endpoint = ColabSession.NormalizeBaseUrl(file.EndpointUrl);
            if (endpoint is null)
                return null;

            string? key = string.IsNullOrWhiteSpace(file.ApiKey) ? null : file.ApiKey.Trim();
            return new RemoteTutorTarget(endpoint, key, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Save(RemoteTutorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            string directory = Path.GetDirectoryName(FilePath) ?? GoogleTokenStore.GetDefaultDirectory();
            Directory.CreateDirectory(directory);

            var file = new ManualEndpointFile
            {
                EndpointUrl = target.EndpointUrl,
                ApiKey = target.ApiKey,
                SavedAtUtc = DateTimeOffset.UtcNow
            };

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));

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
            // Best effort: a locked file must not break the disconnect command.
        }
    }

    private sealed class ManualEndpointFile
    {
        public string? EndpointUrl { get; set; }

        public string? ApiKey { get; set; }

        public DateTimeOffset SavedAtUtc { get; set; }
    }
}
