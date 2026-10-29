// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/Remote/ColabPairCode.cs
// PURPOSE: One-line handshake between the Colab notebook and the workstation.
//          The notebook prints "BBAOI1|<tunnel>|<key>"; the app accepts that
//          line, a "<tunnel>#<key>" shorthand or a bare tunnel URL, so the
//          operator pastes once instead of copying two fields.
//
// NOTE: parsing is pure and allocation-light; it never touches the network.
//       The endpoint still goes through ColabSession.NormalizeBaseUrl so a
//       pasted value cannot smuggle a non-HTTP scheme into the tutor.
// ============================================================================

using System;

namespace BreadboardAOI.Intelligence.Remote;

/// <summary>Formats and parses the Colab pairing line.</summary>
public static class ColabPairCode
{
    /// <summary>Format marker so a stray paste is rejected instead of probed.</summary>
    public const string Version = "BBAOI1";

    /// <summary>Renders the canonical single line for a target.</summary>
    public static string Format(RemoteTutorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return string.IsNullOrWhiteSpace(target.ApiKey)
            ? $"{Version}|{target.EndpointUrl}"
            : $"{Version}|{target.EndpointUrl}|{target.ApiKey}";
    }

    /// <summary>
    /// Parses a pasted pairing line. Accepts <c>BBAOI1|url|key</c>,
    /// <c>BBAOI1|url</c>, <c>url#key</c>, <c>url?key=...</c> and a bare URL.
    /// Surrounding quotes, backticks and whitespace are ignored.
    /// </summary>
    public static bool TryParse(string? text, out RemoteTutorTarget target) =>
        TryParse(text, out target, out _);

    /// <summary>
    /// Same as <see cref="TryParse(string?, out RemoteTutorTarget)"/> but explains a
    /// rejection so the workstation can tell the operator why a paste was refused.
    /// </summary>
    public static bool TryParse(string? text, out RemoteTutorTarget target, out string? rejection)
    {
        target = null!;
        rejection = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string value = text.Trim().Trim('\uFEFF', '"', '\'', '`')
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (value.Length == 0)
            return false;

        string? endpoint = null;
        string? key = null;

        if (value.StartsWith(Version, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = value.Split('|');
            if (parts.Length < 2)
                return false;

            endpoint = parts[1];
            if (parts.Length > 2)
                key = string.Join("|", parts[2..]);
        }
        else if (value.Contains('|', StringComparison.Ordinal))
        {
            // "url|key" without the marker: still an unambiguous two-field paste.
            string[] parts = value.Split('|');
            if (parts.Length != 2)
                return false;

            endpoint = parts[0];
            key = parts[1];
        }
        else
        {
            endpoint = value;
        }

        int hash = endpoint.IndexOf('#');
        if (hash >= 0)
        {
            key ??= endpoint[(hash + 1)..];
            endpoint = endpoint[..hash];
        }

        if (key is null)
        {
            int query = endpoint.IndexOf("?key=", StringComparison.OrdinalIgnoreCase);
            if (query >= 0)
            {
                key = endpoint[(query + 5)..];
                endpoint = endpoint[..query];
            }
        }

        string? baseUrl = ColabSession.NormalizeBaseUrl(endpoint);
        if (baseUrl is null)
            return false;

        // A pair code chooses where the camera crops and the DRC report go, so a
        // cleartext tunnel is refused except on loopback (LM Studio / Ollama).
        if (!ColabEndpoints.IsSecureEndpoint(baseUrl))
        {
            rejection =
                "That pair code uses plain http to another machine. Use the https:// tunnel URL printed by Colab " +
                "(only 127.0.0.1 / localhost may use http) so frames are never sent in cleartext.";
            return false;
        }

        // The paste must not be able to point the app at a LAN host or at a cloud
        // metadata address (SSRF). Public custom domains are allowed; the dock
        // labels them so the operator can spot a typo.
        if (!ColabEndpoints.IsPublicHost(baseUrl))
        {
            rejection =
                "That pair code points at a private or link-local address. Use the public https:// tunnel URL " +
                "printed by Colab; the app refuses to send camera frames to a LAN or metadata endpoint.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            key = key.Trim();
            try
            {
                key = Uri.UnescapeDataString(key);
            }
            catch (UriFormatException)
            {
                // A stray '%' in a key is not worth failing the whole paste over.
            }

            if (key.Length == 0)
                key = null;
        }
        else
        {
            key = null;
        }

        target = new RemoteTutorTarget(baseUrl, key, null);
        return true;
    }
}
