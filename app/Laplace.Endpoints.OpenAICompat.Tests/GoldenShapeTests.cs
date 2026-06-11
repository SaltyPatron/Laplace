using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Wire-shape goldens for every route. These pin the EXACT serialized form served today —
/// including the mixed casing reality (snake_case response wrappers, camelCase embedded
/// records) — so the typed-contract conversion provably changes nothing on the wire.
/// Substrate-backed routes run against <see cref="FakeSubstrateClient"/>.
/// </summary>
public sealed class GoldenShapeTests : IClassFixture<GoldenFactory>
{
    private readonly HttpClient _client;

    public GoldenShapeTests(GoldenFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ---- core ----

    [Fact]
    public async Task Golden_Health()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("health", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Models()
    {
        using var response = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("models", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Capabilities()
    {
        using var response = await _client.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("capabilities", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownApiRoute_ReturnsJson404_NotSpaHtml()
    {
        using var response = await _client.GetAsync("/v1/no/such/route");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_route", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PrometheusMetrics_AreServed()
    {
        using var response = await _client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("http_server", body);
    }

    [Fact]
    public async Task OpenApiDocument_IsServed()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.", json.RootElement.GetProperty("openapi").GetString());
        Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/v1/chat/completions", out _));
    }

    // ---- chat/completions ----

    [Fact]
    public async Task Golden_Chat_InvalidJson()
    {
        using var response = await _client.PostAsync("/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("chat-invalid-json-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_MissingModel()
    {
        using var response = await _client.PostAsJsonAsync("/v1/chat/completions", new
        {
            messages = new[] { new { role = "user", content = "hello" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("chat-missing-model-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_NoQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        GoldenJson.Match("chat-no-quote-402", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_PendingQuote()
    {
        var quoteId = await CreateQuoteAsync("chat.completions", "golden-pending-tenant");
        using var response = await PostWithQuoteAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        }, quoteId);
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        GoldenJson.Match("chat-pending-quote-402", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_Converse()
    {
        var quoteId = await ApproveQuoteAsync("chat.completions", "golden-chat-tenant", "evt_golden_chat");
        using var response = await PostWithQuoteAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("chat-converse-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_Converse_Sse()
    {
        var quoteId = await ApproveQuoteAsync("chat.completions", "golden-chat-sse-tenant", "evt_golden_chat_sse");
        using var response = await PostWithQuoteAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            stream = true,
            messages = new[] { new { role = "user", content = "what is a whale" } }
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType?.ToString());
        GoldenJson.MatchNode("chat-converse-sse", await ReadSseAsync(response));
    }

    [Fact]
    public async Task Golden_Chat_Generate()
    {
        var quoteId = await ApproveQuoteAsync("chat.completions", "golden-gen-tenant", "evt_golden_gen");
        using var response = await PostWithQuoteAsync("/v1/chat/completions", new
        {
            model = "laplace-completions-001",
            max_tokens = 3,
            messages = new[] { new { role = "user", content = "the whale" } }
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("chat-generate-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Chat_Generate_Sse()
    {
        var quoteId = await ApproveQuoteAsync("chat.completions", "golden-gen-sse-tenant", "evt_golden_gen_sse");
        using var response = await PostWithQuoteAsync("/v1/chat/completions", new
        {
            model = "laplace-completions-001",
            stream = true,
            max_tokens = 3,
            messages = new[] { new { role = "user", content = "the whale" } }
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.MatchNode("chat-generate-sse", await ReadSseAsync(response));
    }

    // ---- completions ----

    [Fact]
    public async Task Golden_Completions_MissingPrompt()
    {
        using var response = await _client.PostAsJsonAsync("/v1/completions", new
        {
            model = "laplace-completions-001"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("completions-missing-prompt-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Completions_NoQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/completions", new
        {
            model = "laplace-completions-001",
            prompt = "the whale"
        });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        GoldenJson.Match("completions-no-quote-402", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Completions()
    {
        var quoteId = await ApproveQuoteAsync("completions", "golden-cmpl-tenant", "evt_golden_cmpl");
        using var response = await PostWithQuoteAsync("/v1/completions", new
        {
            model = "laplace-completions-001",
            prompt = "the whale",
            max_tokens = 3
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("completions-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Completions_Logprobs()
    {
        var quoteId = await ApproveQuoteAsync("completions", "golden-cmpl-lp-tenant", "evt_golden_cmpl_lp");
        using var response = await PostWithQuoteAsync("/v1/completions", new
        {
            model = "laplace-completions-001",
            prompt = "the whale",
            max_tokens = 3,
            logprobs = 1
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("completions-200-logprobs", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Completions_Sse()
    {
        var quoteId = await ApproveQuoteAsync("completions", "golden-cmpl-sse-tenant", "evt_golden_cmpl_sse");
        using var response = await PostWithQuoteAsync("/v1/completions", new
        {
            model = "laplace-completions-001",
            prompt = "the whale",
            max_tokens = 3,
            stream = true,
            logprobs = 1
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.MatchNode("completions-sse", await ReadSseAsync(response));
    }

    // ---- embeddings ----

    [Fact]
    public async Task Golden_Embeddings_MissingInput()
    {
        using var response = await _client.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "laplace-embeddings-pending"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("embeddings-missing-input-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Embeddings_NotImplemented()
    {
        using var response = await _client.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "laplace-embeddings-pending",
            input = "whale"
        });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        GoldenJson.Match("embeddings-501", await response.Content.ReadAsStringAsync());
    }

    // ---- evidence ----

    [Fact]
    public async Task Golden_Evidence()
    {
        using var response = await _client.GetAsync("/v1/evidence/whale?limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("evidence-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Evidence_NotFound()
    {
        using var response = await _client.GetAsync("/v1/evidence/unknown-word");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        GoldenJson.Match("evidence-404", await response.Content.ReadAsStringAsync());
    }

    // ---- reports ----

    [Fact]
    public async Task Golden_Audit_NoQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/audit/report", new { scope = "summary" });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        GoldenJson.Match("audit-no-quote-402", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Audit_Report()
    {
        var quoteId = await ApproveQuoteAsync("audit.deep_report", "golden-audit-tenant", "evt_golden_audit");
        using var response = await PostWithQuoteAsync("/v1/audit/report", new
        {
            scope = "full",
            include_evidence = true,
            include_consensus = true,
            include_convergence = true,
            academic = true
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("audit-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Visualization()
    {
        var quoteId = await ApproveQuoteAsync("visualization.deep_export", "golden-viz-tenant", "evt_golden_viz");
        using var response = await PostWithQuoteAsync("/v1/visualizations/substrate", new
        {
            limit = 10,
            include_geometry = true,
            include_evidence = true
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("viz-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Explain_InvalidDepth()
    {
        using var response = await _client.PostAsJsonAsync("/v1/explain/report", new
        {
            prompt = "whale",
            depth = 0,
            beam = 0
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("explain-invalid-depth-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Explain_Report()
    {
        var quoteId = await ApproveQuoteAsync("explain.trace", "golden-explain-tenant", "evt_golden_explain");
        using var response = await PostWithQuoteAsync("/v1/explain/report", new
        {
            prompt = "whale",
            depth = 2,
            beam = 2,
            academic = true
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("explain-200", await response.Content.ReadAsStringAsync());
    }

    // ---- billing reads ----

    [Fact]
    public async Task Golden_Billing_Catalog()
    {
        using var response = await _client.GetAsync("/v1/billing/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("billing-catalog", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Billing_Products()
    {
        using var response = await _client.GetAsync("/v1/billing/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("billing-products", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Billing_Plans()
    {
        using var response = await _client.GetAsync("/v1/billing/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("billing-plans", await response.Content.ReadAsStringAsync());
    }

    // ---- billing flows ----

    [Fact]
    public async Task Golden_Preflight()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "chat.completions",
            units = 1,
            tenant = "golden-preflight-tenant"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("preflight-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Preflight_UnknownService()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "no.such.service",
            units = 1,
            tenant = "golden-preflight-tenant"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("preflight-unknown-service-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_PlanSubscribe()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/plans/studio/subscribe", new
        {
            tenant = "golden-plan-tenant"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("plan-subscribe-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_PlanSubscribe_Unknown()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/plans/no-such-plan/subscribe", new
        {
            tenant = "golden-plan-tenant"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        GoldenJson.Match("plan-subscribe-unknown-400", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Webhook_ApproveQuote()
    {
        var quoteId = await CreateQuoteAsync("audit.report", "golden-webhook-tenant");
        using var response = await PostStripeWebhookAsync(WebhookEnvelope(
            "evt_golden_webhook", "golden-webhook-tenant", "audit.report", quoteId, subscription: null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("webhook-approve-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Quote_Lifecycle()
    {
        var quoteId = await ApproveQuoteAsync("audit.report", "golden-quote-tenant", "evt_golden_quote");
        using var fetched = await _client.GetAsync($"/v1/billing/quotes/{quoteId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        GoldenJson.Match("quote-get-200", await fetched.Content.ReadAsStringAsync());

        using var missing = await _client.GetAsync("/v1/billing/quotes/q_does_not_exist");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        GoldenJson.Match("quote-not-found-400", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Entitlements_And_Consume()
    {
        const string tenant = "golden-entitlement-tenant";
        using var activate = await PostStripeWebhookAsync(WebhookEnvelope(
            "evt_golden_entitlement", tenant, "plan.studio", "q_golden_plan", subscription: "sub_golden_entitlement"));
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        using var entitlements = await GetWithTenantAsync("/v1/billing/entitlements", tenant);
        Assert.Equal(HttpStatusCode.OK, entitlements.StatusCode);
        GoldenJson.Match("entitlements-200", await entitlements.Content.ReadAsStringAsync());

        using var consume = await PostWithTenantAsync("/v1/billing/entitlements/consume",
            new { service_id = "synthesis", units = 10 }, tenant);
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        GoldenJson.Match("consume-200", await consume.Content.ReadAsStringAsync());

        using var denied = await PostWithTenantAsync("/v1/billing/entitlements/consume",
            new { service_id = "synthesis", units = 100000 }, tenant);
        Assert.Equal(HttpStatusCode.PaymentRequired, denied.StatusCode);
        GoldenJson.Match("consume-402", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_CatalogSync_Unconfigured()
    {
        using var response = await _client.PostAsync("/v1/billing/catalog/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("catalog-sync-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_Usage()
    {
        const string tenant = "golden-usage-tenant";
        var quoteId = await ApproveQuoteAsync("completions", tenant, "evt_golden_usage");
        using var execute = await PostWithQuoteAsync("/v1/completions", new
        {
            model = "laplace-completions-001",
            prompt = "the whale",
            max_tokens = 3
        }, quoteId);
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);

        using var usage = await GetWithTenantAsync("/v1/billing/usage", tenant);
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        GoldenJson.Match("usage-200", await usage.Content.ReadAsStringAsync());
    }

    // ---- quote calculators ----

    [Fact]
    public async Task Golden_SynthesisQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/synthesis/quote", new
        {
            tenant = "golden-synthq-tenant",
            vocab_size = 32000,
            hidden_size = 512,
            num_layers = 4,
            num_heads = 8,
            intermediate_size = 2048
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("synthesis-quote-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_ExplainQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/explain/quote", new
        {
            tenant = "golden-explainq-tenant",
            prompt = "why is the sky blue",
            depth = 4,
            beam = 4,
            academic = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("explain-quote-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_AuditQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/audit/quote", new
        {
            tenant = "golden-auditq-tenant",
            scope = "full",
            include_evidence = true,
            include_consensus = true,
            include_convergence = true,
            academic = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("audit-quote-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_VisualizationQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/visualization/quote", new
        {
            tenant = "golden-vizq-tenant",
            nodes = 100,
            edges = 250,
            include_geometry = true,
            include_evidence = true,
            interactive = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("visualization-quote-200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Golden_RecipeQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/recipe/quote", new
        {
            tenant = "golden-recipeq-tenant",
            action = "export",
            content_items = 2500,
            commercial = true,
            private_export = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GoldenJson.Match("recipe-quote-200", await response.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private async Task<string> CreateQuoteAsync(string serviceId, string tenant)
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = serviceId,
            units = 1,
            tenant
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("quote_id").GetString()!;
    }

    private async Task<string> ApproveQuoteAsync(string serviceId, string tenant, string eventId)
    {
        var quoteId = await CreateQuoteAsync(serviceId, tenant);
        using var webhook = await PostStripeWebhookAsync(WebhookEnvelope(eventId, tenant, serviceId, quoteId, subscription: null));
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        return quoteId;
    }

    private static string WebhookEnvelope(string eventId, string tenant, string serviceId, string quoteId, string? subscription) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = $"cs_{eventId}",
                    customer = $"cus_{eventId}",
                    subscription,
                    metadata = new { tenant, service_id = serviceId, quote_id = quoteId }
                }
            }
        });

    private async Task<HttpResponseMessage> PostStripeWebhookAsync(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", SignedWebhookFactory.Sign(payload));
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostWithQuoteAsync(string path, object payload, string quoteId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Laplace-Quote-Id", quoteId);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetWithTenantAsync(string path, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Laplace-Tenant", tenant);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostWithTenantAsync(string path, object payload, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Laplace-Tenant", tenant);
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Collect an SSE body as a JSON array: each data: payload parsed as a node,
    /// the terminal [DONE] sentinel kept as a string.
    /// </summary>
    private static async Task<JsonArray> ReadSseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var events = new JsonArray();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..].Trim();
            if (payload == "[DONE]")
                events.Add("[DONE]");
            else
                events.Add(JsonNode.Parse(payload));
        }
        return events;
    }
}
