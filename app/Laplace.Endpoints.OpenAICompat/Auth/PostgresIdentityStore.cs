using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.Auth;

internal sealed class PostgresIdentityStore : IIdentityStore
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgresIdentityStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IdentityAccount> UpsertExternalIdentityAsync(ExternalIdentityProfile profile, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var existing = await ReadAccountAsync(connection, profile, ct);
        if (existing is not null)
        {
            await UpdateProfileAsync(connection, existing.UserId, profile, ct);
            return WithProfile(existing, profile);
        }

        var userId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var tenantId = $"t-{Guid.NewGuid():N}";
        var tenantName = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.Email ?? "Personal workspace" : $"{profile.DisplayName}'s workspace";
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = NpgsqlCatalog.Command(connection, transaction, "identity.create_account",
                userId, identityId, tenantId, tenantName, profile.Provider, profile.Issuer,
                profile.Subject, profile.Email, profile.DisplayName, profile.AvatarUrl);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(ct);
            var winner = await ReadAccountAsync(connection, profile, ct);
            if (winner is null) throw;
            await UpdateProfileAsync(connection, winner.UserId, profile, ct);
            return WithProfile(winner, profile);
        }
        return new IdentityAccount(userId, tenantId, "owner", profile.Provider,
            profile.Email, profile.DisplayName, profile.AvatarUrl);
    }

    private static IdentityAccount WithProfile(IdentityAccount account, ExternalIdentityProfile profile) => account with
    {
        Email = profile.Email ?? account.Email,
        DisplayName = profile.DisplayName ?? account.DisplayName,
        AvatarUrl = profile.AvatarUrl ?? account.AvatarUrl
    };

    private static async Task<IdentityAccount?> ReadAccountAsync(
        NpgsqlConnection connection, ExternalIdentityProfile profile, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, null, "identity.account",
            profile.Provider, profile.Issuer, profile.Subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new IdentityAccount(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task UpdateProfileAsync(
        NpgsqlConnection connection, Guid userId, ExternalIdentityProfile profile, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(connection, null, "identity.update_profile",
            userId, profile.Provider, profile.Issuer, profile.Subject, profile.Email, profile.DisplayName, profile.AvatarUrl);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertClientAsync(ExternalOidcProvider provider, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.upsert_client",
            provider.Scheme, provider.ClientId, provider.Authority);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task PutWebSessionAsync(StoredWebSession session, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.put_session",
            session.SessionId, session.UserId, session.TenantId, session.Ticket,
            session.CreatedAt, session.LastSeenAt, session.ExpiresAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredWebSession?> GetWebSessionAsync(string sessionId, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSession(reader) : null;
    }

    public async Task RevokeWebSessionAsync(string sessionId, Guid userId, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.revoke_session", sessionId, userId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StoredWebSession>> ListWebSessionsAsync(Guid userId, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.sessions", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var sessions = new List<StoredWebSession>();
        while (await reader.ReadAsync(ct)) sessions.Add(ReadSession(reader));
        return sessions;
    }

    private static StoredWebSession ReadSession(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetString(2),
        reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    public async Task UpsertConversationAsync(
        string tenantId, Guid userId, string sessionKey, string? title, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.upsert_conversation",
            tenantId, sessionKey, userId, title);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationSessionView>> ListConversationsAsync(
        Guid userId, string tenantId, CancellationToken ct)
    {
        await using var command = NpgsqlCatalog.Command(_dataSource, "identity.conversations", userId, tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var conversations = new List<ConversationSessionView>();
        while (await reader.ReadAsync(ct))
            conversations.Add(new ConversationSessionView(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return conversations;
    }
}
