using Laplace.Endpoints.OpenAICompat.Auth;
using Npgsql;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class PlatformInitialRoleDatabaseTests
{
    private static NpgsqlDataSource Database()
    {
        var connection = Environment.GetEnvironmentVariable("LAPLACE_TEST_APP_DB");
        Skip.If(string.IsNullOrWhiteSpace(connection), "LAPLACE_TEST_APP_DB must select the isolated platform-access test database.");
        return NpgsqlDataSource.Create(connection!);
    }

    private static async Task<Guid> Person(NpgsqlDataSource database)
    {
        var user = Guid.NewGuid();
        await using var command = database.CreateCommand("INSERT INTO app.users(user_id) VALUES($1)");
        command.Parameters.AddWithValue(user);
        await command.ExecuteNonQueryAsync();
        return user;
    }

    private static async Task Cleanup(NpgsqlDataSource database, Guid user)
    {
        foreach (var sql in new[] {
            "DELETE FROM app.platform_role_events WHERE user_id = $1",
            "DELETE FROM app.users WHERE user_id = $1"
        })
        {
            await using var command = database.CreateCommand(sql);
            command.Parameters.AddWithValue(user);
            await command.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task InitialAssignmentIsAuditedAndARepeatIsRefused()
    {
        await using var database = Database();
        var user = await Person(database);
        var store = new PostgresPlatformAccessStore(database);
        try
        {
            await store.SetRoleAsync(user, "administrator", null, default, onlyIfNeverAssigned: true);
            Assert.Equal("administrator", await store.RoleAsync(user, default));
            var created = Assert.Single(await store.EventsAsync(default), e => e.UserId == user);
            Assert.Null(created.PreviousRole);
            Assert.Equal("administrator", created.Role);
            Assert.Equal("installation-credential", created.Actor);
            await Assert.ThrowsAsync<AccessChangeException>(() =>
                store.SetRoleAsync(user, "administrator", null, default, onlyIfNeverAssigned: true));
            Assert.Equal(created, Assert.Single(await store.EventsAsync(default), e => e.UserId == user));
        }
        finally { await Cleanup(database, user); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task RemovedRoleHistoryCannotBeReenrolledAsAnInitialAssignment()
    {
        await using var database = Database();
        var user = await Person(database);
        var store = new PostgresPlatformAccessStore(database);
        try
        {
            await store.SetRoleAsync(user, "observer", null, default);
            await store.SetRoleAsync(user, null, null, default);
            Assert.Null(await store.RoleAsync(user, default));
            var before = (await store.EventsAsync(default)).Where(e => e.UserId == user).ToArray();
            Assert.Equal(2, before.Length);
            await Assert.ThrowsAsync<AccessChangeException>(() =>
                store.SetRoleAsync(user, "administrator", null, default, onlyIfNeverAssigned: true));
            Assert.Null(await store.RoleAsync(user, default));
            Assert.Equal(before, (await store.EventsAsync(default)).Where(e => e.UserId == user).ToArray());
        }
        finally { await Cleanup(database, user); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task ExistingUnauditedRoleCannotBeOverwrittenAsAnInitialAssignment()
    {
        await using var database = Database();
        var user = await Person(database);
        var store = new PostgresPlatformAccessStore(database);
        try
        {
            await using var seed = database.CreateCommand("INSERT INTO app.platform_roles(user_id, role) VALUES($1, 'observer')");
            seed.Parameters.AddWithValue(user);
            await seed.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<AccessChangeException>(() =>
                store.SetRoleAsync(user, "administrator", null, default, onlyIfNeverAssigned: true));
            Assert.Equal("observer", await store.RoleAsync(user, default));
            Assert.DoesNotContain(await store.EventsAsync(default), e => e.UserId == user);
        }
        finally { await Cleanup(database, user); }
    }

    [SkippableFact, Trait("Tier", "db")]
    public async Task ConcurrentInitialAssignmentsProduceOneGrantAndOneAuditEvent()
    {
        await using var database = Database();
        var user = await Person(database);
        var store = new PostgresPlatformAccessStore(database);
        try
        {
            async Task<bool> Assign()
            {
                try
                {
                    await store.SetRoleAsync(user, "administrator", null, default, onlyIfNeverAssigned: true);
                    return true;
                }
                catch (AccessChangeException) { return false; }
            }
            var results = await Task.WhenAll(Assign(), Assign());
            Assert.Single(results, succeeded => succeeded);
            Assert.Equal("administrator", await store.RoleAsync(user, default));
            Assert.Single(await store.EventsAsync(default), e => e.UserId == user);
        }
        finally { await Cleanup(database, user); }
    }
}
