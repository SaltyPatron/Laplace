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

public sealed class FoundryEndpointTests : IClassFixture<FoundryTestFactory>
{
    private readonly HttpClient _client;

    private const string SampleRecipe = """
        {
          "kind": "laplace.recipe",
          "name": "test-recipe",
          "structure": "dense",
          "hidden_size": 256,
          "num_layers": 2,
          "rope": true,
          "tie_embeddings": false,
          "norm": "rmsnorm",
          "vocab": { "source": "crawl", "seeds": ["dog"], "hops": 1, "fanout": 8, "size": 128 },
          "embed": { "op": "coord" },
          "lm_head": { "op": "trajectory" },
          "compile": "continuation",
          "layers": [
            { "kv_heads": 2, "heads": [ {"op":"context"} ], "ffn": {"op":"unary"} },
            { "kv_heads": 2, "heads": [ {"op":"context"} ], "ffn": {"op":"unary"} }
          ]
        }
        """;

    public FoundryEndpointTests(FoundryTestFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task RecipeCompile_WithoutQuote_Returns402()
    {
        using var response = await _client.PostAsJsonAsync("/v1/recipe/compile", new { recipe = SampleRecipe });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task SynthesisExport_WithoutQuote_Returns402()
    {
        using var response = await _client.PostAsJsonAsync("/v1/synthesis/export", new { recipe = SampleRecipe });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task RecipeCompile_InvalidRecipe_Returns400()
    {
        var quoteId = await ApproveQuoteAsync("recipe.compile", "foundry-compile-tenant");
        using var response = await PostWithQuoteAsync("/v1/recipe/compile", new { recipe = "{}" }, quoteId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecipeCompile_ValidRecipe_ReturnsMetadata()
    {
        var quoteId = await ApproveQuoteAsync("recipe.compile", "foundry-compile-ok");
        using var response = await PostWithQuoteAsync("/v1/recipe/compile", new { recipe = SampleRecipe }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<RecipeCompileResponse>();
        Assert.NotNull(body);
        Assert.Equal("test-recipe", body!.Name);
        Assert.Equal(2, body.NumLayers);
        Assert.Equal("continuation", body.CompileMode);
        Assert.Equal(32, body.RecipeIdHex.Length);
        Assert.Equal("recipe.compile", body.Billing!.ServiceId);
    }

    [Fact]
    public async Task SynthesisExport_WithApprovedQuote_UsesFoundryStub()
    {
        var quoteId = await ApproveQuoteAsync("synthesis", "foundry-export-tenant");
        using var response = await PostWithQuoteAsync("/v1/synthesis/export", new { recipe = SampleRecipe }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SynthesisExportResponse>();
        Assert.NotNull(body);
        Assert.Equal("gguf", body!.Format);
        Assert.True(body.Bytes > 0);
        Assert.Equal("synthesis", body.Billing!.ServiceId);
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

    private async Task<HttpResponseMessage> PostWithQuoteAsync(string path, object payload, string quoteId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-Laplace-Quote-Id", quoteId);
        return await _client.SendAsync(request);
    }
}

internal sealed class StubFoundryExportService : IFoundryExportService
{
    public Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-stub-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(path, [0x47, 0x47, 0x55, 0x46]); // GGUF magic stub
        return Task.FromResult(new FoundryExportResult(path, 4, format));
    }
}

public sealed class FoundryTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.RemoveAll<IFoundryExportService>();
            services.AddSingleton<IFoundryExportService, StubFoundryExportService>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
                o.Bypass = false;
            });
        });
}
