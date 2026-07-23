using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task Issue_Validate_Revoke_Roundtrip()
    {
        var service = new ApiKeyService(new InMemoryApiKeyStore());

        var issued = await service.IssueAsync("acme", "test", CancellationToken.None);
        Assert.StartsWith(ApiKeyService.KeyPrefix, issued.Key);
        Assert.Equal("acme", issued.Record.Tenant);

        var validated = await service.ValidateAsync(issued.Key, CancellationToken.None);
        Assert.NotNull(validated);
        Assert.Equal("acme", validated!.Tenant);

        Assert.Null(await service.ValidateAsync(ApiKeyService.KeyPrefix + "not-a-real-key", CancellationToken.None));
        Assert.Null(await service.ValidateAsync("sk-other-vendor", CancellationToken.None));

        Assert.True(await service.RevokeByPrefixAsync("acme", issued.Record.KeyPrefix, CancellationToken.None));
        Assert.Null(await service.ValidateAsync(issued.Key, CancellationToken.None));
    }
}

/// <summary>key-mode composition: enforcement middleware + operator endpoints live.</summary>
public sealed class KeyModeFactory : WebApplicationFactory<Program>
{
    public const string OperatorToken = "op_test_token";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.RemoveAll<IHostedService>();
            services.RemoveAll<TurnWitness>();
            services.AddSingleton<TurnWitness>(sp =>
            {
                var witness = new TurnWitness(
                    sp.GetRequiredService<SubstrateClient>(),
                    sp.GetRequiredService<ILogger<TurnWitness>>());
                witness.TestForceAvailable = true;
                return witness;
            });
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.Bypass = true;
            });
            services.PostConfigure<LaplaceAuthOptions>(o =>
            {
                o.Mode = "key";
                o.OperatorToken = OperatorToken;
            });
        });
}

public sealed class KeyModeEnforcementTests : IClassFixture<KeyModeFactory>
{
    private readonly KeyModeFactory _factory;

    public KeyModeEnforcementTests(KeyModeFactory factory) => _factory = factory;

    [Fact]
    public async Task Protected_Route_Requires_Key()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("api_key_required", body);
    }

    [Fact]
    public async Task Anonymous_Signup_Surface_Stays_Open()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/billing/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/billing/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task Invalid_Key_Rejected_Even_On_Open_Route()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiKeyService.KeyPrefix + "bogus");
        using var response = await client.GetAsync("/v1/billing/plans");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_api_key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Operator_Issues_Key_Then_Key_Resolves_Tenant()
    {
        using var client = _factory.CreateClient();

        using var forbidden = await client.PostAsJsonAsync("/v1/billing/operator/keys", new { tenant = "acme" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var issueRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/operator/keys")
        {
            Content = JsonContent.Create(new { tenant = "acme", label = "ci" })
        };
        issueRequest.Headers.Add(OperatorAuth.TokenHeader, KeyModeFactory.OperatorToken);
        using var issued = await client.SendAsync(issueRequest);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        using var issuedDoc = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var apiKey = issuedDoc.RootElement.GetProperty("api_key").GetString();
        Assert.StartsWith(ApiKeyService.KeyPrefix, apiKey);

        using var keyed = new HttpRequestMessage(HttpMethod.Get, "/v1/billing/keys");
        keyed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var listed = await client.SendAsync(keyed);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedDoc = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal("acme", listedDoc.RootElement.GetProperty("tenant").GetString());
        Assert.Equal(1, listedDoc.RootElement.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public async Task Operator_Bootstrap_Reports_State_Without_Stripe()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/operator/bootstrap");
        request.Headers.Add(OperatorAuth.TokenHeader, KeyModeFactory.OperatorToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("memory", doc.RootElement.GetProperty("store_mode").GetString());
        Assert.False(doc.RootElement.GetProperty("stripe_configured").GetBoolean());
        Assert.Equal("stripe_not_configured",
            doc.RootElement.GetProperty("webhook").GetProperty("status").GetString());
    }
}

public sealed class PlanCreditGateTests : IClassFixture<GoldenFactory>
{
    private readonly GoldenFactory _factory;

    public PlanCreditGateTests(GoldenFactory factory) => _factory = factory;

    private BillingPlan Plan(string planId)
    {
        var catalog = _factory.Services.GetRequiredService<IBillingCatalog>();
        return catalog.ListPlans().Single(p => p.PlanId == planId);
    }

    [Fact]
    public async Task Flat_Service_Covered_By_Plan_Credits_Without_Quote()
    {
        var tenant = "credit-tenant-flat";
        var entitlements = _factory.Services.GetRequiredService<IBillingEntitlementStore>();
        await entitlements.ActivatePlanAsync(tenant, Plan("developer"), null, "sub_test_flat", DateTimeOffset.UtcNow, CancellationToken.None);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTenantResolver.TenantHeader, tenant);
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await entitlements.GetByTenantAsync(tenant, CancellationToken.None);
        Assert.Equal(1, after.Single().UsedCredits["chat.completions"]);

        var usage = await _factory.Services.GetRequiredService<IBillingOrchestrator>()
            .GetUsageAsync(tenant, CancellationToken.None);
        Assert.Single(usage);
        Assert.Equal(0, usage[0].AmountCents);
    }

    [Fact]
    public async Task Metered_Quote_Redeemed_By_Plan_Credits()
    {
        var tenant = "credit-tenant-metered";
        var entitlements = _factory.Services.GetRequiredService<IBillingEntitlementStore>();
        await entitlements.ActivatePlanAsync(tenant, Plan("studio"), null, "sub_test_metered", DateTimeOffset.UtcNow, CancellationToken.None);

        var billing = _factory.Services.GetRequiredService<IBillingOrchestrator>();
        var quote = await billing.CreatePreflightQuoteAsync(tenant, "explain.trace", 10, CancellationToken.None);
        Assert.Equal("awaiting_manual_approval", quote.Status);

        var gate = await billing.EnsureExecutableAsync(quote.QuoteId, tenant, "explain.trace", CancellationToken.None);
        Assert.True(gate.Allowed);
        Assert.Equal("plan_credit", gate.Code);
        Assert.Equal(0, gate.Quote!.AmountCents);

        var after = await entitlements.GetByTenantAsync(tenant, CancellationToken.None);
        Assert.Equal(10, after.Single().UsedCredits["explain.trace"]);
    }

    [Fact]
    public async Task No_Credits_Still_402s()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTenantResolver.TenantHeader, "no-plan-tenant");
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "laplace-converse-001",
            messages = new[] { new { role = "user", content = "what is a whale" } }
        });
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }
}
