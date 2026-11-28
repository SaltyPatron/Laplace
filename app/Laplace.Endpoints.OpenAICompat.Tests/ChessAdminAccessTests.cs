using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Laplace.Chess.Service;
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

[Collection(ChessAdminAccessTests.Environment)]
public sealed class ChessAdminAccessTests : IDisposable
{
    internal const string Environment = "chess-admin-access environment";
    private static readonly Guid User = Guid.Parse("7d2b4e8f-3a1c-4f9b-8e2d-5a6c7b8d9e01");
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("laplace-chess-admin-").FullName;
    private readonly string? _prevData = System.Environment.GetEnvironmentVariable("LAPLACE_DATA");

    public ChessAdminAccessTests()
    {
        System.Environment.SetEnvironmentVariable("LAPLACE_DATA", _dataRoot);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable("LAPLACE_DATA", _prevData);
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    private sealed class Store(string? role) : IPlatformAccessStore
    {
        public Task<string?> RoleAsync(Guid user, CancellationToken ct) => Task.FromResult(role);
        public Task<IReadOnlyList<PlatformPerson>> PeopleAsync(string search, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformPerson>>([]);
        public Task<IReadOnlyList<PlatformRoleEvent>> EventsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformRoleEvent>>([]);
        public Task SetRoleAsync(Guid user, string? role, Guid? actor, CancellationToken ct, bool onlyIfNeverAssigned = false) => Task.CompletedTask;
    }

    private sealed class RecordingWitnesser : IChessFideLinkWitnesser
    {
        public List<(string RootDir, string FilePath)> Calls { get; } = [];
        public Task WitnessAsync(string rootDir, string filePath, CancellationToken ct)
        {
            Calls.Add((rootDir, filePath));
            return Task.CompletedTask;
        }
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

    private sealed class Factory(string? role) : WebApplicationFactory<Program>
    {
        public RecordingWitnesser Witnesser { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPlatformAccessStore>(); services.AddSingleton<IPlatformAccessStore>(new Store(role));
            services.RemoveAll<ITenantResolver>(); services.AddSingleton<ITenantResolver, Resolver>();
            services.RemoveAll<ISubstrateClient>(); services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.RemoveAll<IChessFideLinkWitnesser>(); services.AddSingleton<IChessFideLinkWitnesser>(Witnesser);
            services.PostConfigure<LaplaceAuthOptions>(o => { o.Mode = "identity"; o.DevPrincipal = null; o.OperatorToken = "test-installation"; });
            services.PostConfigure<StripeBillingOptions>(TestBillingOptions.IsolateFromHostStripe);
            services.AddAuthentication(o => o.DefaultAuthenticateScheme = "chess-admin-test")
                .AddScheme<AuthenticationSchemeOptions, Identity>("chess-admin-test", _ => { });
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
    [InlineData("lichess-fetch")]
    [InlineData("player-profile")]
    [InlineData("fide-profile")]
    [InlineData("fide-roster")]
    public async Task CuratedLabJobs_RequireIngestPermission(string kind)
    {
        await using var userFactory = new Factory(role: null);
        using var userClient = userFactory.Client(signedIn: true);
        using var denied = await userClient.PostAsJsonAsync("/chess/lab/start", new { kind, config = new Dictionary<string, string>() });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("ingestion.manage", await denied.Content.ReadAsStringAsync());

        await using var observerFactory = new Factory(role: "observer");
        using var observerClient = observerFactory.Client(signedIn: true);
        using var observerDenied = await observerClient.PostAsJsonAsync("/chess/lab/start", new { kind, config = new Dictionary<string, string>() });
        Assert.Equal(HttpStatusCode.Forbidden, observerDenied.StatusCode);
    }

    [Fact]
    public async Task AdminFideLink_RejectsNonAdminAndValidatesAndWitnessesForAdmin()
    {
        await using var userFactory = new Factory(role: "observer");
        using var userClient = userFactory.Client(signedIn: true);
        using var denied = await userClient.PostAsJsonAsync("/v1/admin/chess/fide-link", new
        {
            fide_id = "1503014",
            provider = "chesscom",
            username = "MagnusCarlsen",
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("platform_permission_required", await denied.Content.ReadAsStringAsync());
        Assert.Empty(userFactory.Witnesser.Calls);

        await using var adminFactory = new Factory(role: "operator");
        using var adminClient = adminFactory.Client(signedIn: true);

        using var badFide = await adminClient.PostAsJsonAsync("/v1/admin/chess/fide-link", new
        {
            fide_id = "abc",
            provider = "chesscom",
            username = "MagnusCarlsen",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badFide.StatusCode);

        using var badProvider = await adminClient.PostAsJsonAsync("/v1/admin/chess/fide-link", new
        {
            fide_id = "1503014",
            provider = "bogus",
            username = "MagnusCarlsen",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);

        using var ok = await adminClient.PostAsJsonAsync("/v1/admin/chess/fide-link", new
        {
            fide_id = "1503014",
            provider = "chesscom",
            username = "MagnusCarlsen",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("1503014", root.GetProperty("fide_id").GetString());
        Assert.Equal("chesscom", root.GetProperty("provider").GetString());
        Assert.Equal("MagnusCarlsen", root.GetProperty("username").GetString());
        Assert.Equal("Laplace Curated Chess", root.GetProperty("witness").GetString());
        Assert.Equal("AcademicCurated", root.GetProperty("trust_class").GetString());

        string expectedRoot = Path.GetFullPath(Path.Combine(_dataRoot, "Games", "Chess", "fide", "links"));
        string expectedPath = Path.Combine(expectedRoot, "player-links.json");
        Assert.Equal(expectedPath, root.GetProperty("path").GetString());
        Assert.True(File.Exists(expectedPath));

        var call = Assert.Single(adminFactory.Witnesser.Calls);
        Assert.Equal(expectedRoot, call.RootDir);
        Assert.Equal(expectedPath, call.FilePath);
    }
}

[CollectionDefinition(ChessAdminAccessTests.Environment, DisableParallelization = true)]
public sealed class ChessAdminAccessEnvironmentCollection;
