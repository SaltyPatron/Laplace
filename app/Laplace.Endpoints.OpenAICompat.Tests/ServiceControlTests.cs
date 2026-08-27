using System.Net;
using System.Net.Http.Json;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ServiceControlTests
{
    private sealed class FakeControl : IServiceControl
    {
        public bool? Unavailable;
        public List<(ManagedService, ServiceAction)> Calls { get; } = [];
        public Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct)
        {
            Calls.Add((service, action));
            if (Unavailable is { } busy) throw new ServiceControlUnavailableException(busy);
            var name = service.ToString().ToLowerInvariant();
            return Task.FromResult(new ServiceControlResult(name, $"laplace-{name}.service", action.ToString(),
                action != ServiceAction.Status, "loaded", "active", "running", "success", 123));
        }
    }

    private sealed class Factory(string? token = "operator-test-only", string mode = "header") : WebApplicationFactory<Program>
    {
        public FakeControl Control { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IServiceControl>();
            services.AddSingleton<IServiceControl>(Control);
            services.PostConfigure<LaplaceAuthOptions>(o => { o.Mode = mode; o.OperatorToken = token; });
            services.PostConfigure<StripeBillingOptions>(TestBillingOptions.IsolateFromHostStripe);
        });
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect")]
    public async Task HeaderModeAndForgedTenantCannotAuthorizeServiceControls(string? presented)
    {
        await using var factory = new Factory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Laplace-Tenant", "operator");
        if (presented is not null) client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, presented);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/services/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/admin/services/mcp/stop", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/chess/lichess/start", new { })).StatusCode);
        Assert.Empty(factory.Control.Calls);
    }

    [Fact]
    public async Task UnconfiguredOperatorSecretFailsClosed()
    {
        await using var factory = new Factory(token: null);
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/services/lichess")).StatusCode);
        Assert.Empty(factory.Control.Calls);
    }

    [Theory]
    [InlineData("mcp", "start", ManagedService.Mcp, ServiceAction.Start)]
    [InlineData("mcp", "stop", ManagedService.Mcp, ServiceAction.Stop)]
    [InlineData("mcp", "restart", ManagedService.Mcp, ServiceAction.Restart)]
    [InlineData("lichess", "start", ManagedService.Lichess, ServiceAction.Start)]
    [InlineData("lichess", "stop", ManagedService.Lichess, ServiceAction.Stop)]
    [InlineData("lichess", "restart", ManagedService.Lichess, ServiceAction.Restart)]
    public async Task OperatorActionsHaveExactAllowlistedTargets(string name, string verb, ManagedService expected, ServiceAction action)
    {
        await using var factory = new Factory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        using var reply = await client.PostAsJsonAsync($"/v1/admin/services/{name}/{verb}", new { });
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Equal((expected, action), Assert.Single(factory.Control.Calls));
        Assert.Contains($"laplace-{name}.service", await reply.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("postgresql", "stop")]
    [InlineData("laplace-api.service", "restart")]
    [InlineData("mcp", "enable")]
    [InlineData("mcp;id", "stop")]
    public async Task ArbitraryUnitsActionsAndShellTextNeverReachHelper(string name, string verb)
    {
        await using var factory = new Factory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        using var reply = await client.PostAsJsonAsync($"/v1/admin/services/{name}/{verb}", new { });
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        Assert.Empty(factory.Control.Calls);
    }

    [Fact]
    public async Task StatusDoesNotBecomeAMutation_AndLegacyStartDoesNotSilentlyIgnoreSettings()
    {
        await using var factory = new Factory();
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/services/mcp")).StatusCode);
        Assert.Equal((ManagedService.Mcp, ServiceAction.Status), Assert.Single(factory.Control.Calls));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/chess/lichess/start", new { depth = 20 })).StatusCode);
        Assert.Single(factory.Control.Calls);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("key")]
    public async Task OperatorPolicyIsIndependentOfCustomerAuthMode(string mode)
    {
        await using var factory = new Factory(mode: mode);
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/services/mcp")).StatusCode);
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/services/mcp")).StatusCode);
        Assert.Single(factory.Control.Calls);
    }

    [Fact]
    public async Task OldLabRouteCannotBypassManagedLichessOwnership()
    {
        await using var factory = new Factory();
        using var client = factory.Client();
        using var reply = await client.PostAsJsonAsync("/chess/lab/start", new { kind = "lichess-bot" });
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        Assert.Empty(factory.Control.Calls);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.Conflict)]
    public async Task MissingPrivilegeOrDeploymentConflictNeverClaimsSuccess(bool busy, HttpStatusCode expected)
    {
        await using var factory = new Factory();
        factory.Control.Unavailable = busy;
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        Assert.Equal(expected, (await client.PostAsJsonAsync("/v1/admin/services/mcp/start", new { })).StatusCode);
        Assert.Equal(expected, (await client.PostAsJsonAsync("/chess/lichess/stop", new { })).StatusCode);
    }

    [Fact]
    public async Task PlainHttpCannotCarryRemoteOperatorActions()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "operator-test-only");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1/admin/services/mcp/stop", new { })).StatusCode);
        Assert.Empty(factory.Control.Calls);
    }
}
