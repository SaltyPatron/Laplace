using System.Data;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>Projects private installation owner authority into the application database.</summary>
public sealed class PlatformOwnerReconciler(NpgsqlDataSource database, PlatformOwnerPolicy policy)
{
    private (string[] Providers, string[] Issuers, string[] Subjects) OwnerBindingColumns()
    {
        var count = policy.Owners.Count;
        var providers = new string[count];
        var issuers = new string[count];
        var subjects = new string[count];
        for (int i = 0; i < count; i++)
        {
            var owner = policy.Owners[i];
            providers[i] = owner.Provider;
            issuers[i] = owner.Issuer;
            subjects[i] = owner.Subject;
        }
        return (providers, issuers, subjects);
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (policy.Owners.Count == 0) return;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);

        var (providers, issuers, subjects) = OwnerBindingColumns();
        var bindings = await FindBatchAsync(connection, transaction, providers, issuers, subjects, ct);
        var toGrant = new Dictionary<Guid, string?>();

        for (int i = 0; i < policy.Owners.Count; i++)
        {
            var binding = bindings[i];
            if (binding.UserId is { } existingUser)
            {
                if (binding.Role != "administrator")
                    toGrant.TryAdd(existingUser, binding.Role);
                continue;
            }

            var provisioned = await ProvisionAsync(connection, transaction, policy.Owners[i], ct);
            if (provisioned.Role != "administrator")
                toGrant.TryAdd(provisioned.UserId, provisioned.Role);
        }

        if (toGrant.Count > 0)
            await GrantBatchAsync(connection, transaction, toGrant, ct);

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

        var (providers, issuers, subjects) = OwnerBindingColumns();
        var bindings = await FindBatchAsync(connection, transaction, providers, issuers, subjects, ct);
        for (int i = 0; i < policy.Owners.Count; i++)
        {
            if (bindings[i].UserId is null || bindings[i].Role != "administrator")
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
        var (providers, issuers, subjects) = OwnerBindingColumns();
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            connection, transaction, "owner.is_owner_batch", ct,
            user, providers, issuers, subjects);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlySet<Guid>> OwnerUsersAsync(CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        await using var connection = await database.OpenConnectionAsync(ct);
        var (providers, issuers, subjects) = OwnerBindingColumns();
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            connection, null, "owner.list_owner_users_batch", ct,
            providers, issuers, subjects);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // Same ordering as manual role changes. Login account creation commits before requesting this lock.
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(connection, transaction, "owner.lock_platform_roles", ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(Guid? UserId, string? Role)[]> FindBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string[] providers, string[] issuers, string[] subjects, CancellationToken ct)
    {
        var rows = new (Guid? UserId, string? Role)[providers.Length];
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(
            connection, transaction, "owner.find_bindings_batch", ct,
            providers, issuers, subjects);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int idx = reader.GetInt32(0) - 1;
            if ((uint)idx < (uint)rows.Length)
                rows[idx] = (
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return rows;
    }

    private static async Task<Guid?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        PlatformOwner owner, CancellationToken ct)
    {
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(
            connection, transaction, "owner.find_binding", ct,
            owner.Provider, owner.Issuer, owner.Subject);
        return await command.ExecuteScalarAsync(ct) is Guid user ? user : null;
    }

    private static async Task<(Guid UserId, string? Role)> ProvisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PlatformOwner owner, CancellationToken ct)
    {
        // These UUIDs are relational storage handles, never provider or canonical content identities.
        var user = Guid.NewGuid();
        await transaction.SaveAsync("owner_provision", ct);
        try
        {
            await using var command = await NpgsqlCatalog.PreparedCommandAsync(
                connection,
                transaction,
                "owner.provision",
                ct,
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
            return (user, null);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A real sign-in may have created this binding while the restore waited. Keep that account intact.
            await transaction.RollbackAsync("owner_provision", ct);
            var existing = await FindAsync(connection, transaction, owner, ct)
                ?? throw new InvalidOperationException("Installation owner provisioning conflicted with existing account state.");
            var role = await RoleAsync(connection, transaction, existing, ct);
            return (existing, role);
        }
    }

    private static async Task<string?> RoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, CancellationToken ct)
    {
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(connection, transaction, "owner.get_role", ct, user);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task GrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, CancellationToken ct)
    {
        var previous = await RoleAsync(connection, transaction, user, ct);
        if (previous == "administrator") return;
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(
            connection, transaction, "owner.grant_administrator", ct,
            user, (object?)previous ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task GrantBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyDictionary<Guid, string?> users, CancellationToken ct)
    {
        var userIds = new Guid[users.Count];
        var prevRoles = new string?[users.Count];
        int i = 0;
        foreach (var (userId, prevRole) in users)
        {
            userIds[i] = userId;
            prevRoles[i] = prevRole;
            i++;
        }
        await using var command = await NpgsqlCatalog.PreparedCommandAsync(
            connection, transaction, "owner.grant_administrators_batch", ct,
            userIds, prevRoles);
        await command.ExecuteNonQueryAsync(ct);
    }
}
