// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/RemotePairingTests.cs
// PURPOSE: Pins the one-paste Colab handshake: the "BBAOI1|url|key" line the
//          notebook prints, its shorthand forms, and the on-disk store that
//          lets the app reconnect on the next start. No network access.
// ============================================================================

using System;
using System.IO;
using BreadboardAOI.Intelligence.Remote;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class RemotePairingTests
{
    [Fact]
    public void Full_pair_code_yields_endpoint_and_key()
    {
        ColabPairCode.TryParse(
            "BBAOI1|https://calm-otter-1234.trycloudflare.com|k3y-with-dashes_9",
            out RemoteTutorTarget target).Should().BeTrue();

        target.EndpointUrl.Should().Be("https://calm-otter-1234.trycloudflare.com");
        target.ApiKey.Should().Be("k3y-with-dashes_9");
        target.ChatEndpoint.Should().Be("https://calm-otter-1234.trycloudflare.com/v1/chat/completions");
    }

    [Fact]
    public void Pair_code_survives_quotes_newlines_and_padding()
    {
        string pasted = "  \uFEFF\"BBAOI1|https://calm-otter-1234.trycloudflare.com|abc123\"\n";

        ColabPairCode.TryParse(pasted, out RemoteTutorTarget target).Should().BeTrue();
        target.EndpointUrl.Should().Be("https://calm-otter-1234.trycloudflare.com");
        target.ApiKey.Should().Be("abc123");
    }

    [Theory]
    [InlineData("BBAOI1|https://calm-otter.trycloudflare.com", null)]
    [InlineData("https://calm-otter.trycloudflare.com#secret", "secret")]
    [InlineData("https://calm-otter.trycloudflare.com?key=secret", "secret")]
    [InlineData("https://calm-otter.trycloudflare.com|secret", "secret")]
    [InlineData("https://calm-otter.trycloudflare.com", null)]
    [InlineData("calm-otter.trycloudflare.com", null)]
    public void Shorthand_pastes_are_accepted(string text, string? expectedKey)
    {
        ColabPairCode.TryParse(text, out RemoteTutorTarget target).Should().BeTrue();
        target.EndpointUrl.Should().Be("https://calm-otter.trycloudflare.com");
        target.ApiKey.Should().Be(expectedKey);
    }

    [Fact]
    public void Url_encoded_keys_are_decoded()
    {
        ColabPairCode.TryParse("https://calm-otter.trycloudflare.com?key=a%2Bb%3Dc", out RemoteTutorTarget target)
            .Should().BeTrue();

        target.ApiKey.Should().Be("a+b=c");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BBAOI1")]
    [InlineData("BBAOI1|")]
    [InlineData("BBAOI1|not a host")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("a|b|c")]
    public void Nonsense_pastes_are_rejected(string? text)
    {
        ColabPairCode.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://192.168.1.20", false)]
    [InlineData("https://10.0.0.5:8089", false)]
    [InlineData("https://172.20.1.2", false)]
    [InlineData("https://169.254.169.254", false)]
    [InlineData("https://100.64.0.7", false)]
    [InlineData("https://box.local", false)]
    [InlineData("https://metadata.google.internal", false)]
    [InlineData("https://[fd12:3456::1]", false)]
    [InlineData("https://[fe80::1]", false)]
    [InlineData("https://[::1]", true)]
    [InlineData("https://127.0.0.1:8089", true)]
    [InlineData("http://localhost:1234", true)]
    [InlineData("https://calm-otter-1234.trycloudflare.com", true)]
    [InlineData("https://example.com", true)]
    public void Private_and_link_local_hosts_are_not_public(string url, bool expected)
    {
        ColabEndpoints.IsPublicHost(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://calm-otter-1234.trycloudflare.com", true)]
    [InlineData("https://abc.ngrok-free.app", true)]
    [InlineData("https://abc.gradio.live", true)]
    [InlineData("https://abc.loca.lt", true)]
    [InlineData("http://127.0.0.1:8089", true)]
    [InlineData("https://example.com", false)]
    public void Tunnel_hosts_are_recognised_for_the_custom_host_note(string url, bool expected)
    {
        ColabEndpoints.IsKnownTunnelHost(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("BBAOI1|https://192.168.1.20|key")]
    [InlineData("BBAOI1|https://169.254.169.254/latest/meta-data|key")]
    [InlineData("https://10.1.2.3#key")]
    public void Pair_codes_aimed_at_private_networks_are_rejected(string text)
    {
        ColabPairCode.TryParse(text, out _, out string? rejection).Should().BeFalse();
        rejection.Should().NotBeNullOrWhiteSpace();
        rejection.Should().Contain("private");
    }

    [Fact]
    public void A_public_custom_domain_is_accepted_but_not_labelled_as_a_tunnel()
    {
        ColabPairCode.TryParse("BBAOI1|https://tutor.example.edu|key", out RemoteTutorTarget target)
            .Should().BeTrue();

        target.EndpointUrl.Should().Be("https://tutor.example.edu");
        ColabEndpoints.IsKnownTunnelHost(target.EndpointUrl).Should().BeFalse();
    }

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var original = new RemoteTutorTarget("https://calm-otter.trycloudflare.com", "token-1", null);

        string line = ColabPairCode.Format(original);
        line.Should().Be("BBAOI1|https://calm-otter.trycloudflare.com|token-1");

        ColabPairCode.TryParse(line, out RemoteTutorTarget parsed).Should().BeTrue();
        parsed.EndpointUrl.Should().Be(original.EndpointUrl);
        parsed.ApiKey.Should().Be(original.ApiKey);
    }

    [Fact]
    public void A_keyless_target_formats_without_a_trailing_separator()
    {
        ColabPairCode.Format(new RemoteTutorTarget("http://127.0.0.1:8089", null, null))
            .Should().Be("BBAOI1|http://127.0.0.1:8089");
    }

    [Fact]
    public void Saved_endpoint_survives_a_restart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-pair-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "remote-endpoint.json");
        var store = new ManualEndpointStore(file);

        try
        {
            store.Load().Should().BeNull();

            store.Save(new RemoteTutorTarget("https://calm-otter.trycloudflare.com", "token-2", null))
                .Should().BeTrue();

            RemoteTutorTarget? loaded = store.Load();
            loaded.Should().NotBeNull();
            loaded!.EndpointUrl.Should().Be("https://calm-otter.trycloudflare.com");
            loaded.ApiKey.Should().Be("token-2");

            store.Clear();
            store.Load().Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_store_file_is_ignored()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "remote-endpoint.json");

        try
        {
            File.WriteAllText(file, "{ not json");
            new ManualEndpointStore(file).Load().Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
