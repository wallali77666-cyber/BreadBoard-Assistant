// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/RemoteColabTests.cs
// PURPOSE: Pins the pure remote-AI contracts: tunnel URL arithmetic, backend
//          selection, session JSON (shared with the notebook), PKCE and the
//          on-disk grant store. No network access is required.
// ============================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using BreadboardAOI.Intelligence;
using BreadboardAOI.Intelligence.Remote;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class RemoteColabTests
{
    [Theory]
    [InlineData("https://abc.trycloudflare.com", "https://abc.trycloudflare.com/v1/chat/completions")]
    [InlineData("https://abc.trycloudflare.com/", "https://abc.trycloudflare.com/v1/chat/completions")]
    [InlineData("https://abc.trycloudflare.com/v1", "https://abc.trycloudflare.com/v1/chat/completions")]
    [InlineData("https://abc.trycloudflare.com/v1/chat/completions", "https://abc.trycloudflare.com/v1/chat/completions")]
    [InlineData("abc.trycloudflare.com", "https://abc.trycloudflare.com/v1/chat/completions")]
    [InlineData("  http://127.0.0.1:8089  ", "http://127.0.0.1:8089/v1/chat/completions")]
    public void Chat_endpoint_is_derived_from_any_useful_form(string input, string expected)
    {
        ColabEndpoints.BuildChatEndpoint(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    public void Unusable_endpoints_are_rejected(string? input)
    {
        ColabEndpoints.BuildChatEndpoint(input).Should().BeNull();
        ColabEndpoints.BuildHealthEndpoint(input).Should().BeNull();
    }

    [Theory]
    [InlineData("https://abc.trycloudflare.com", "https://abc.trycloudflare.com/health")]
    [InlineData("https://abc.trycloudflare.com/v1/chat/completions", "https://abc.trycloudflare.com/health")]
    [InlineData("http://127.0.0.1:8089/v1", "http://127.0.0.1:8089/health")]
    public void Health_endpoint_strips_the_openai_path(string input, string expected)
    {
        ColabEndpoints.BuildHealthEndpoint(input).Should().Be(expected);
    }

    [Fact]
    public void Host_description_is_safe_for_the_status_dock()
    {
        ColabEndpoints.DescribeHost("https://abc-def.trycloudflare.com/v1").Should().Be("abc-def.trycloudflare.com");
        ColabEndpoints.DescribeHost(null).Should().Be("NO ENDPOINT");
    }

    [Theory]
    [InlineData(true, true, true, true, RemoteTutorRoute.Remote)]
    [InlineData(true, false, true, true, RemoteTutorRoute.Local)]
    [InlineData(true, true, false, true, RemoteTutorRoute.Local)]
    [InlineData(false, true, true, true, RemoteTutorRoute.Local)]
    [InlineData(true, true, true, false, RemoteTutorRoute.Remote)]
    [InlineData(false, false, false, false, RemoteTutorRoute.Offline)]
    public void Route_selection_prefers_remote_then_local_then_offline(
        bool preferRemote,
        bool remoteServing,
        bool remoteHealthy,
        bool localAvailable,
        RemoteTutorRoute expected)
    {
        RemoteTutorSelector.Select(preferRemote, remoteServing, remoteHealthy, localAvailable)
            .Should().Be(expected);
    }

    [Fact]
    public void Session_round_trips_through_the_shared_notebook_json()
    {
        var session = new ColabSession
        {
            Status = ColabSession.ServingStatus,
            EndpointUrl = "https://unit-test.trycloudflare.com/",
            ApiKey = "bbaoi_unit",
            Model = "Qwen3-VL-8B-Instruct-Q4_K_M.gguf",
            Gpu = "Tesla T4",
            Detail = "test",
            UpdatedUtc = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)
        };

        string json = ColabSessionJson.Serialize(session);

        // The notebook writes camelCase keys; the app must agree.
        json.Should().Contain("\"endpointUrl\"");
        json.Should().Contain("\"apiKey\"");
        json.Should().Contain("\"updatedUtc\"");

        ColabSession? parsed = ColabSessionJson.Deserialize(json);
        parsed.Should().NotBeNull();
        parsed!.Status.Should().Be(ColabSession.ServingStatus);
        parsed.ApiKey.Should().Be("bbaoi_unit");
        parsed.Model.Should().Be("Qwen3-VL-8B-Instruct-Q4_K_M.gguf");
        parsed.IsServing.Should().BeTrue();
    }

    [Fact]
    public void Notebook_written_session_without_paths_is_not_serving()
    {
        const string NotebookJson = """
        { "status": "serving", "endpointUrl": "", "apiKey": "bbaoi_x" }
        """;

        ColabSession? session = ColabSessionJson.Deserialize(NotebookJson);

        session.Should().NotBeNull();
        session!.IsServing.Should().BeFalse();
        RemoteTutorTarget.FromSession(session).Should().BeNull();
    }

    [Fact]
    public void Corrupt_session_files_are_ignored()
    {
        ColabSessionJson.Deserialize("{not json").Should().BeNull();
        ColabSessionJson.Deserialize("").Should().BeNull();
        ColabSessionJson.Deserialize(null).Should().BeNull();
    }

    [Fact]
    public void Serving_session_converts_into_a_remote_target()
    {
        var session = new ColabSession
        {
            Status = ColabSession.ServingStatus,
            EndpointUrl = "https://abc.trycloudflare.com",
            ApiKey = "bbaoi_key",
            Model = "qwen3-vl-8b"
        };

        RemoteTutorTarget? target = RemoteTutorTarget.FromSession(session);

        target.Should().NotBeNull();
        target!.ChatEndpoint.Should().Be("https://abc.trycloudflare.com/v1/chat/completions");
        target.DisplayHost.Should().Be("abc.trycloudflare.com");
        target.ApiKey.Should().Be("bbaoi_key");
        target.ModelAlias.Should().Be("qwen3-vl-8b");
    }

    [Fact]
    public void Pkce_challenge_matches_the_rfc7636_vector()
    {
        GoogleOAuthClient.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void Authorization_url_carries_pkce_offline_access_and_state()
    {
        var options = new GoogleOAuthOptions
        {
            ClientId = "unit.apps.googleusercontent.com",
            Scopes = GoogleOAuthOptions.DefaultScopes
        };

        using var client = new GoogleOAuthClient(options, new StubHandler());
        string url = client.BuildAuthorizationUrl(
            "http://127.0.0.1:51234/oauth2/callback",
            "state-123",
            "challenge-abc");

        url.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth?");
        url.Should().Contain("client_id=unit.apps.googleusercontent.com");
        url.Should().Contain("response_type=code");
        url.Should().Contain("code_challenge_method=S256");
        url.Should().Contain("code_challenge=challenge-abc");
        url.Should().Contain("access_type=offline");
        url.Should().Contain("state=state-123");
        url.Should().Contain(Uri.EscapeDataString("http://127.0.0.1:51234/oauth2/callback"));
        url.Should().Contain(Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file"));
    }

    [Fact]
    public async Task Unconfigured_client_refuses_to_start_the_flow()
    {
        using var client = new GoogleOAuthClient(new GoogleOAuthOptions(), new StubHandler());

        Func<System.Threading.Tasks.Task> act = () => client.AuthorizeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OAuth client*");
    }

    [Fact]
    public void Restores_remote_endpoint_without_launching_a_local_server()
    {
        var local = new QwenMoEOptions { AutoLaunchServer = true };

        QwenMoEOptions remote = local.WithRemoteEndpoint(
            "https://abc.trycloudflare.com/v1/chat/completions",
            "bbaoi_key",
            "qwen3-vl-8b");

        remote.AutoLaunchServer.Should().BeFalse();
        remote.EndpointUrl.Should().Be("https://abc.trycloudflare.com/v1/chat/completions");
        remote.ApiKey.Should().Be("bbaoi_key");
        remote.ModelAlias.Should().Be("qwen3-vl-8b");
        remote.RequestTimeoutSeconds.Should().BeGreaterOrEqualTo(60d);

        // The local path is untouched.
        local.AutoLaunchServer.Should().BeTrue();
    }

    [Fact]
    public void Google_grant_survives_a_store_round_trip_and_can_be_cleared()
    {
        string path = Path.Combine(Path.GetTempPath(), "breadboardaoi-tests", Guid.NewGuid().ToString("N"), "session.json");
        var store = new GoogleTokenStore(path);

        try
        {
            store.Load().Should().BeNull();

            var tokens = new GoogleTokenSet
            {
                AccessToken = "access-1",
                RefreshToken = "refresh-1",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                Email = "maker@example.com",
                DisplayName = "Maker"
            };

            store.Save(tokens).Should().BeTrue();
            File.Exists(path).Should().BeTrue();

            GoogleTokenSet? loaded = store.Load();
            loaded.Should().NotBeNull();
            loaded!.AccessToken.Should().Be("access-1");
            loaded.RefreshToken.Should().Be("refresh-1");
            loaded.DescribeAccount().Should().Be("Maker <maker@example.com>");

            store.Clear();
            store.Load().Should().BeNull();
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Expired_grants_are_detected_with_a_two_minute_skew()
    {
        var tokens = new GoogleTokenSet
        {
            AccessToken = "a",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        };

        tokens.IsExpired(DateTimeOffset.UtcNow).Should().BeTrue();

        var fresh = tokens with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) };
        fresh.IsExpired(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Corrupt_grant_files_do_not_throw()
    {
        string path = Path.Combine(Path.GetTempPath(), "breadboardaoi-tests", Guid.NewGuid().ToString("N"), "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken");

        try
        {
            new GoogleTokenStore(path).Load().Should().BeNull();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>No test may reach the network; Google calls are never issued here.</summary>
    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotImplemented));
    }
}
