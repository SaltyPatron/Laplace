using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Laplace.SubstrateCRUD.Npgsql;
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

    private const string SelectColumns = """
        SELECT key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at
        FROM app.api_keys
        """;

    public Task PutAsync(ApiKeyRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.api_keys (key_hash, key_prefix, tenant, label, created_at, revoked_at, last_used_at)
            VALUES (@key_hash, @key_prefix, @tenant, @label, @created_at, @revoked_at, @last_used_at)
            ON CONFLICT (key_hash) DO NOTHING;
            """;
        return NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("key_hash", record.KeyHash);
            p.AddWithValue("key_prefix", record.KeyPrefix);
            p.AddWithValue("tenant", record.Tenant);
            p.AddWithValue("label", (object?)record.Label ?? DBNull.Value);
            p.AddWithValue("created_at", record.CreatedAt);
            p.AddWithValue("revoked_at", (object?)record.RevokedAt ?? DBNull.Value);
            p.AddWithValue("last_used_at", (object?)record.LastUsedAt ?? DBNull.Value);
        }, ct: ct);
    }

    public Task<ApiKeyRecord?> TryGetAsync(string keyHash, CancellationToken ct) =>
        NpgsqlRead.ReadFirstOrDefaultAsync(
            _dataSource, SelectColumns + " WHERE key_hash = @key_hash;", Map,
            p => p.AddWithValue("key_hash", keyHash), ct: ct);

    public Task<IReadOnlyList<ApiKeyRecord>> GetByTenantAsync(string tenant, CancellationToken ct) =>
        NpgsqlRead.ReadRowsAsync(
            _dataSource, SelectColumns + " WHERE tenant = @tenant ORDER BY created_at DESC;", Map,
            p => p.AddWithValue("tenant", tenant), ct: ct);

    public Task<IReadOnlyList<ApiKeyRecord>> GetByLabelAsync(string label, CancellationToken ct) =>
        NpgsqlRead.ReadRowsAsync(
            _dataSource, SelectColumns + " WHERE label = @label;", Map,
            p => p.AddWithValue("label", label), ct: ct);

    public async Task<bool> RevokeAsync(string keyHash, CancellationToken ct)
    {
        const string sql = """
            UPDATE app.api_keys SET revoked_at = now()
            WHERE key_hash = @key_hash AND revoked_at IS NULL;
            """;
        return await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql,
            p => p.AddWithValue("key_hash", keyHash), ct: ct) > 0;
    }

    public Task TouchAsync(string keyHash, DateTimeOffset usedAt, CancellationToken ct)
    {
        const string sql = "UPDATE app.api_keys SET last_used_at = @used_at WHERE key_hash = @key_hash;";
        return NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("key_hash", keyHash);
            p.AddWithValue("used_at", usedAt);
        }, ct: ct);
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
