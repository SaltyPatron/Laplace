using System.Data;
using Npgsql;
using NpgsqlTypes;

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
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
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
        if (policy.Owners.Count > 0)
        {
            await using var command = OwnerQuery(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM app.external_identities WHERE user_id = $1 AND (", "))");
            command.Parameters.Insert(0, new NpgsqlParameter<Guid> { TypedValue = user });
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return true;
        }
        await using var fallback = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM app.platform_role_events WHERE user_id = $1 AND actor = 'installation-owner' AND role = 'administrator')",
            connection, transaction);
        fallback.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = user });
        return (bool)(await fallback.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlySet<Guid>> OwnerUsersAsync(CancellationToken ct = default)
    {
        var result = new HashSet<Guid>();
        await using var connection = await database.OpenConnectionAsync(ct);
        if (policy.Owners.Count > 0)
        {
            await using var command = OwnerQuery(connection, null,
                "SELECT DISTINCT user_id FROM app.external_identities WHERE ", "", offset: 0);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        }
        await using var fallback = new NpgsqlCommand(
            "SELECT DISTINCT user_id FROM app.platform_role_events WHERE actor = 'installation-owner' AND role = 'administrator'",
            connection);
        await using var fallbackReader = await fallback.ExecuteReaderAsync(ct);
        while (await fallbackReader.ReadAsync(ct)) result.Add(fallbackReader.GetGuid(0));
        return result;
    }

    private NpgsqlCommand OwnerQuery(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string prefix, string suffix, int offset = 1)
    {
        var clauses = new List<string>();
        var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        foreach (var owner in policy.Owners)
        {
            clauses.Add($"(provider = ${offset + 1} AND issuer = ${offset + 2} AND subject = ${offset + 3})");
            command.Parameters.AddWithValue(owner.Provider);
            command.Parameters.AddWithValue(owner.Issuer);
            command.Parameters.AddWithValue(owner.Subject);
            offset += 3;
        }
        command.CommandText = prefix + string.Join(" OR ", clauses) + suffix;
        return command;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        // Same ordering as manual role changes. Login account creation commits before requesting this lock.
        await using var command = new NpgsqlCommand("LOCK TABLE app.platform_roles IN SHARE ROW EXCLUSIVE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PlatformOwner owner, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT user_id FROM app.external_identities
            WHERE provider = $1 AND issuer = $2 AND subject = $3
            """, connection, transaction);
        command.Parameters.AddWithValue(owner.Provider);
        command.Parameters.AddWithValue(owner.Issuer);
        command.Parameters.AddWithValue(owner.Subject);
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
            await using var command = new NpgsqlCommand("""
                WITH person AS (
                    INSERT INTO app.users(user_id, display_name, email) VALUES($1, $2, $3) RETURNING user_id
                ), workspace AS (
                    INSERT INTO app.tenants(tenant_id, display_name, kind) VALUES($4, $5, 'personal') RETURNING tenant_id
                ), binding AS (
                    INSERT INTO app.external_identities(identity_id, user_id, provider, issuer, subject, email, last_login_at)
                    SELECT $6, user_id, $7, $8, $9, $3, $10 FROM person RETURNING user_id
                )
                INSERT INTO app.tenant_memberships(tenant_id, user_id, role)
                SELECT workspace.tenant_id, binding.user_id, 'owner' FROM workspace CROSS JOIN binding
                """, connection, transaction);
            command.Parameters.AddWithValue(user);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)owner.DisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)owner.Email ?? DBNull.Value);
            command.Parameters.AddWithValue($"t-{Guid.NewGuid():N}");
            command.Parameters.AddWithValue(owner.DisplayName is { } name ? $"{name}'s workspace" : "Personal workspace");
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(owner.Provider);
            command.Parameters.AddWithValue(owner.Issuer);
            command.Parameters.AddWithValue(owner.Subject);
            command.Parameters.AddWithValue(owner.VerifiedAt);
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
        await using var command = new NpgsqlCommand("SELECT role FROM app.platform_roles WHERE user_id = $1", connection, transaction);
        command.Parameters.AddWithValue(user);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task GrantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, CancellationToken ct)
    {
        var previous = await RoleAsync(connection, transaction, user, ct);
        if (previous == "administrator") return;
        await using var command = new NpgsqlCommand("""
            WITH granted AS (
                INSERT INTO app.platform_roles(user_id, role) VALUES($1, 'administrator')
                ON CONFLICT(user_id) DO UPDATE SET role = excluded.role, updated_at = now()
                RETURNING user_id
            )
            INSERT INTO app.platform_role_events(user_id, actor, previous_role, role)
            SELECT user_id, 'installation-owner', $2, 'administrator' FROM granted
            """, connection, transaction);
        command.Parameters.AddWithValue(user);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)previous ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
