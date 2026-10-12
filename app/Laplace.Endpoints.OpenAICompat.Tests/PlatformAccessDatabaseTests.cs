using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PlatformAccessDatabaseTests : IClassFixture<GoldenFactory>
{
    private readonly GoldenFactory factory;
    public PlatformAccessDatabaseTests(GoldenFactory factory) => this.factory = factory;
    private static async Task<IdentityAccount> Person(NpgsqlDataSource database)
    {
        var unique = Guid.NewGuid().ToString("N");
        return await new PostgresIdentityStore(database).UpsertExternalIdentityAsync(
            new ExternalIdentityProfile("access-test", "https://access.example.test", unique, $"{unique}@example.test", "Access test", null), CancellationToken.None);
    }
    private static async Task Cleanup(NpgsqlDataSource database, params IdentityAccount[] people)
    {
        await using var command = database.CreateCommand("""
            DELETE FROM app.platform_role_events WHERE user_id = ANY(@users);
            DELETE FROM app.users WHERE user_id = ANY(@users);
            DELETE FROM app.tenants WHERE tenant_id = ANY(@tenants);
            """);
        command.Parameters.AddWithValue("users", people.Select(p => p.UserId).ToArray());
        command.Parameters.AddWithValue("tenants", people.Select(p => p.TenantId).ToArray());
        await command.ExecuteNonQueryAsync();
    }

    [Fact, Trait("Tier", "db")]
    public async Task RoleChangesAreAuditedRecheckTheActorAndPreserveAnAdministrator()
    {
        await using var database = NpgsqlDataSource.Create(LaplaceInstall.AppConnectionString());
        var first = await Person(database); var second = await Person(database);
        var store = new PostgresPlatformAccessStore(database);
        try
        {
            await using var count = database.CreateCommand("SELECT count(*) FROM app.platform_roles WHERE role = 'administrator'");
            var existingAdministrators = (long)(await count.ExecuteScalarAsync())!;
            await store.SetRoleAsync(first.UserId, "administrator", null, default);
            await store.SetRoleAsync(second.UserId, "administrator", first.UserId, default);
            Assert.Equal("administrator", await store.RoleAsync(second.UserId, default));
            Assert.Contains(await store.PeopleAsync(second.Email!, default), p => p.UserId == second.UserId && p.Role == "administrator");
            // The lock serializes competing demotions. With no other administrators,
            // exactly one succeeds; with existing administrators both are safe.
            async Task<bool> Demote(Guid user)
            {
                try { await store.SetRoleAsync(user, "operator", user, default); return true; }
                catch (AccessChangeException) { return false; }
            }
            var demotions = await Task.WhenAll(Demote(first.UserId), Demote(second.UserId));
            Assert.Equal(existingAdministrators == 0 ? 1 : 2, demotions.Count(x => x));
            Assert.True((long)(await count.ExecuteScalarAsync())! >= 1);
            var revokedActor = demotions[0] ? first : second;
            await Assert.ThrowsAsync<AccessChangeException>(() => store.SetRoleAsync(second.UserId, "observer", revokedActor.UserId, default));
            await Assert.ThrowsAsync<AccessChangeException>(() => store.SetRoleAsync(second.UserId, "invented-role", null, default));
            var events = (await store.EventsAsync(default)).Where(e => e.UserId == first.UserId || e.UserId == second.UserId).ToArray();
            Assert.Equal(2 + demotions.Count(x => x), events.Length);
            Assert.Contains(events, e => e.UserId == second.UserId && e.Actor == first.UserId.ToString() && e.Role == "administrator");
        }
        finally { await Cleanup(database, first, second); }
    }

    private sealed class IdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IdentityAccount account)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(LaplaceClaimTypes.User, account.UserId.ToString()), new Claim(LaplaceClaimTypes.Tenant, account.TenantId)
            ], Scheme.Name)), Scheme.Name)));
    }

    [Fact, Trait("Tier", "db")]
    public async Task WorkspaceRoleChangesProtectTheLastOwnerAndCannotReachAnotherWorkspace()
    {
        await using var database = NpgsqlDataSource.Create(LaplaceInstall.AppConnectionString());
        var owner = await Person(database); var member = await Person(database);
        try
        {
            using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddKeyedSingleton<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey, database);
                services.AddSingleton(owner);
                services.AddSingleton<IBillingEntitlementStore, InMemoryBillingEntitlementStore>();
                services.AddAuthentication(o => o.DefaultAuthenticateScheme = "workspace-role-test")
                    .AddScheme<AuthenticationSchemeOptions, IdentityHandler>("workspace-role-test", _ => { });
            }));
            using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Laplace-Request", "1");
            // The other user is an owner elsewhere, but is not a member here.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/v1/account/members/{member.UserId}/role", new { role = "admin" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/v1/account/members/{owner.UserId}/role", new { role = "member" })).StatusCode);
            await using (var add = database.CreateCommand("INSERT INTO app.tenant_memberships(tenant_id, user_id, role) VALUES($1, $2, 'member')"))
            {
                add.Parameters.AddWithValue(owner.TenantId); add.Parameters.AddWithValue(member.UserId); await add.ExecuteNonQueryAsync();
            }
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/v1/account/members/{member.UserId}/role", new { role = "owner" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/v1/account/members/{owner.UserId}/role", new { role = "admin" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/v1/account/members/{member.UserId}/role", new { role = "member" })).StatusCode);
            var history = await client.GetStringAsync("/v1/account/role-events");
            using var payload = System.Text.Json.JsonDocument.Parse(history);
            Assert.Equal(2, payload.RootElement.GetProperty("events").GetArrayLength());
            await using var verify = database.CreateCommand("SELECT role FROM app.tenant_memberships WHERE tenant_id = $1 AND user_id = $2");
            verify.Parameters.AddWithValue(member.TenantId); verify.Parameters.AddWithValue(member.UserId);
            Assert.Equal("owner", await verify.ExecuteScalarAsync());
            await host.Services.GetRequiredService<IBillingEntitlementStore>().ActivatePlanAsync(owner.TenantId,
                host.Services.GetRequiredService<IBillingCatalog>().ListPlans().First(), "fixture-customer", "fixture-subscription", DateTimeOffset.UtcNow, default);
            using var beforeRemoval = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/v1/account"));
            Assert.Equal(1, beforeRemoval.RootElement.GetProperty("subscriptions").GetArrayLength());
            // Platform authority remains usable if the selected workspace membership
            // is removed. It must not restore access to that workspace's account data.
            await new PostgresPlatformAccessStore(database).SetRoleAsync(owner.UserId, "observer", null, default);
            await using var remove = database.CreateCommand("DELETE FROM app.tenant_memberships WHERE tenant_id = $1 AND user_id = $2");
            remove.Parameters.AddWithValue(owner.TenantId); remove.Parameters.AddWithValue(owner.UserId);
            await remove.ExecuteNonQueryAsync();
            using var access = await client.GetAsync("/v1/access");
            Assert.Equal(HttpStatusCode.OK, access.StatusCode);
            using var permissions = System.Text.Json.JsonDocument.Parse(await access.Content.ReadAsStringAsync());
            Assert.Equal("observer", permissions.RootElement.GetProperty("platform").GetProperty("role").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, permissions.RootElement.GetProperty("workspace").GetProperty("role").ValueKind);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/account/members")).StatusCode);
            using var discovery = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/v1/account"));
            Assert.Equal(0, discovery.RootElement.GetProperty("subscriptions").GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/v1/account/workspaces/{member.TenantId}/select", new { })).StatusCode);
            await using var membership = database.CreateCommand("INSERT INTO app.tenant_memberships(tenant_id, user_id, role) VALUES($1, $2, 'member')");
            membership.Parameters.AddWithValue(member.TenantId); membership.Parameters.AddWithValue(owner.UserId);
            await membership.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/v1/account/workspaces/{member.TenantId}/select", new { })).StatusCode);
        }
        finally { await Cleanup(database, owner, member); }
    }
}
