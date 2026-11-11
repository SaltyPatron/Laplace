using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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

// The shared Lichess bot is a platform service. Changing its routing follows the platform
// roles (services.manage), never a separate list of user IDs kept in the controller file.
[Collection(LichessControlAccessTests.Environment)]
public sealed class LichessControlAccessTests : IDisposable
{
    internal const string Environment = "lichess-control-client environment";
    private static readonly Guid User = Guid.Parse("6f0c2a4e-1c1b-4d7a-9b8e-3a1f5c2d7e90");
    private readonly string _root = Directory.CreateTempSubdirectory("lichess-control-access").FullName;
    private readonly string? _previous = System.Environment.GetEnvironmentVariable("LAPLACE_LICHESS_CONTROL_CLIENT");

    public LichessControlAccessTests()
    {
        var token = Path.Combine(_root, "control.token");
        File.WriteAllText(token, "fixture-control-token");
        var client = Path.Combine(_root, "chess-client.json");
        // A file written before this change still names operators; it is ignored, not trusted.
        File.WriteAllText(client, JsonSerializer.Serialize(new { url = "http://127.0.0.1:5189/", tokenFile = token, operators = new[] { User } }));
        System.Environment.SetEnvironmentVariable("LAPLACE_LICHESS_CONTROL_CLIENT", client);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable("LAPLACE_LICHESS_CONTROL_CLIENT", _previous);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class Store(string? role) : IPlatformAccessStore
    {
        public Task<string?> RoleAsync(Guid user, CancellationToken ct) => Task.FromResult(role);
        public Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformPerson>>([]);
        public Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformRoleEvent>>([]);
        public Task SetRoleAsync(Guid user, string? role, Guid? actor, CancellationToken ct, bool onlyIfNeverAssigned = false) => Task.CompletedTask;
    }

    private sealed class Identity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.ContainsKey("Test-Signed-In")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(LaplaceClaimTypes.User, User.ToString()), new Claim(LaplaceClaimTypes.Tenant, "test-workspace")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.NoResult());
    }

    private sealed class Resolver : ITenantResolver
    {
        public ValueTask<TenantContext> ResolveAsync(HttpContext context, CancellationToken ct)
        {
            context.Items["laplace.workspace_role"] = "owner";
            if (context.Request.Headers.Authorization.Count > 0)
                return ValueTask.FromResult(new TenantContext("test-workspace", "api_key", new Dictionary<string, string> { ["key_prefix"] = "sk-laplace-test" }));
            return ValueTask.FromResult(context.User.Identity?.IsAuthenticated == true
                ? new TenantContext("test-workspace", "browser", new Dictionary<string, string> { ["user_id"] = User.ToString() })
                : new TenantContext("test-workspace", "header", TenantContext.NoClaims));
        }
    }

    /// <summary>The controller on the chess host: answers status and records each forwarded selection.</summary>
    private sealed class Controller : HttpMessageHandler
    {
        public readonly List<string> Selections = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("fixture-control-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/selection"))
            {
                Selections.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { revision = 2 }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { phase = "off", revision = 1 }) };
        }
    }

    private sealed class Factory(string? role) : WebApplicationFactory<Program>
    {
        public Controller Controller { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPlatformAccessStore>(); services.AddSingleton<IPlatformAccessStore>(new Store(role));
            services.RemoveAll<ITenantResolver>(); services.AddSingleton<ITenantResolver, Resolver>();
            services.RemoveAll<ISubstrateClient>(); services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.AddHttpClient("lichess-control").ConfigurePrimaryHttpMessageHandler(() => Controller);
            services.PostConfigure<LaplaceAuthOptions>(o => { o.Mode = "identity"; o.DevPrincipal = null; o.OperatorToken = "test-installation"; });
            services.PostConfigure<StripeBillingOptions>(TestBillingOptions.IsolateFromHostStripe);
            services.AddAuthentication(o => o.DefaultAuthenticateScheme = "lichess-test")
                .AddScheme<AuthenticationSchemeOptions, Identity>("lichess-test", _ => { });
        });
        public HttpClient Client(bool signedIn)
        {
            var client = CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            if (signedIn) client.DefaultRequestHeaders.Add("Test-Signed-In", "yes");
            client.DefaultRequestHeaders.Add("X-Laplace-Request", "1");
            return client;
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("observer", false)]
    [InlineData("operator", true)]
    [InlineData("administrator", true)]
    public async Task RoutingFollowsThePlatformServicesPermission(string? role, bool allowed)
    {
        await using var factory = new Factory(role);
        using var client = factory.Client(signedIn: true);
        using var status = JsonDocument.Parse(await client.GetStringAsync("/chess/lichess/control"));
        Assert.Equal(allowed, status.RootElement.GetProperty("canControl").GetBoolean());
        if (!allowed) Assert.Contains("Operations manager or Platform administrator role", status.RootElement.GetProperty("controlRefusal").GetString());
        using var applied = await client.PostAsJsonAsync("/chess/lichess/selection", new { profile = "staging", expectedRevision = 1 });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, applied.StatusCode);
        Assert.Equal(allowed ? 1 : 0, factory.Controller.Selections.Count);
        if (allowed) Assert.Contains($"/{User:D}", factory.Controller.Selections[0]);
    }

    [Fact]
    public async Task AServiceKeyIsToldWhyItCannotRouteAndTheInstallationCredentialCan()
    {
        await using var factory = new Factory(null);
        using var key = factory.Client(signedIn: false);
        key.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-laplace-test");
        using var refused = JsonDocument.Parse(await key.GetStringAsync("/chess/lichess/control"));
        Assert.False(refused.RootElement.GetProperty("canControl").GetBoolean());
        Assert.Contains("service credential", refused.RootElement.GetProperty("controlRefusal").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync("/chess/lichess/selection", new { profile = "staging", expectedRevision = 1 })).StatusCode);
        Assert.Empty(factory.Controller.Selections);

        // The installation credential holds every platform permission, as it does for /v1/admin services.
        key.DefaultRequestHeaders.Add(OperatorAuth.TokenHeader, "test-installation");
        using var allowed = JsonDocument.Parse(await key.GetStringAsync("/chess/lichess/control"));
        Assert.True(allowed.RootElement.GetProperty("canControl").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await key.PostAsJsonAsync("/chess/lichess/selection", new { profile = "staging", expectedRevision = 1 })).StatusCode);
        Assert.Contains("installation-credential", factory.Controller.Selections.Single());
    }
}

[CollectionDefinition(LichessControlAccessTests.Environment, DisableParallelization = true)]
public sealed class LichessControlEnvironmentCollection;
