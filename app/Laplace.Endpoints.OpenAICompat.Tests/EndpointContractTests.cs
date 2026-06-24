using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class EndpointContractTests : IClassFixture<SignedWebhookFactory>
{
    private readonly SignedWebhookFactory _factory;
    private readonly HttpClient _client;

    public EndpointContractTests(SignedWebhookFactory factory)
    {
        _factory = factory;
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
    public async Task HealthReady_WhenSubstrateUnreachable_Returns503AndDoesNotLie()
    {
        // This factory wires the real SubstrateClient with no DB behind it. Readiness must
        // report 503 + substrate_reachable=false rather than a hollow "ok". Liveness stays 200.
        using var live = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var response = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.False(json.RootElement.GetProperty("substrate_reachable").GetBoolean());
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
        Assert.Equal("live", endpoints.GetProperty("audit_reports").GetProperty("status").GetString());
        Assert.Equal("live", endpoints.GetProperty("visualizations").GetProperty("status").GetString());
        Assert.Equal("live", endpoints.GetProperty("explainability_reports").GetProperty("status").GetString());
        Assert.Equal("live", endpoints.GetProperty("embeddings").GetProperty("status").GetString());
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
    public async Task PaidReportEndpoints_WithoutQuote_ReturnPaymentRequired()
    {
        using var audit = await _client.PostAsJsonAsync("/v1/audit/report", new
        {
            scope = "summary"
        });
        using var visualization = await _client.PostAsJsonAsync("/v1/visualizations/substrate", new
        {
            limit = 10,
            include_geometry = true
        });
        using var explain = await _client.PostAsJsonAsync("/v1/explain/report", new
        {
            prompt = "why is the sky blue",
            depth = 3,
            beam = 2,
            academic = false
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, audit.StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, visualization.StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, explain.StatusCode);
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
    public async Task PlanSubscribe_ReturnsPlanQuote()
    {
        using var response = await _client.PostAsJsonAsync("/v1/billing/plans/studio/subscribe", new
        {
            tenant = "plan-tenant"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("quote_id").GetString()));
        Assert.Equal("studio", json.RootElement.GetProperty("plan_id").GetString());
        Assert.Equal("plan.studio", json.RootElement.GetProperty("service_id").GetString());
        Assert.True(json.RootElement.GetProperty("monthly_credits").TryGetProperty("synthesis", out _));
    }

    [Fact]
    public async Task StripeWebhook_ApprovesQuote()
    {
        using var quoteResponse = await _client.PostAsJsonAsync("/v1/billing/preflight", new
        {
            service_id = "audit.report",
            units = 1,
            tenant = "webhook-quote-tenant"
        });
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);

        using var quoteJson = JsonDocument.Parse(await quoteResponse.Content.ReadAsStringAsync());
        var quoteId = quoteJson.RootElement.GetProperty("quote_id").GetString();
        var payload = JsonSerializer.Serialize(new
        {
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_quote",
                    customer = "cus_test_quote",
                    subscription = (string?)null,
                    metadata = new
                    {
                        tenant = "webhook-quote-tenant",
                        service_id = "audit.report",
                        quote_id = quoteId
                    }
                }
            }
        });

        using var webhookResponse = await PostStripeWebhookAsync(payload);
        Assert.Equal(HttpStatusCode.OK, webhookResponse.StatusCode);

        using var fetchedQuote = await _client.GetAsync($"/v1/billing/quotes/{quoteId}");
        Assert.Equal(HttpStatusCode.OK, fetchedQuote.StatusCode);

        using var fetchedJson = JsonDocument.Parse(await fetchedQuote.Content.ReadAsStringAsync());
        Assert.Equal("approved", fetchedJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StripeWebhook_ActivatesPlanAndCreditsCanBeConsumed()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_plan",
                    customer = "cus_test_plan",
                    subscription = "sub_test_plan",
                    metadata = new
                    {
                        tenant,
                        service_id = "plan.studio",
                        quote_id = "q_test_plan"
                    }
                }
            }
        });

        using var webhookResponse = await PostStripeWebhookAsync(payload);
        Assert.Equal(HttpStatusCode.OK, webhookResponse.StatusCode);

        using var webhookJson = JsonDocument.Parse(await webhookResponse.Content.ReadAsStringAsync());
        Assert.Equal("plan_activated", webhookJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("studio", webhookJson.RootElement.GetProperty("plan_id").GetString());

        using var consumeResponse = await ConsumeAsync(tenant, "synthesis", 10);
        Assert.Equal(HttpStatusCode.OK, consumeResponse.StatusCode);

        using var consumeJson = JsonDocument.Parse(await consumeResponse.Content.ReadAsStringAsync());
        Assert.True(consumeJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("studio", consumeJson.RootElement.GetProperty("plan_id").GetString());
        Assert.Equal(490, consumeJson.RootElement.GetProperty("remaining").GetInt32());

        using var entitlementRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/billing/entitlements");
        entitlementRequest.Headers.Add("X-Laplace-Tenant", tenant);
        using var entitlementResponse = await _client.SendAsync(entitlementRequest);
        Assert.Equal(HttpStatusCode.OK, entitlementResponse.StatusCode);

        using var entitlementJson = JsonDocument.Parse(await entitlementResponse.Content.ReadAsStringAsync());
        var entitlement = entitlementJson.RootElement.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("studio", entitlement.GetProperty("plan_id").GetString());
        Assert.Equal(10, entitlement.GetProperty("used_credits").GetProperty("synthesis").GetInt32());
    }

    [Fact]
    public async Task StripeWebhook_DuplicateEventIsIdempotent()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            id = eventId,
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_duplicate",
                    customer = "cus_test_duplicate",
                    subscription = "sub_test_duplicate",
                    metadata = new
                    {
                        tenant,
                        service_id = "plan.developer",
                        quote_id = "q_test_duplicate"
                    }
                }
            }
        });

        using var first = await PostStripeWebhookAsync(payload);
        using var second = await PostStripeWebhookAsync(payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(eventId, json.RootElement.GetProperty("event_id").GetString());
    }

    [Fact]
    public async Task StripeWebhook_RenewsThenCancelsPlan()
    {
        var tenant = $"tenant-{Guid.NewGuid():N}";
        var subscription = $"sub_{Guid.NewGuid():N}";
        await PostStripeWebhookAsync(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_lifecycle",
                    customer = "cus_test_lifecycle",
                    subscription,
                    metadata = new { tenant, service_id = "plan.studio", quote_id = "q_test_lifecycle" }
                }
            }
        });
        using (var consume = await ConsumeAsync(tenant, "synthesis", 10))
        {
            Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        }

        using var renewal = await PostStripeWebhookAsync(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "invoice.paid",
            data = new
            {
                @object = new
                {
                    id = "in_test_lifecycle",
                    customer = "cus_test_lifecycle",
                    subscription,
                    metadata = new { tenant, service_id = "plan.studio" }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        using (var renewalJson = JsonDocument.Parse(await renewal.Content.ReadAsStringAsync()))
        {
            Assert.Equal("plan_renewed", renewalJson.RootElement.GetProperty("status").GetString());
        }

        using (var consumeAfterRenewal = await ConsumeAsync(tenant, "synthesis", 500))
        {
            Assert.Equal(HttpStatusCode.OK, consumeAfterRenewal.StatusCode);
            using var consumedJson = JsonDocument.Parse(await consumeAfterRenewal.Content.ReadAsStringAsync());
            Assert.Equal(0, consumedJson.RootElement.GetProperty("remaining").GetInt32());
        }

        using var cancel = await PostStripeWebhookAsync(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "customer.subscription.deleted",
            data = new
            {
                @object = new
                {
                    id = subscription,
                    metadata = new { tenant, service_id = "plan.studio" }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using (var cancelJson = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync()))
        {
            Assert.Equal("plan_canceled", cancelJson.RootElement.GetProperty("status").GetString());
        }

        using var blocked = await ConsumeAsync(tenant, "synthesis", 1);
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
    }

    [Fact]
    public async Task StripeWebhook_UnconfiguredSecret_FailsClosed()
    {
        await using var unconfigured = new UnconfiguredWebhookFactory();
        using var client = unconfigured.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new { @object = new { id = "cs_x", metadata = new { tenant = "attacker", service_id = "plan.studio" } } }
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", SignedWebhookFactory.Sign(payload));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("webhook_secret_unconfigured", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(json.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task StripeWebhook_InvalidSignature_IsRejected()
    {
        await using var strict = new StrictWebhookFactory();
        using var client = strict.CreateClient();

        var payload = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new { @object = new { id = "cs_x", metadata = new { tenant = "attacker", service_id = "plan.studio" } } }
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", "t=1700000000,v1=deadbeef");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_signature", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("accepted").GetBoolean());
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
        Assert.True(largeAmount > smallAmount);
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

    private async Task<HttpResponseMessage> PostStripeWebhookAsync(object payload) =>
        await PostStripeWebhookAsync(JsonSerializer.Serialize(payload));

    private async Task<HttpResponseMessage> PostStripeWebhookAsync(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", SignedWebhookFactory.Sign(payload));
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ConsumeAsync(string tenant, string serviceId, int units)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/entitlements/consume")
        {
            Content = JsonContent.Create(new { service_id = serviceId, units })
        };
        request.Headers.Add("X-Laplace-Tenant", tenant);
        return await _client.SendAsync(request);
    }
}
