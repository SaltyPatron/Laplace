using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Laplace.Endpoints.OpenAICompat.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PlatformAccessTests
{
    private static readonly Guid User = Guid.Parse("0d949a32-2662-452e-a246-546c4e6433fb");
    private sealed class AccessStore(string? role) : IPlatformAccessStore
    {
        public string? Role = role;
        public int Changes;
        public bool InitialAssignmentRequested;
        public bool RefuseInitialAssignment;
        public Task<string?> RoleAsync(Guid user, CancellationToken ct) => Task.FromResult(Role);
        public Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformPerson>>([]);
        public Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformRoleEvent>>([]);
        public Task SetRoleAsync(Guid user, string? role, Guid? actor, CancellationToken ct, bool onlyIfNeverAssigned = false)
        {
            InitialAssignmentRequested = onlyIfNeverAssigned;
            if (onlyIfNeverAssigned && RefuseInitialAssignment) throw new AccessChangeException("Previous role change exists.");
            Changes++;
            return Task.CompletedTask;
        }
    }
    private sealed class IdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.ContainsKey("Test-Signed-In")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(LaplaceClaimTypes.User, User.ToString()), new Claim(LaplaceClaimTypes.Tenant, "test-workspace"),
                new Claim(ClaimTypes.Email, "owner@example.test")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }
    private sealed class Resolver : ITenantResolver
    {
        public ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct)
        {
            // Exercise the real middleware with a current workspace-owner membership.
            context.Items["laplace.workspace_role"] = "owner";
            return ValueTask.FromResult(context.User.Identity?.IsAuthenticated == true
                ? new TenantContext("test-workspace", "browser", new Dictionary<string, string> { ["user_id"] = User.ToString() })
                : new TenantContext("test-workspace", "header", TenantContext.NoClaims));
        }
    }
    private sealed class Control : IServiceControl
    {
        public int Calls;
        public Task<ServiceControlResult> ExecuteAsync(ManagedService service, ServiceAction action, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ServiceControlResult(service.ToString(), "fixture", action.ToString(), action != ServiceAction.Status, "loaded", "active", "running", "success", 1));
        }
    }
    private sealed class Factory(string? role) : WebApplicationFactory<Program>
    {
        public AccessStore Access { get; } = new(role);
        public Control Control { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPlatformAccessStore>(); services.AddSingleton<IPlatformAccessStore>(Access);
            services.RemoveAll<ITenantResolver>(); services.AddSingleton<ITenantResolver, Resolver>();
            services.RemoveAll<ISubstrateClient>(); services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.RemoveAll<IServiceControl>(); services.AddSingleton<IServiceControl>(Control);
            services.PostConfigure<LaplaceAuthOptions>(o => { o.Mode = "identity"; o.DevPrincipal = null; o.OperatorToken = "test-installation"; });
            services.PostConfigure<StripeBillingOptions>(TestBillingOptions.IsolateFromHostStripe);
            services.AddAuthentication(o => o.DefaultAuthenticateScheme = "platform-test")
                .AddScheme<AuthenticationSchemeOptions, IdentityHandler>("platform-test", _ => { });
        });
        public HttpClient Client(bool signedIn = true)
        {
            var client = CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            if (signedIn) client.DefaultRequestHeaders.Add("Test-Signed-In", "yes");
            client.DefaultRequestHeaders.Add("X-Laplace-Request", "1");
            return client;
        }
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData("observer", true, false, false)]
    [InlineData("operator", true, true, false)]
    [InlineData("administrator", true, true, true)]
    public async Task WorkspaceOwnerMustHaveExplicitPlatformPermissions(string? role, bool read, bool services, bool access)
    {
        await using var factory = new Factory(role);
        using var client = factory.Client();
        Assert.Equal(read ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/services/mcp")).StatusCode);
        Assert.Equal(services ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/admin/services/mcp/start", new { })).StatusCode);
        Assert.Equal(access ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/access/people")).StatusCode);
        Assert.Equal(access ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/access/capabilities")).StatusCode);
        Assert.Equal(access ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/v1/admin/access/people/{User}/role", new { role = "administrator" })).StatusCode);
        Assert.Equal((read ? 1 : 0) + (services ? 1 : 0), factory.Control.Calls);
        Assert.Equal(access ? 1 : 0, factory.Access.Changes);
        Assert.False(factory.Access.InitialAssignmentRequested);
    }

    [Fact]
    public async Task InitialAssignmentCapabilityAndConditionUseTheProtectedAccessRoute()
    {
        await using var factory = new Factory(null);
        using var client = factory.Client(signedIn: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/access/capabilities")).StatusCode);
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "test-installation");
        using var capabilities = await client.GetAsync("/v1/admin/access/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("initialRoleAssignment").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/v1/admin/access/people/{User}/role",
            new { role = "administrator", onlyIfNeverAssigned = true })).StatusCode);
        Assert.True(factory.Access.InitialAssignmentRequested);
        Assert.Equal(1, factory.Access.Changes);
        factory.Access.RefuseInitialAssignment = true;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/v1/admin/access/people/{User}/role",
            new { role = "administrator", onlyIfNeverAssigned = true })).StatusCode);
        Assert.Equal(1, factory.Access.Changes);
    }

    [Theory]
    [InlineData("observer", "ops.ingest_runs", true)]
    [InlineData("observer", "ops.ingest_run_close", false)]
    [InlineData("operator", "ops.ingest_run_close", true)]
    [InlineData("operator", "ops.analyze_substrate", false)]
    [InlineData("administrator", "ops.analyze_substrate", true)]
    [InlineData(null, "ops.activity", false)]
    [InlineData(null, "ops.source_status", true)]
    public async Task GenericOperationEndpointEnforcesTheSameRoleBoundary(string? role, string operation, bool allowed)
    {
        await using var factory = new Factory(role);
        using var client = factory.Client();
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/v1/op", new { name = operation })).StatusCode);
    }

    [Fact]
    public async Task EveryInstalledMutationRequiresAPlatformPermission()
    {
        foreach (var operation in Laplace.SubstrateCRUD.Npgsql.InstalledOpInvoker.WritableOps)
            Assert.NotNull(PlatformPolicy.ForOperation(operation));
        await using var factory = new Factory(null);
        using var client = factory.Client();
        foreach (var operation in Laplace.SubstrateCRUD.Npgsql.InstalledOpInvoker.WritableOps)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/op", new { name = operation })).StatusCode);
    }

    [Fact]
    public async Task RevocationAppliesOnTheNextRequestWithoutSigningOut()
    {
        await using var factory = new Factory("administrator");
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/access/people")).StatusCode);
        factory.Access.Role = null;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/access/people")).StatusCode);
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("operator")]
    public async Task OperationalReadAccessDoesNotExposeAgentConfigurationOrAccessDirectories(string role)
    {
        await using var factory = new Factory(role);
        using var client = factory.Client();
        foreach (var path in new[] { "/v1/admin/agents/config", "/v1/admin/access/clients", "/v1/admin/access/workspaces" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Theory]
    [InlineData("Authorization", "Bearer invalid")]
    [InlineData("X-Api-Key", "lp_invalid")]
    public async Task PresentedClientCredentialCannotFallBackToAnAdministratorCookie(string header, string value)
    {
        await using var factory = new Factory("administrator");
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/admin/services/mcp/stop", new { })).StatusCode);
        Assert.Equal(0, factory.Control.Calls);
    }

    [Fact]
    public async Task CrossOriginBrowserMutationIsDeniedEvenWithARequestMarker()
    {
        await using var factory = new Factory("administrator");
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/admin/services/mcp/stop", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/v1/op", new { name = "ops.analyze_substrate" })).StatusCode);
        Assert.Equal(0, factory.Control.Calls);
    }

    [Fact]
    public async Task AnonymousAndForgedWorkspaceAreDeniedButInstallationCredentialRemainsUsable()
    {
        await using var factory = new Factory("administrator");
        using var client = factory.Client(signedIn: false);
        client.DefaultRequestHeaders.Add("X-Laplace-Tenant", "administrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/admin/access/people")).StatusCode);
        client.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "test-installation");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/access/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/op", new { name = "ops.analyze_substrate" })).StatusCode);
    }
}
