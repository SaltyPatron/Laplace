using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laplace.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>Explore billing gates with bypass disabled (does not inherit env.cmd bypass).</summary>
public sealed class ExploreBillingTests : IClassFixture<ExploreBillingFactory>
{
    private readonly HttpClient _client;

    public ExploreBillingTests(ExploreBillingFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ExploreEntity_WithoutQuote_Returns402()
    {
        using var resolve = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        var hit = await resolve.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        using var response = await _client.GetAsync($"/v1/explore/entities/{hit!.IdHex}");
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task ExploreExport_WithoutQuote_Returns402()
    {
        using var resolve = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        var hit = await resolve.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        using var response = await _client.PostAsJsonAsync(
            $"/v1/explore/entities/{hit!.IdHex}/export",
            new { include_members = true, include_peers = false });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task Preflight_Inspect_ReturnsQuoteId()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "inspect",
            units = 1,
            tenant = "explore-billing-tenant"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("quote_id").GetString()));
        Assert.Equal("inspect", json.RootElement.GetProperty("service_id").GetString());
    }

    [Fact]
    public async Task Preflight_RecipeExport_ReturnsQuoteId()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "recipe.export",
            units = 250,
            tenant = "explore-export-tenant"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("quote_id").GetString()));
        Assert.Equal("recipe.export", json.RootElement.GetProperty("service_id").GetString());
    }

    [Fact]
    public async Task ExploreEntity_WithApprovedQuote_Returns200AndBillingReceipt()
    {
        using var resolve = await _client.GetAsync("/v1/explore/resolve?reference=whale");
        var hit = await resolve.Content.ReadFromJsonAsync<ExploreResolveResponse>();
        var quoteId = await ApproveQuoteAsync("inspect", "explore-inspect-tenant");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/explore/entities/{hit!.IdHex}");
        request.Headers.Add("X-Laplace-Quote-Id", quoteId);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(json.RootElement.GetProperty("billing").GetProperty("quote_id").GetString());
        Assert.Equal("inspect", json.RootElement.GetProperty("billing").GetProperty("service_id").GetString());
    }

    private async Task<string> ApproveQuoteAsync(string serviceId, string tenant)
    {
        using var preflight = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = serviceId,
            units = 1,
            tenant
        });
        using var preJson = JsonDocument.Parse(await preflight.Content.ReadAsStringAsync());
        var quoteId = preJson.RootElement.GetProperty("quote_id").GetString()!;
        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{serviceId}_{tenant}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = $"cs_{tenant}",
                    customer = $"cus_{tenant}",
                    subscription = (string?)null,
                    metadata = new { tenant, service_id = serviceId, quote_id = quoteId }
                }
            }
        });
        using var webhook = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        webhook.Headers.Add("Stripe-Signature", SignedWebhookFactory.Sign(payload));
        using var wh = await _client.SendAsync(webhook);
        Assert.Equal(HttpStatusCode.OK, wh.StatusCode);
        return quoteId;
    }
}

public sealed class ExploreBillingFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
                o.Bypass = false;
            });
        });
}
