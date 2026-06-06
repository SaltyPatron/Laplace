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

    [Fact]
    public async Task BillingCatalog_IncludesSynthesisAndAuditServices()
    {
        using var response = await _client.GetAsync("/v1/billing/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var serviceIds = json.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(e => e.GetProperty("service_id").GetString())
            .ToHashSet();

        Assert.Contains("synthesis", serviceIds);
        Assert.Contains("audit.report", serviceIds);
        Assert.Contains("inspect", serviceIds);
        Assert.Contains("recipe.publish", serviceIds);
        Assert.Contains("visualization.export", serviceIds);
        Assert.Contains("explain.trace", serviceIds);
        Assert.Contains("audit.deep_report", serviceIds);
        Assert.Contains("visualization.deep_export", serviceIds);
        Assert.Contains("recipe.compile", serviceIds);
        Assert.Contains("recipe.export", serviceIds);
        Assert.Contains("plan.developer", serviceIds);
        Assert.Contains("plan.studio", serviceIds);
        Assert.Contains("plan.enterprise", serviceIds);
    }

    [Fact]
    public async Task BillingPlans_ReturnRecurringBundles()
    {
        using var response = await _client.GetAsync("/v1/billing/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var plans = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(plans, p => p.GetProperty("plan_id").GetString() == "developer");
        Assert.Contains(plans, p => p.GetProperty("plan_id").GetString() == "studio");
        Assert.Contains(plans, p => p.GetProperty("plan_id").GetString() == "enterprise");

        var studio = plans.Single(p => p.GetProperty("plan_id").GetString() == "studio");
        Assert.Equal("plan.studio", studio.GetProperty("service_id").GetString());
        Assert.True(studio.GetProperty("monthly_price_cents").GetInt64() > 0);
        Assert.True(studio.GetProperty("monthly_credits").TryGetProperty("synthesis", out _));
    }

    [Fact]
    public async Task SynthesisQuote_MissingDimensions_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/synthesis/quote", new
        {
            vocab_size = 0,
            hidden_size = 0,
            num_layers = 0,
            num_heads = 0
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SynthesisQuote_ScalesWithDimensionality()
    {
        using var smallResponse = await _client.PostAsJsonAsync("/v1/billing/synthesis/quote", new
        {
            vocab_size = 32000,
            hidden_size = 512,
            num_layers = 4,
            num_heads = 8,
            intermediate_size = 2048
        });
        using var largeResponse = await _client.PostAsJsonAsync("/v1/billing/synthesis/quote", new
        {
            vocab_size = 128000,
            hidden_size = 4096,
            num_layers = 32,
            num_heads = 32,
            intermediate_size = 14336
        });

        Assert.Equal(HttpStatusCode.OK, smallResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, largeResponse.StatusCode);

        using var small = JsonDocument.Parse(await smallResponse.Content.ReadAsStringAsync());
        using var large = JsonDocument.Parse(await largeResponse.Content.ReadAsStringAsync());

        Assert.Equal("synthesis", small.RootElement.GetProperty("service_id").GetString());
        var smallParams = small.RootElement.GetProperty("estimated_parameters").GetInt64();
        var largeParams = large.RootElement.GetProperty("estimated_parameters").GetInt64();
        var smallAmount = small.RootElement.GetProperty("amount_cents").GetInt64();
        var largeAmount = large.RootElement.GetProperty("amount_cents").GetInt64();

        Assert.True(largeParams > smallParams);
        // Metered by dimensionality: the bigger mold must cost more.
        Assert.True(largeAmount > smallAmount);
        // Base job fee floors every synthesis quote above the per-unit meter alone.
        Assert.True(smallAmount > small.RootElement.GetProperty("billable_units").GetInt64());
    }

    [Fact]
    public async Task ExplainQuote_AcademicTierCostsMoreThanShallowTrace()
    {
        using var shallowResponse = await _client.PostAsJsonAsync("/v1/billing/explain/quote", new
        {
            prompt = "why is the sky blue",
            depth = 4,
            beam = 4,
            academic = false
        });
        using var academicResponse = await _client.PostAsJsonAsync("/v1/billing/explain/quote", new
        {
            prompt = "why is the sky blue",
            depth = 16,
            beam = 12,
            academic = true
        });

        Assert.Equal(HttpStatusCode.OK, shallowResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, academicResponse.StatusCode);

        using var shallow = JsonDocument.Parse(await shallowResponse.Content.ReadAsStringAsync());
        using var academic = JsonDocument.Parse(await academicResponse.Content.ReadAsStringAsync());

        Assert.Equal("explain.trace", shallow.RootElement.GetProperty("service_id").GetString());
        Assert.True(academic.RootElement.GetProperty("academic").GetBoolean());

        var shallowNodes = shallow.RootElement.GetProperty("estimated_trace_nodes").GetInt64();
        var academicNodes = academic.RootElement.GetProperty("estimated_trace_nodes").GetInt64();
        var shallowAmount = shallow.RootElement.GetProperty("amount_cents").GetInt64();
        var academicAmount = academic.RootElement.GetProperty("amount_cents").GetInt64();

        Assert.True(academicNodes > shallowNodes);
        // Deeper/wider trace + academic provenance expansion must cost more.
        Assert.True(academicAmount > shallowAmount);
    }

    [Fact]
    public async Task ExplainQuote_MissingDepth_ReturnsBadRequest()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/explain/quote", new
        {
            prompt = "anything",
            depth = 0,
            beam = 0
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuditQuote_FullAcademicReportCostsMoreThanSummary()
    {
        using var summaryResponse = await _client.PostAsJsonAsync("/v1/billing/audit/quote", new
        {
            scope = "summary",
            include_evidence = false,
            include_consensus = true,
            include_convergence = false,
            academic = false
        });
        using var academicResponse = await _client.PostAsJsonAsync("/v1/billing/audit/quote", new
        {
            scope = "full",
            include_evidence = true,
            include_consensus = true,
            include_convergence = true,
            academic = true
        });

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, academicResponse.StatusCode);

        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        using var academic = JsonDocument.Parse(await academicResponse.Content.ReadAsStringAsync());

        Assert.Equal("audit.deep_report", summary.RootElement.GetProperty("service_id").GetString());
        Assert.True(academic.RootElement.GetProperty("metered_items").GetInt64() > summary.RootElement.GetProperty("metered_items").GetInt64());
        Assert.True(academic.RootElement.GetProperty("amount_cents").GetInt64() > summary.RootElement.GetProperty("amount_cents").GetInt64());
    }

    [Fact]
    public async Task VisualizationQuote_ScalesWithGraphSizeAndOverlays()
    {
        using var smallResponse = await _client.PostAsJsonAsync("/v1/billing/visualization/quote", new
        {
            nodes = 25,
            edges = 30,
            include_geometry = true,
            include_evidence = false,
            interactive = false
        });
        using var largeResponse = await _client.PostAsJsonAsync("/v1/billing/visualization/quote", new
        {
            nodes = 500,
            edges = 1200,
            include_geometry = true,
            include_evidence = true,
            interactive = true
        });

        Assert.Equal(HttpStatusCode.OK, smallResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, largeResponse.StatusCode);

        using var small = JsonDocument.Parse(await smallResponse.Content.ReadAsStringAsync());
        using var large = JsonDocument.Parse(await largeResponse.Content.ReadAsStringAsync());

        Assert.Equal("visualization.deep_export", small.RootElement.GetProperty("service_id").GetString());
        Assert.True(large.RootElement.GetProperty("metered_items").GetInt64() > small.RootElement.GetProperty("metered_items").GetInt64());
        Assert.True(large.RootElement.GetProperty("amount_cents").GetInt64() > small.RootElement.GetProperty("amount_cents").GetInt64());
    }

    [Fact]
    public async Task RecipeQuote_CoversCompileAndPrivateExportMeters()
    {
        using var compileResponse = await _client.PostAsJsonAsync("/v1/billing/recipe/quote", new
        {
            action = "compile",
            content_items = 250,
            commercial = false,
            private_export = false
        });
        using var exportResponse = await _client.PostAsJsonAsync("/v1/billing/recipe/quote", new
        {
            action = "export",
            content_items = 2500,
            commercial = true,
            private_export = true
        });

        Assert.Equal(HttpStatusCode.OK, compileResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);

        using var compile = JsonDocument.Parse(await compileResponse.Content.ReadAsStringAsync());
        using var export = JsonDocument.Parse(await exportResponse.Content.ReadAsStringAsync());

        Assert.Equal("recipe.compile", compile.RootElement.GetProperty("service_id").GetString());
        Assert.Equal("recipe.export", export.RootElement.GetProperty("service_id").GetString());
        Assert.True(export.RootElement.GetProperty("amount_cents").GetInt64() > compile.RootElement.GetProperty("amount_cents").GetInt64());
    }
}
