using System.Net;
using System.Text.Json;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class CapabilitiesForwardBackendTests : IClassFixture<SignedWebhookFactory>
{
    private readonly HttpClient _client;

    public CapabilitiesForwardBackendTests(SignedWebhookFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Capabilities_AdvertiseCanonicalForwardProgramNotLegacyRecallOrConsensusCompletion()
    {
        using var response = await _client.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = json.RootElement.GetProperty("endpoints");
        var chatBackend = endpoints.GetProperty("chat_completions").GetProperty("backend").GetString();
        var completionsBackend = endpoints.GetProperty("completions").GetProperty("backend").GetString();

        Assert.Equal("converse.forward_turn -> generation.forward_program (native)", chatBackend);
        Assert.Equal("converse.forward_turn -> generation.forward_program (native streaming)", completionsBackend);
        Assert.DoesNotContain("recall_session", chatBackend ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.completions", completionsBackend ?? string.Empty, StringComparison.Ordinal);
    }
}
