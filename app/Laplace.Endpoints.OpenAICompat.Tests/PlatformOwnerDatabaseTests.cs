using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PlatformOwnerDatabaseTests
{
    private static NpgsqlDataSource Database()
    {
        var connection = Environment.GetEnvironmentVariable("LAPLACE_TEST_APP_DB");
        Skip.If(string.IsNullOrWhiteSpace(connection), "LAPLACE_TEST_APP_DB must select the isolated platform-access test database.");
        return NpgsqlDataSource.Create(connection!);
    }

    private static async Task<object?> Scalar(NpgsqlDataSource database, string sql, params object[] values)
    {
        await using var command = database.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<Guid> User(NpgsqlDataSource database, string subject) => (Guid)(await Scalar(database,
        "SELECT user_id FROM app.external_identities WHERE provider='google' AND issuer='https://accounts.google.com' AND subject=$1", subject))!;

    private static async Task InsertBinding(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, string subject)
    {
        await using var command = new NpgsqlCommand("""
            WITH person AS (INSERT INTO app.users(user_id, email) VALUES($1, 'fixture@example.test') RETURNING user_id)
            INSERT INTO app.external_identities(identity_id,user_id,provider,issuer,subject,last_login_at)
            SELECT $2,user_id,'google','https://accounts.google.com',$3,'2025-06-01T00:00:00Z' FROM person
            """, connection, transaction);
        command.Parameters.AddWithValue(user);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(subject);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Cleanup(NpgsqlDataSource database, string subject)
    {
        var value = await Scalar(database, "SELECT user_id FROM app.external_identities WHERE provider='google' AND issuer='https://accounts.google.com' AND subject=$1", subject);
        if (value is not Guid user) return;
        var workspace = await Scalar(database, "SELECT m.tenant_id FROM app.tenant_memberships m JOIN app.tenants t USING(tenant_id) WHERE m.user_id=$1 AND t.kind='personal'", user);
        await Scalar(database, "DELETE FROM app.platform_role_events WHERE user_id=$1", user);
        await Scalar(database, "DELETE FROM app.users WHERE user_id=$1", user);
        if (workspace is string tenant) await Scalar(database, "DELETE FROM app.tenants WHERE tenant_id=$1", tenant);
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task ProvisioningRestoresVerifiedBindingAndAdminWithoutPretendingToSignIn()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        try
        {
            await owners.ReconcileAsync();
            var user = await User(database, subject);
            await owners.VerifyAsync();
            await owners.ReconcileAsync();
            await owners.ReconcileUserAsync(user);
            Assert.Equal("administrator", await Scalar(database, "SELECT role FROM app.platform_roles WHERE user_id=$1", user));
            Assert.Equal(PlatformOwnerPolicyTests.VerifiedAt.UtcDateTime, await Scalar(database, "SELECT last_login_at FROM app.external_identities WHERE user_id=$1", user));
            Assert.Equal(0L, await Scalar(database, "SELECT count(*) FROM app.web_sessions WHERE user_id=$1", user));
            Assert.Equal(1L, await Scalar(database, "SELECT count(*) FROM app.tenant_memberships WHERE user_id=$1 AND role='owner'", user));
            Assert.Equal(1L, await Scalar(database, "SELECT count(*) FROM app.platform_role_events WHERE user_id=$1 AND actor='installation-owner' AND previous_role IS NULL AND role='administrator'", user));
            Assert.True(await owners.IsOwnerAsync(user));
            Assert.Contains(user, await owners.OwnerUsersAsync());
        }
        finally { await Cleanup(database, subject); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task ExistingOwnerDataAndRevocationHistoryArePreservedWhileAuthorityIsRestored()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        try
        {
            await owners.ReconcileAsync();
            var user = await User(database, subject);
            var tenant = await Scalar(database, "SELECT tenant_id FROM app.tenant_memberships WHERE user_id=$1", user);
            await Scalar(database, "UPDATE app.users SET display_name='Current profile',email='current@example.test' WHERE user_id=$1", user);
            await Scalar(database, "UPDATE app.external_identities SET last_login_at='2025-06-01T00:00:00Z' WHERE user_id=$1", user);
            await Scalar(database, "DELETE FROM app.platform_roles WHERE user_id=$1", user);
            await Scalar(database, "INSERT INTO app.platform_role_events(user_id,actor,previous_role,role) VALUES($1,'historical-revocation','administrator',NULL)", user);
            await owners.ReconcileAsync();
            Assert.Equal(user, await User(database, subject));
            Assert.Equal(tenant, await Scalar(database, "SELECT tenant_id FROM app.tenant_memberships WHERE user_id=$1", user));
            Assert.Equal("Current profile", await Scalar(database, "SELECT display_name FROM app.users WHERE user_id=$1", user));
            Assert.Equal("current@example.test", await Scalar(database, "SELECT email FROM app.users WHERE user_id=$1", user));
            Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), await Scalar(database, "SELECT last_login_at FROM app.external_identities WHERE user_id=$1", user));
            Assert.Equal(3L, await Scalar(database, "SELECT count(*) FROM app.platform_role_events WHERE user_id=$1", user));
        }
        finally { await Cleanup(database, subject); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task OwnerDowngradeIsRejectedAndOrdinaryRevocationRemainsEffective()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        var store = new PostgresPlatformAccessStore(database, owners);
        var ordinary = Guid.NewGuid();
        try
        {
            await owners.ReconcileAsync();
            var user = await User(database, subject);
            await Assert.ThrowsAsync<AccessChangeException>(() => store.SetRoleAsync(user, null, null, default));
            await Assert.ThrowsAsync<AccessChangeException>(() => store.SetRoleAsync(user, "operator", null, default));
            Assert.True(Assert.Single(await store.PeopleAsync(user.ToString(), default)).InstallationOwner);
            await Scalar(database, "INSERT INTO app.users(user_id) VALUES($1)", ordinary);
            await Scalar(database, "INSERT INTO app.external_identities(identity_id,user_id,provider,issuer,subject,email) VALUES($1,$2,'google','https://accounts.google.com',$3,'fixture@example.test')",
                Guid.NewGuid(), ordinary, subject + "-different-account");
            await store.SetRoleAsync(ordinary, "observer", null, default);
            await store.SetRoleAsync(ordinary, null, null, default);
            await owners.ReconcileUserAsync(ordinary);
            Assert.Null(await store.RoleAsync(ordinary, default));
            Assert.Equal(2L, await Scalar(database, "SELECT count(*) FROM app.platform_role_events WHERE user_id=$1", ordinary));
            await Scalar(database, "DELETE FROM app.platform_roles WHERE user_id=$1", user);
            Assert.Equal("administrator", await store.RoleAsync(user, default));
        }
        finally
        {
            await Cleanup(database, subject);
            await Scalar(database, "DELETE FROM app.platform_role_events WHERE user_id=$1", ordinary);
            await Scalar(database, "DELETE FROM app.users WHERE user_id=$1", ordinary);
        }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task ConcurrentRestorationAndLoginReconciliationProduceOneGrant()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owners.ReconcileAsync()));
            var user = await User(database, subject);
            await Task.WhenAll(owners.ReconcileAsync(), owners.ReconcileUserAsync(user));
            Assert.Equal(1L, await Scalar(database, "SELECT count(*) FROM app.external_identities WHERE provider='google' AND subject=$1", subject));
            Assert.Equal(1L, await Scalar(database, "SELECT count(*) FROM app.platform_role_events WHERE user_id=$1", user));
        }
        finally { await Cleanup(database, subject); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task VerificationIsReadOnlyAndPolicySurvivesAccountRecreation()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => owners.VerifyAsync());
            Assert.Equal(0L, await Scalar(database, "SELECT count(*) FROM app.external_identities WHERE subject=$1", subject));
            await owners.ReconcileAsync();
            await Cleanup(database, subject);
            await owners.ReconcileAsync();
            await owners.VerifyAsync();
            var user = await User(database, subject);
            Assert.Equal(PlatformOwnerPolicyTests.VerifiedAt.UtcDateTime, await Scalar(database, "SELECT last_login_at FROM app.external_identities WHERE user_id=$1", user));
            Assert.Equal(0L, await Scalar(database, "SELECT count(*) FROM app.web_sessions WHERE user_id=$1", user));
        }
        finally { await Cleanup(database, subject); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task LoginReconciliationReusesItsCommittedConnectionWithSingleConnectionPool()
    {
        await using var fixture = Database();
        var selection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1, Timeout = 2 };
        await using var database = NpgsqlDataSource.Create(selection.ConnectionString);
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var owners = new PlatformOwnerReconciler(database, PlatformOwnerPolicyTests.Policy(subject));
        var user = Guid.NewGuid();
        try
        {
            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await InsertBinding(connection, transaction, user, subject);
                await transaction.CommitAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await owners.ReconcileUserAsync(connection, user, timeout.Token);
            }
            Assert.Equal("administrator", await Scalar(database, "SELECT role FROM app.platform_roles WHERE user_id=$1", user));
            Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), await Scalar(database, "SELECT last_login_at FROM app.external_identities WHERE user_id=$1", user));
        }
        finally { await Cleanup(database, subject); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task RestoreWaitsForConcurrentBindingCreationAndKeepsTheRealLoginAccount()
    {
        await using var database = Database();
        var subject = $"owner-test-{Guid.NewGuid():N}";
        var selection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { ApplicationName = $"owner-race-{Guid.NewGuid():N}", MaxPoolSize = 1 };
        await using var restoreDatabase = NpgsqlDataSource.Create(selection.ConnectionString);
        var owners = new PlatformOwnerReconciler(restoreDatabase, PlatformOwnerPolicyTests.Policy(subject));
        var user = Guid.NewGuid();
        Task? restoring = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await InsertBinding(connection, transaction, user, subject);
                restoring = owners.ReconcileAsync(deadline.Token);
                // Observe the unique-binding wait before committing, so this exercises the savepoint race path.
                while (!Equals(true, await Scalar(database,
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock')", selection.ApplicationName)))
                {
                    if (restoring.IsCompleted) await restoring;
                    await Task.Delay(10, deadline.Token);
                }
                await transaction.CommitAsync();
            }
            await restoring;
            Assert.Equal(user, await User(database, subject));
            Assert.Equal("fixture@example.test", await Scalar(database, "SELECT email FROM app.users WHERE user_id=$1", user));
            Assert.Equal(1L, await Scalar(database, "SELECT count(*) FROM app.platform_role_events WHERE user_id=$1", user));
            Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), await Scalar(database, "SELECT last_login_at FROM app.external_identities WHERE user_id=$1", user));
        }
        finally
        {
            deadline.Cancel();
            if (restoring is not null) { try { await restoring; } catch (OperationCanceledException) { } }
            await Cleanup(database, subject);
        }
    }
}
