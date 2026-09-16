using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class BrowserIdentityTests : IClassFixture<GoldenFactory>
{
    private readonly GoldenFactory _factory;

    public BrowserIdentityTests(GoldenFactory factory) => _factory = factory;

    [Theory]
    [InlineData("https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0", "9188040d-6c67-4c5b-b112-36a304b66dad", true)]
    [InlineData("https://login.microsoftonline.com/tenant-a/v2.0", "tenant-b", false)]
    [InlineData("https://evil.example/tenant-a/v2.0", "tenant-a", false)]
    [InlineData("https://login.microsoftonline.com/common/v2.0", null, false)]
    public void MicrosoftIssuerMustMatchTheValidatedTokensTenant(
        string issuer, string? tenant, bool expected) =>
        Assert.Equal(expected, ExternalIdentitySignIn.ValidMicrosoftIssuer(issuer, tenant));

    [Fact]
    public async Task BrowserIdentityOverridesCallerControlledTenantHeader()
    {
        var userId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(LaplaceClaimTypes.User, userId.ToString("D")),
                new Claim(LaplaceClaimTypes.Tenant, "real-tenant"),
                new Claim(LaplaceClaimTypes.Provider, "google")
            ], "test"))
        };
        context.Request.Headers[HeaderTenantResolver.TenantHeader] = "forged-tenant";
        var resolver = new ApiKeyTenantResolver(
            new ApiKeyService(new InMemoryApiKeyStore()));

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal("real-tenant", resolved.TenantId);
        Assert.Equal("browser", resolved.AuthKind);
        Assert.Equal(userId.ToString("D"), resolved.Claims["user_id"]);
    }

    [Fact]
    public async Task ServerSideCookieTicketCanBeRevokedImmediately()
    {
        var store = new MemoryIdentityStore();
        var tickets = new BrowserTicketStore(store, new EphemeralDataProtectionProvider());
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(LaplaceClaimTypes.User, userId.ToString("D")),
            new Claim(LaplaceClaimTypes.Tenant, "tenant-a")
        ], BrowserAuthSettings.CookieScheme));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            BrowserAuthSettings.CookieScheme);

        var key = await tickets.StoreAsync(ticket);
        var roundTrip = await tickets.RetrieveAsync(key);

        Assert.NotNull(roundTrip);
        Assert.Equal(key, roundTrip!.Principal.FindFirstValue(LaplaceClaimTypes.WebSession));
        Assert.Single(await store.ListWebSessionsAsync(userId, CancellationToken.None));

        await tickets.RemoveAsync(key);
        Assert.Null(await tickets.RetrieveAsync(key));
    }

    [Fact]
    public async Task AnonymousIdentityDiscoveryIsPublicAndReportsNoSession()
    {
        using var client = _factory.CreateClient();
        using var me = await client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var document = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());

        using var login = await client.GetAsync("/v1/auth/login/microsoft");
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Contains("provider_not_configured", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("Tier", "db")]
    public async Task PostgresIdentityStorePersistsAccountSessionAndConversation()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(
            LaplaceInstall.PostgresConnectionString()).Build();
        var store = new PostgresIdentityStore(dataSource);
        var suffix = Guid.NewGuid().ToString("N");
        var provider = $"test-{suffix}";
        var issuer = $"https://identity.test/{suffix}";
        IdentityAccount? account = null;
        try
        {
            var profile = new ExternalIdentityProfile(
                provider, issuer, "subject-1", $"{suffix}@example.test", "Test User", null);
            account = await store.UpsertExternalIdentityAsync(profile, CancellationToken.None);
            var second = await store.UpsertExternalIdentityAsync(profile, CancellationToken.None);
            Assert.Equal(account.UserId, second.UserId);
            Assert.Equal(account.TenantId, second.TenantId);

            await store.UpsertClientAsync(new ExternalOidcProvider(
                provider, "Test", "public-client", "never-persist-this", issuer, "/signin-test"),
                CancellationToken.None);
            await store.PutWebSessionAsync(new StoredWebSession(
                $"ws-{suffix}", account.UserId, account.TenantId, [1, 2, 3],
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null),
                CancellationToken.None);
            await store.UpsertConversationAsync(
                account.TenantId, account.UserId, $"s-{suffix}", "Persistent chat", CancellationToken.None);

            var retainedSession = await store.GetWebSessionAsync($"ws-{suffix}", CancellationToken.None);
            Assert.NotNull(retainedSession);
            Assert.Equal(account.UserId, retainedSession!.UserId);
            Assert.Equal(account.TenantId, retainedSession.TenantId);
            Assert.Equal(new byte[] { 1, 2, 3 }, retainedSession.Ticket);
            Assert.Single(await store.ListWebSessionsAsync(account.UserId, CancellationToken.None));
            var conversations = await store.ListConversationsAsync(
                account.UserId, account.TenantId, CancellationToken.None);
            Assert.Equal("Persistent chat", Assert.Single(conversations).Title);

            await store.RevokeWebSessionAsync($"ws-{suffix}", account.UserId, CancellationToken.None);
            Assert.Null(await store.GetWebSessionAsync($"ws-{suffix}", CancellationToken.None));
            Assert.Empty(await store.ListWebSessionsAsync(account.UserId, CancellationToken.None));

            await using var secretProbe = dataSource.CreateCommand("""
                SELECT count(*) FROM app.auth_clients c
                WHERE c.provider = @provider AND c.client_id = 'public-client'
                  AND to_jsonb(c)::text NOT LIKE '%never-persist-this%';
                """);
            secretProbe.Parameters.AddWithValue("provider", provider);
            Assert.Equal(1L, (long)(await secretProbe.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand("""
                DELETE FROM app.auth_clients WHERE provider = @provider;
                DELETE FROM app.users WHERE user_id = @user_id;
                DELETE FROM app.tenants WHERE tenant_id = @tenant_id;
                """);
            cleanup.Parameters.AddWithValue("provider", provider);
            cleanup.Parameters.AddWithValue("user_id", account?.UserId ?? Guid.Empty);
            cleanup.Parameters.AddWithValue("tenant_id", account?.TenantId ?? "");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class MemoryIdentityStore : IIdentityStore
    {
        private readonly Dictionary<string, StoredWebSession> _sessions = new(StringComparer.Ordinal);

        public Task<IdentityAccount> UpsertExternalIdentityAsync(
            ExternalIdentityProfile profile, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpsertClientAsync(ExternalOidcProvider provider, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PutWebSessionAsync(StoredWebSession session, CancellationToken ct)
        {
            _sessions[session.SessionId] = session;
            return Task.CompletedTask;
        }

        public Task<StoredWebSession?> GetWebSessionAsync(string sessionId, CancellationToken ct)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)
                || session.RevokedAt is not null
                || session.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult<StoredWebSession?>(null);
            return Task.FromResult<StoredWebSession?>(session);
        }

        public Task RevokeWebSessionAsync(string sessionId, Guid userId, CancellationToken ct)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.UserId == userId)
                _sessions[sessionId] = session with { RevokedAt = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredWebSession>> ListWebSessionsAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredWebSession>>(_sessions.Values
                .Where(session => session.UserId == userId && session.RevokedAt is null)
                .ToArray());

        public Task UpsertConversationAsync(
            string tenantId, Guid userId, string sessionKey, string? title, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConversationSessionView>> ListConversationsAsync(
            Guid userId, string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConversationSessionView>>([]);
    }
}
