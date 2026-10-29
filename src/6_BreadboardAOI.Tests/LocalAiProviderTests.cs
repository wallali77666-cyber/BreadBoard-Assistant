using System;
using System.IO;
using BreadboardAOI.Intelligence.Remote;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

/// <summary>
/// The local-AI path reuses the Colab plumbing, so these facts pin down the
/// discovery rules and the persisted model alias that make LM Studio and Ollama
/// connect without a tunnel or a download.
/// </summary>
public sealed class LocalAiProviderTests
{
    [Fact]
    public void Model_list_accepts_the_openai_data_array()
    {
        const string json = """
            {
              "object": "list",
              "data": [
                { "id": "qwen2.5-vl-7b-instruct", "object": "model" },
                { "id": "llama-3.2-11b-vision", "object": "model" }
              ]
            }
            """;

        LocalAiProviders.ParseModelIds(json)
            .Should().Equal("llama-3.2-11b-vision", "qwen2.5-vl-7b-instruct");
    }

    [Fact]
    public void Model_list_rejects_junk_without_throwing()
    {
        LocalAiProviders.ParseModelIds(null).Should().BeEmpty();
        LocalAiProviders.ParseModelIds("not json").Should().BeEmpty();
        LocalAiProviders.ParseModelIds("""{ "data": "nope" }""").Should().BeEmpty();
        LocalAiProviders.ParseModelIds("""{ "data": [ { "id": 7 } ] }""").Should().BeEmpty();
    }

    [Fact]
    public void Choose_model_prefers_exact_then_prefix_then_first()
    {
        var models = new[] { "llama-3.2-11b-vision", "qwen2.5-vl-7b-instruct-q4" };

        LocalAiProviders.ChooseModel(models, "qwen2.5-vl-7b-instruct-q4")
            .Should().Be("qwen2.5-vl-7b-instruct-q4");

        LocalAiProviders.ChooseModel(models, "qwen2.5-vl")
            .Should().Be("qwen2.5-vl-7b-instruct-q4");

        LocalAiProviders.ChooseModel(models, "missing")
            .Should().Be("llama-3.2-11b-vision");

        LocalAiProviders.ChooseModel(Array.Empty<string>(), null).Should().BeNull();
    }

    [Fact]
    public void Presets_are_addressable_and_point_at_loopback()
    {
        LocalAiProviders.Find("LMSTUDIO")!.BaseUrl.Should().Be("http://127.0.0.1:1234");
        LocalAiProviders.Find("ollama")!.ApiKey.Should().Be("ollama");
        LocalAiProviders.Find("llamacpp")!.ChatEndpoint.Should().Be("http://127.0.0.1:8089/v1/chat/completions");
        LocalAiProviders.Find(" ").Should().BeNull();
        LocalAiProviders.Find("not-a-provider").Should().BeNull();

        foreach (LocalAiPreset preset in LocalAiProviders.All)
            ColabEndpoints.IsLoopback(preset.BaseUrl).Should().BeTrue(preset.Id);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234", true)]
    [InlineData("http://localhost:11434", true)]
    [InlineData("https://127.0.0.1:8443", true)]
    [InlineData("https://random-words.trycloudflare.com", false)]
    [InlineData(null, false)]
    [InlineData("nonsense", false)]
    public void Loopback_detection_labels_routes_without_changing_them(string? url, bool expected)
    {
        ColabEndpoints.IsLoopback(url).Should().Be(expected);
    }

    [Fact]
    public void Loopback_target_reports_its_route_and_chat_endpoint()
    {
        var target = new RemoteTutorTarget("http://127.0.0.1:11434", "ollama", "qwen2.5-vl:7b");

        target.IsLoopback.Should().BeTrue();
        target.DisplayHost.Should().Be("127.0.0.1:11434");
        target.ChatEndpoint.Should().Be("http://127.0.0.1:11434/v1/chat/completions");
    }

    [Fact]
    public void Endpoint_store_round_trips_the_model_alias_and_key()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "remote-endpoint.json");

        try
        {
            var store = new ManualEndpointStore(path);
            var target = new RemoteTutorTarget("http://127.0.0.1:1234", "secret-key", "qwen2.5-vl-7b-instruct");

            store.Save(target).Should().BeTrue();
            File.Exists(path).Should().BeTrue();

            RemoteTutorTarget? loaded = store.Load();
            loaded.Should().NotBeNull();
            loaded!.EndpointUrl.Should().Be("http://127.0.0.1:1234");
            loaded.ApiKey.Should().Be("secret-key");
            loaded.ModelAlias.Should().Be("qwen2.5-vl-7b-instruct");
            loaded.IsLoopback.Should().BeTrue();

            store.Clear();
            File.Exists(path).Should().BeFalse();
            store.Load().Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_endpoint_file_is_ignored_instead_of_blocking_startup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "remote-endpoint.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not json");

            new ManualEndpointStore(path).Load().Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
