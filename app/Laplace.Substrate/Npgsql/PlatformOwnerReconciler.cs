using System.Data;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>Projects private installation owner authority into the application database.</summary>
public sealed class PlatformOwnerReconciler(NpgsqlDataSource database, PlatformOwnerPolicy policy)
{
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (policy.Owners.Count == 0) return;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        foreach (var owner in policy.Owners)
        {
            var user = await FindAsync(connection, transaction, owner, ct)
                ?? await ProvisionAsync(connection, transaction, owner, ct);
            await GrantAsync(connection, transaction, user, ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>Read-only acceptance check; never provisions accounts or repairs grants.</summary>
    public async Task VerifyAsync(CancellationToken ct = default)
    {
        if (policy.Owners.Count == 0) return;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = NpgsqlCatalog.Command(connection, transaction, "owner.set_read_only"))
            await readOnly.ExecuteNonQueryAsync(ct);
        foreach (var owner in policy.Owners)
        {
            var user = await FindAsync(connection, transaction, owner, ct);
            if (user is null || await RoleAsync(connection, transaction, user.Value, ct) != "administrator")
                throw new InvalidOperationException("Configured installation owner access is not reconciled.");
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>Called after a validated login has committed its account, before issuing its ticket.</summary>
    public async Task ReconcileUserAsync(Guid user, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await ReconcileUserAsync(connection, user, ct);
    }

    public async Task ReconcileUserAsync(NpgsqlConnection connection, Guid user, CancellationToken ct = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        if (await IsOwnerAsync(connection, transaction, user, ct))
            await GrantAsync(connection, transaction, user, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsOwnerAsync(Guid user, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        return await IsOwnerAsync(connection, null, user, ct);
    }

    public async Task<bool> IsOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid user, CancellationToken ct = default)
    {
        foreach (var owner in policy.Owners)
        {
            await using var cmd = NpgsqlCatalog.Command(
                connection, transaction, "owner.is_binding_for_user",
                user, owner.Provider, owner.Issuer, owner.Subject);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return true;
        }
        await using var fallback = NpgsqlCatalog.Command(connection, transaction, "owner.is_event_owner", user);
        return (bool)(await fallback.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlySet<Guid>> OwnerUsersAsync(CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        await using var connection = await database.OpenConnectionAsync(ct);
        foreach (var owner in policy.Owners)
        {
            var user = await FindAsync(connection, null, owner, ct);
            if (user is not null) result.Add(user.Value);
        }
        await using var fallback = NpgsqlCatalog.Command(connection, null, "owner.list_event_owners");
        await using var fallbackReader = await fallback.ExecuteReaderAsync(ct);
        while (await fallbackReader.ReadAsync(ct)) result.Add(fallbackReader.GetGuid(0));
        return result;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // Same ordering as manual role changes. Login account creation commits before requesting this lock.
        await using var command = NpgsqlCatalog.Command(connection, transaction, "owner.lock_platform_roles");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        PlatformOwner owner, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(
            connection, transaction, "owner.find_binding",
            owner.Provider, owner.Issuer, owner.Subject);
        return await command.ExecuteScalarAsync(ct) is Guid user ? user : null;
    }

    private static async Task<Guid> ProvisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PlatformOwner owner, CancellationToken ct)
    {
        // These UUIDs are relational storage handles, never provider or canonical content identities.
        var user = Guid.NewGuid();
        await transaction.SaveAsync("owner_provision", ct);
        try
        {
            await using var command = NpgsqlCatalog.Command(
                connection,
                transaction,
                "owner.provision",
                user,
                (object?)owner.DisplayName ?? DBNull.Value,
                (object?)owner.Email ?? DBNull.Value,
                $"t-{Guid.NewGuid():N}",
                owner.DisplayName is { } name ? $"{name}'s workspace" : "Personal workspace",
                Guid.NewGuid(),
                owner.Provider,
                owner.Issuer,
                owner.Subject,
                owner.VerifiedAt);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.ReleaseAsync("owner_provision", ct);
            return user;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A real sign-in may have created this binding while the restore waited. Keep that account intact.
            await transaction.RollbackAsync("owner_provision", ct);
            return await FindAsync(connection, transaction, owner, ct)
                ?? throw new InvalidOperationException("Installation owner provisioning conflicted with existing account state.");
        }
    }

    private static async Task<string?> RoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, transaction, "owner.get_role", user);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task GrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, CancellationToken ct)
    {
        var previous = await RoleAsync(connection, transaction, user, ct);
        if (previous == "administrator") return;
        await using var command = NpgsqlCatalog.Command(
            connection, transaction, "owner.grant_administrator",
            user, (object?)previous ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
