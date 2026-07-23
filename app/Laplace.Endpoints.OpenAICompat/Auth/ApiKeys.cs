using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.Auth;

public sealed record ApiKeyRecord(
    string KeyHash,
    string KeyPrefix,
    string Tenant,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt);

public sealed record IssuedApiKey(string Key, ApiKeyRecord Record);

public interface IApiKeyStore
{
    Task PutAsync(ApiKeyRecord record, CancellationToken ct);
    Task<ApiKeyRecord?> TryGetAsync(string keyHash, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyRecord>> GetByTenantAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyRecord>> GetByLabelAsync(string label, CancellationToken ct);
    Task<bool> RevokeAsync(string keyHash, CancellationToken ct);
    Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken ct);
}

internal sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<string, ApiKeyRecord> _keys = new(StringComparer.Ordinal);

    public Task PutAsync(ApiKeyRecord record, CancellationToken ct)
    {
        _keys[record.KeyHash] = record;
        return Task.CompletedTask;
    }

    public Task<ApiKeyRecord?> TryGetAsync(string keyHash, CancellationToken ct) =>
        Task.FromResult(_keys.TryGetValue(keyHash, out var record) ? record : null);

    public Task<IReadOnlyList<ApiKeyRecord>> GetByTenantAsync(string tenant, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ApiKeyRecord>>(_keys.Values
            .Where(k => string.Equals(k.Tenant, tenant, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.CreatedAt)
            .ToArray());

    public Task<IReadOnlyList<ApiKeyRecord>> GetByLabelAsync(string label, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ApiKeyRecord>>(_keys.Values
            .Where(k => string.Equals(k.Label, label, StringComparison.Ordinal))
            .ToArray());

    public Task<bool> RevokeAsync(string keyHash, CancellationToken ct)
    {
        if (!_keys.TryGetValue(keyHash, out var record) || record.RevokedAt is not null)
            return Task.FromResult(false);
        _keys[keyHash] = record with { RevokedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken ct)
    {
        if (_keys.TryGetValue(keyHash, out var record))
            _keys[keyHash] = record with { LastUsedAt = usedAt };
        return Task.CompletedTask;
    }
}

internal sealed class PostgresApiKeyStore : IApiKeyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresApiKeyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task PutAsync(ApiKeyRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.api_keys (key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at)
            VALUES (@key_hash, @key_prefix, @tenant, @label, @created_at, @revoked_at, @last_used_at)
            ON CONFLICT (key_hash) DO NOTHING;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key_hash", record.KeyHash);
        cmd.Parameters.AddWithValue("key_prefix", record.KeyPrefix);
        cmd.Parameters.AddWithValue("tenant", record.Tenant);
        cmd.Parameters.AddWithValue("label", (object?)record.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created_at", record.CreatedAt);
        cmd.Parameters.AddWithValue("revoked_at", (object?)record.RevokedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("last_used_at", (object?)record.LastUsedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ApiKeyRecord?> TryGetAsync(string keyHash, CancellationToken ct)
    {
        const string sql = """
            SELECT key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at
            FROM app.api_keys WHERE key_hash = @key_hash;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key_hash", keyHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        const string sql = """
            SELECT key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at
            FROM app.api_keys WHERE tenant = @tenant ORDER BY created_at DESC;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> GetByLabelAsync(string label, CancellationToken ct)
    {
        const string sql = """
            SELECT key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at
            FROM app.api_keys WHERE label = @label;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("label", label);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<bool> RevokeAsync(string keyHash, CancellationToken ct)
    {
        const string sql = """
            UPDATE app.api_keys SET revoked_at = now()
            WHERE key_hash = @key_hash AND revoked_at IS NULL;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key_hash", keyHash);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken ct)
    {
        const string sql = "UPDATE app.api_keys SET last_used_at = @used_at WHERE key_hash = @key_hash;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key_hash", keyHash);
        cmd.Parameters.AddWithValue("used_at", usedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<ApiKeyRecord>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var records = new List<ApiKeyRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            records.Add(Map(reader));
        return records;
    }

    private static ApiKeyRecord Map(NpgsqlDataReader reader) => new(
        KeyHash: reader.GetString(0),
        KeyPrefix: reader.GetString(1),
        Tenant: reader.GetString(2),
        Label: reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(4),
        RevokedAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        LastUsedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}

public interface IApiKeyService
{
    Task<IssuedApiKey> IssueAsync(string tenant, string? label, CancellationToken ct);
    Task<ApiKeyRecord?> ValidateAsync(string presentedKey, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string tenant, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyRecord>> FindByLabelAsync(string label, CancellationToken ct);
    Task<bool> RevokeByPrefixAsync(string tenant, string keyPrefix, CancellationToken ct);
}

internal sealed class ApiKeyService : IApiKeyService
{
    public const string KeyPrefix = "sk-laplace-";
    private const int PrefixDisplayLength = 16;

    private readonly IApiKeyStore _store;

    public ApiKeyService(IApiKeyStore store) => _store = store;

    public async Task<IssuedApiKey> IssueAsync(string tenant, string? label, CancellationToken ct)
    {
        var key = KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var record = new ApiKeyRecord(
            KeyHash: Hash(key),
            KeyPrefix: key[..PrefixDisplayLength],
            Tenant: tenant.Trim(),
            Label: label,
            CreatedAt: DateTimeOffset.UtcNow,
            RevokedAt: null,
            LastUsedAt: null);
        await _store.PutAsync(record, ct);
        return new IssuedApiKey(key, record);
    }

    public async Task<ApiKeyRecord?> ValidateAsync(string presentedKey, CancellationToken ct)
    {
        if (!presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return null;
        var record = await _store.TryGetAsync(Hash(presentedKey), ct);
        if (record is null || record.RevokedAt is not null)
            return null;
        await _store.TouchAsync(record.KeyHash, DateTimeOffset.UtcNow, ct);
        return record;
    }

    public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(string tenant, CancellationToken ct) =>
        _store.GetByTenantAsync(tenant, ct);

    public Task<IReadOnlyList<ApiKeyRecord>> FindByLabelAsync(string label, CancellationToken ct) =>
        _store.GetByLabelAsync(label, ct);

    public async Task<bool> RevokeByPrefixAsync(string tenant, string keyPrefix, CancellationToken ct)
    {
        var keys = await _store.GetByTenantAsync(tenant, ct);
        var match = keys.FirstOrDefault(k =>
            k.RevokedAt is null && string.Equals(k.KeyPrefix, keyPrefix, StringComparison.Ordinal));
        return match is not null && await _store.RevokeAsync(match.KeyHash, ct);
    }

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
