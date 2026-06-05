using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class EndpointContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public EndpointContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("F-scaffold", json.RootElement.GetProperty("stream").GetString());
    }

    [Fact]
    public async Task Capabilities_ExposeLiveAndPendingStatus()
    {
        using var response = await _client.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = json.RootElement.GetProperty("endpoints");
        Assert.Equal("live", endpoints.GetProperty("chat_completions").GetProperty("status").GetString());
        Assert.Equal("live", endpoints.GetProperty("completions").GetProperty("status").GetString());
        Assert.Equal("pending", endpoints.GetProperty("embeddings").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ChatCompletions_MissingModel_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v1/chat/completions", new
        {
            messages = new[] { new { role = "user", content = "hello" } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Completions_MissingPrompt_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v1/completions", new
        {
            model = "laplace-completions-001"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Embeddings_MissingInput_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "laplace-embeddings-pending"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatCompletions_ValidPayloadWithoutQuote_ReturnsPaymentRequired()
    {
        using var response = await _client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "hello" } }
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task BillingPreflight_ReturnsQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "chat.completions",
            units = 1,
            tenant = "test-tenant"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("quote_id").GetString()));
        Assert.Equal("chat.completions", json.RootElement.GetProperty("service_id").GetString());
    }
}
