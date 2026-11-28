using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record KnowledgePackageRecord(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("witness_ids")] IReadOnlyList<string> WitnessIds,
    [property: JsonPropertyName("min_trust")] double MinTrust,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

internal sealed record KnowledgePackageGrant(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("granted_by")] string? GrantedBy,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt);

internal sealed record TenantKnowledgePackageView(
    KnowledgePackageRecord Package,
    bool Granted);

internal sealed record PackageScopeResolution(
    bool Authorized,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<Hash128> WitnessIds,
    double MinTrust);

internal interface IKnowledgePackageStore
{
    Task<IReadOnlyList<TenantKnowledgePackageView>> ListForTenantAsync(
        string tenantId, bool includeAll = false, CancellationToken ct = default);
    Task<KnowledgePackageRecord?> TryGetAsync(string packageId, CancellationToken ct = default);
    Task<KnowledgePackageRecord> UpsertPackageAsync(
        string packageId, string displayName, string visibility,
        IReadOnlyList<string> witnessIds, double minTrust = 0.0, CancellationToken ct = default);
    Task<KnowledgePackageGrant> GrantPackageAsync(
        string packageId, string tenantId, string? grantedBy, CancellationToken ct = default);
    Task<bool> RevokeGrantAsync(
        string packageId, string tenantId, CancellationToken ct = default);
    PackageScopeResolution ResolveForTenant(string tenantId, IReadOnlyList<string> packageIds);
    Task<PackageScopeResolution> ResolveForTenantAsync(
        string tenantId, IReadOnlyList<string> packageIds, CancellationToken ct = default);
}

internal sealed partial class KnowledgePackageService : IKnowledgePackageStore
{
    private static readonly HashSet<string> ReservedScopeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "world", "tenant", "private", "empty", "none", "deny"
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageSlugRegex();

    private readonly NpgsqlDataSource? _dataSource;
    private readonly ConcurrentDictionary<string, KnowledgePackageRecord> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, KnowledgePackageGrant>> _grants = new(StringComparer.OrdinalIgnoreCase);

    public KnowledgePackageService() : this((NpgsqlDataSource?)null) { }

    public KnowledgePackageService(NpgsqlDataSource? dataSource)
    {
        _dataSource = dataSource;
        SeedPublicPackages();
    }

    [ActivatorUtilitiesConstructor]
    public KnowledgePackageService(IServiceProvider services)
        : this(ResolvePostgresDataSource(services)) { }

    internal static NpgsqlDataSource? ResolvePostgresDataSource(IServiceProvider services)
    {
        var storeMode = services.GetService<BillingStoreMode>();
        if (!string.Equals(storeMode?.Mode, "postgres", StringComparison.OrdinalIgnoreCase))
            return null;
        return services.GetKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
    }

    private IReadOnlyList<string> QueryRealWitnessIds(params string[] keywords)
    {
        if (_dataSource is null) return [];
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT witness::text, COALESCE(name, '') FROM public.laplace_witnesses_named() ORDER BY readings DESC NULLS LAST, witness LIMIT 256", conn);
            using var reader = cmd.ExecuteReader();
            var matched = new List<string>();
            while (reader.Read())
            {
                string wid = reader.GetString(0).Trim();
                string name = reader.GetString(1);
                if (!Hash128.TryParseHex(wid, out var parsed)) continue;
                bool hit = keywords.Length == 0 || keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (hit) matched.Add(parsed.ToHex());
            }
            return matched.Distinct(StringComparer.Ordinal).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void SeedPublicPackages()
    {
        var seededAt = DateTimeOffset.UnixEpoch;
        var coreWitnesses = QueryRealWitnessIds("wordnet", "wikipedia", "wikidata", "wiktionary", "conceptnet", "framenet", "verbnet", "propbank", "universal", "cili", "iso", "unicode");
        if (coreWitnesses.Count == 0) coreWitnesses = QueryRealWitnessIds();
        if (coreWitnesses.Count == 0 && _dataSource is null)
            coreWitnesses = [DeterministicWitnessHex("laplace:package:core-open:wikipedia"), DeterministicWitnessHex("laplace:package:core-open:wikidata"), DeterministicWitnessHex("laplace:package:core-open:wordnet")];

        PutInMemorySeed(new KnowledgePackageRecord(
            PackageId: "core-open",
            DisplayName: "Core Open Knowledge",
            Visibility: "public",
            WitnessIds: coreWitnesses,
            MinTrust: 0.0,
            UpdatedAt: seededAt));

        var chessWitnesses = QueryRealWitnessIds("chess", "lichess", "fide", "syzygy", "eco", "pgn");
        if (chessWitnesses.Count == 0 && _dataSource is null)
            chessWitnesses = [DeterministicWitnessHex("laplace:package:domain-chess:fide"), DeterministicWitnessHex("laplace:package:domain-chess:lichess"), DeterministicWitnessHex("laplace:package:domain-chess:syzygy")];

        PutInMemorySeed(new KnowledgePackageRecord(
            PackageId: "domain-chess",
            DisplayName: "Chess Corpus & Syzygy",
            Visibility: "public",
            WitnessIds: chessWitnesses,
            MinTrust: 0.0,
            UpdatedAt: seededAt));

        var codeWitnesses = QueryRealWitnessIds("code", "workspace", "toolchain", "git", "tree-sitter", "laplace");
        if (codeWitnesses.Count == 0 && _dataSource is null)
            codeWitnesses = [DeterministicWitnessHex("laplace:package:domain-code:treesitter"), DeterministicWitnessHex("laplace:package:domain-code:toolchain")];

        PutInMemorySeed(new KnowledgePackageRecord(
            PackageId: "domain-code",
            DisplayName: "Code AST & Toolchain Verification",
            Visibility: "public",
            WitnessIds: codeWitnesses,
            MinTrust: 0.0,
            UpdatedAt: seededAt));
    }

    private void PutInMemorySeed(KnowledgePackageRecord record) => _packages[record.PackageId] = record;

    public static string DeterministicWitnessHex(string label) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes(label)).ToHex();

    public static string NormalizePackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("Field 'package_id' is required.", nameof(packageId));
        var normalized = packageId.Trim().ToLowerInvariant();
        if (!PackageSlugRegex().IsMatch(normalized))
            throw new ArgumentException(
                "Field 'package_id' must match [a-z0-9][a-z0-9._-]{0,127}.", nameof(packageId));
        if (ReservedScopeTokens.Contains(normalized))
            throw new ArgumentException(
                $"Package id '{normalized}' is a reserved authority scope keyword.", nameof(packageId));
        if (Hash128.TryParseHex(normalized, out _))
            throw new ArgumentException(
                "Package id cannot be a 32-hex entity id.", nameof(packageId));
        return normalized;
    }

    public static string NormalizeVisibility(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility))
            return "private";
        var normalized = visibility.Trim().ToLowerInvariant();
        if (normalized is not ("public" or "private"))
            throw new ArgumentException("Field 'visibility' must be 'public' or 'private'.", nameof(visibility));
        return normalized;
    }

    public static IReadOnlyList<string> NormalizeWitnessIds(IReadOnlyList<string>? witnessIds)
    {
        if (witnessIds is null || witnessIds.Count == 0)
            return Array.Empty<string>();
        var list = new List<string>(witnessIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in witnessIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!Hash128.TryParseHex(raw.Trim(), out var parsed))
                throw new ArgumentException($"Witness id '{raw}' must be a 32-hex entity id.", nameof(witnessIds));
            var hex = parsed.ToHex();
            if (seen.Add(hex))
                list.Add(hex);
        }
        return list;
    }

    public async Task<IReadOnlyList<TenantKnowledgePackageView>> ListForTenantAsync(
        string tenantId, bool includeAll = false, CancellationToken ct = default)
    {
        if (_dataSource is not null)
            return await ListForTenantPostgresAsync(tenantId, includeAll, ct).ConfigureAwait(false);

        var grantedSet = GetInMemoryGrants(tenantId);
        return _packages.Values
            .OrderBy(p => p.PackageId, StringComparer.Ordinal)
            .Select(p => new TenantKnowledgePackageView(
                p,
                string.Equals(p.Visibility, "public", StringComparison.OrdinalIgnoreCase) || grantedSet.Contains(p.PackageId)))
            .Where(v => includeAll || v.Granted)
            .ToArray();
    }

    public async Task<KnowledgePackageRecord?> TryGetAsync(string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;
        var key = packageId.Trim().ToLowerInvariant();
        if (_dataSource is not null)
            return await TryGetPostgresAsync(key, ct).ConfigureAwait(false);
        return _packages.TryGetValue(key, out var pkg) ? pkg : null;
    }

    public async Task<KnowledgePackageRecord> UpsertPackageAsync(
        string packageId, string displayName, string visibility,
        IReadOnlyList<string> witnessIds, double minTrust = 0.0, CancellationToken ct = default)
    {
        var id = NormalizePackageId(packageId);
        var name = string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim();
        if (name.Length > 160 || name.Any(char.IsControl))
            throw new ArgumentException("Field 'display_name' must be 1..160 non-control characters.", nameof(displayName));
        var vis = NormalizeVisibility(visibility);
        var witnesses = NormalizeWitnessIds(witnessIds);
        if (!double.IsFinite(minTrust) || minTrust < 0.0 || minTrust > 1.0)
            throw new ArgumentException("Field 'min_trust' must be finite and within [0, 1].", nameof(minTrust));

        var record = new KnowledgePackageRecord(id, name, vis, witnesses, minTrust, DateTimeOffset.UtcNow);
        if (_dataSource is not null)
            return await UpsertPackagePostgresAsync(record, ct).ConfigureAwait(false);

        _packages[id] = record;
        return record;
    }

    public async Task<KnowledgePackageGrant> GrantPackageAsync(
        string packageId, string tenantId, string? grantedBy, CancellationToken ct = default)
    {
        var id = NormalizePackageId(packageId);
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Field 'tenant_id' is required.", nameof(tenantId));
        var normalizedTenant = tenantId.Trim();

        if (_dataSource is not null)
            return await GrantPackagePostgresAsync(id, normalizedTenant, grantedBy?.Trim(), ct).ConfigureAwait(false);

        if (!_packages.ContainsKey(id))
            throw new KeyNotFoundException($"Knowledge package '{id}' does not exist.");

        var grant = new KnowledgePackageGrant(normalizedTenant, id, grantedBy?.Trim(), DateTimeOffset.UtcNow);
        var tenantGrants = _grants.GetOrAdd(normalizedTenant, _ => new ConcurrentDictionary<string, KnowledgePackageGrant>(StringComparer.OrdinalIgnoreCase));
        tenantGrants[id] = grant;
        return grant;
    }

    public async Task<bool> RevokeGrantAsync(
        string packageId, string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(tenantId))
            return false;
        var id = packageId.Trim().ToLowerInvariant();
        var normalizedTenant = tenantId.Trim();

        if (_dataSource is not null)
            return await RevokeGrantPostgresAsync(id, normalizedTenant, ct).ConfigureAwait(false);

        return _grants.TryGetValue(normalizedTenant, out var tenantGrants)
            && tenantGrants.TryRemove(id, out _);
    }

    public PackageScopeResolution ResolveForTenant(string tenantId, IReadOnlyList<string> packageIds)
    {
        if (_dataSource is null)
            return ResolveInMemory(tenantId, packageIds);

        return Task.Run(() => ResolveForTenantAsync(tenantId, packageIds, CancellationToken.None))
            .GetAwaiter().GetResult();
    }

    public async Task<PackageScopeResolution> ResolveForTenantAsync(
        string tenantId, IReadOnlyList<string> packageIds, CancellationToken ct = default)
    {
        if (_dataSource is null)
            return ResolveInMemory(tenantId, packageIds);

        if (packageIds is null || packageIds.Count == 0)
            return Denied("authority_empty_scope", "No knowledge packages specified; failing closed.");

        var normalized = NormalizeRequestedPackageIds(packageIds);
        var fetched = await FetchPackagesPostgresAsync(normalized, ct).ConfigureAwait(false);
        var grants = await FetchTenantGrantsPostgresAsync(tenantId, normalized, ct).ConfigureAwait(false);
        return EvaluateResolvedPackages(tenantId, normalized, fetched, grants);
    }

    private PackageScopeResolution ResolveInMemory(string tenantId, IReadOnlyList<string> packageIds)
    {
        if (packageIds is null || packageIds.Count == 0)
            return Denied("authority_empty_scope", "No knowledge packages specified; failing closed.");

        var normalized = NormalizeRequestedPackageIds(packageIds);
        var fetched = new Dictionary<string, KnowledgePackageRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in normalized)
        {
            if (_packages.TryGetValue(id, out var pkg))
                fetched[id] = pkg;
        }
        var grants = GetInMemoryGrants(tenantId);
        return EvaluateResolvedPackages(tenantId, normalized, fetched, grants);
    }

    private static PackageScopeResolution EvaluateResolvedPackages(
        string tenantId,
        IReadOnlyList<string> requestedIds,
        IReadOnlyDictionary<string, KnowledgePackageRecord> packages,
        ISet<string> grantedIds)
    {
        var resolvedWitnesses = new List<Hash128>();
        double maxMinTrust = 0.0;

        foreach (var id in requestedIds)
        {
            if (!packages.TryGetValue(id, out var pkg))
                return Denied("authority_package_denied",
                    $"Authority package '{id}' does not exist or is not accessible to workspace '{tenantId}'.");

            bool isPublic = string.Equals(pkg.Visibility, "public", StringComparison.OrdinalIgnoreCase);
            if (!isPublic && (string.IsNullOrWhiteSpace(tenantId) || !grantedIds.Contains(pkg.PackageId)))
                return Denied("authority_package_denied",
                    $"Workspace '{tenantId}' is not granted access to private authority package '{pkg.PackageId}'.");

            foreach (var w in pkg.WitnessIds)
            {
                if (Hash128.TryParseHex(w, out var parsed))
                    resolvedWitnesses.Add(parsed);
            }
            if (double.IsFinite(pkg.MinTrust))
                maxMinTrust = Math.Max(maxMinTrust, Math.Clamp(pkg.MinTrust, 0.0, 1.0));
        }

        var distinct = resolvedWitnesses.Distinct().ToArray();
        if (distinct.Length == 0)
            return Denied("authority_empty_scope",
                "Resolved authority package(s) produced zero permitted witnesses; failing closed.");

        return new PackageScopeResolution(true, null, null, distinct, maxMinTrust);
    }

    private static string[] NormalizeRequestedPackageIds(IReadOnlyList<string> packageIds) =>
        packageIds.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private HashSet<string> GetInMemoryGrants(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return _grants.TryGetValue(tenantId.Trim(), out var map)
            ? new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static PackageScopeResolution Denied(string code, string message) =>
        new(false, code, message, Array.Empty<Hash128>(), 0.0);

    private async Task<IReadOnlyList<TenantKnowledgePackageView>> ListForTenantPostgresAsync(
        string tenantId, bool includeAll, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT p.package_id, p.display_name, p.visibility, p.witness_ids, p.min_trust, p.updated_at,
                   (p.visibility = 'public' OR g.package_id IS NOT NULL) AS granted
            FROM app.knowledge_packages p
            LEFT JOIN app.tenant_knowledge_packages g
              ON g.package_id = p.package_id AND g.tenant_id = $1
            WHERE $2 OR p.visibility = 'public' OR g.package_id IS NOT NULL
            ORDER BY p.package_id
            """, conn);
        cmd.Parameters.AddWithValue(tenantId ?? string.Empty);
        cmd.Parameters.AddWithValue(includeAll);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TenantKnowledgePackageView>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var pkg = ReadPackage(reader);
            list.Add(new TenantKnowledgePackageView(pkg, reader.GetBoolean(6)));
        }
        return list;
    }

    private async Task<KnowledgePackageRecord?> TryGetPostgresAsync(string packageId, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT package_id, display_name, visibility, witness_ids, min_trust, updated_at
            FROM app.knowledge_packages
            WHERE package_id = $1
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue(packageId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    private async Task<KnowledgePackageRecord> UpsertPackagePostgresAsync(KnowledgePackageRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO app.knowledge_packages (package_id, display_name, visibility, witness_ids, min_trust, updated_at)
            VALUES ($1, $2, $3, $4, $5, now())
            ON CONFLICT (package_id) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                visibility = EXCLUDED.visibility,
                witness_ids = EXCLUDED.witness_ids,
                min_trust = EXCLUDED.min_trust,
                updated_at = now()
            RETURNING package_id, display_name, visibility, witness_ids, min_trust, updated_at
            """, conn);
        cmd.Parameters.AddWithValue(record.PackageId);
        cmd.Parameters.AddWithValue(record.DisplayName);
        cmd.Parameters.AddWithValue(record.Visibility);
        cmd.Parameters.AddWithValue(record.WitnessIds.ToArray());
        cmd.Parameters.AddWithValue(record.MinTrust);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadPackage(reader);
    }

    private async Task<KnowledgePackageGrant> GrantPackagePostgresAsync(
        string packageId, string tenantId, string? grantedBy, CancellationToken ct)
    {
        var existing = await TryGetPostgresAsync(packageId, ct).ConfigureAwait(false);
        if (existing is null)
            throw new KeyNotFoundException($"Knowledge package '{packageId}' does not exist.");

        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO app.tenant_knowledge_packages (tenant_id, package_id, granted_by, granted_at)
            VALUES ($1, $2, $3, now())
            ON CONFLICT (tenant_id, package_id) DO UPDATE SET
                granted_by = EXCLUDED.granted_by,
                granted_at = now()
            RETURNING tenant_id, package_id, granted_by, granted_at
            """, conn);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(packageId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)grantedBy ?? DBNull.Value
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new KnowledgePackageGrant(
            TenantId: reader.GetString(0),
            PackageId: reader.GetString(1),
            GrantedBy: reader.IsDBNull(2) ? null : reader.GetString(2),
            GrantedAt: reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<bool> RevokeGrantPostgresAsync(string packageId, string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM app.tenant_knowledge_packages WHERE tenant_id = $1 AND package_id = $2", conn);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(packageId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private async Task<Dictionary<string, KnowledgePackageRecord>> FetchPackagesPostgresAsync(
        string[] packageIds, CancellationToken ct)
    {
        var result = new Dictionary<string, KnowledgePackageRecord>(StringComparer.OrdinalIgnoreCase);
        if (packageIds.Length == 0)
            return result;

        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT package_id, display_name, visibility, witness_ids, min_trust, updated_at
            FROM app.knowledge_packages
            WHERE package_id = ANY($1)
            """, conn);
        cmd.Parameters.AddWithValue(packageIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var pkg = ReadPackage(reader);
            result[pkg.PackageId] = pkg;
        }
        return result;
    }

    private async Task<HashSet<string>> FetchTenantGrantsPostgresAsync(
        string tenantId, string[] packageIds, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tenantId) || packageIds.Length == 0)
            return result;

        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT package_id
            FROM app.tenant_knowledge_packages
            WHERE tenant_id = $1 AND package_id = ANY($2)
            """, conn);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(packageIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    private static KnowledgePackageRecord ReadPackage(NpgsqlDataReader reader) => new(
        PackageId: reader.GetString(0),
        DisplayName: reader.GetString(1),
        Visibility: reader.GetString(2),
        WitnessIds: reader.GetFieldValue<string[]>(3),
        MinTrust: reader.GetDouble(4),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(5));
}

internal sealed record WitnessPayeeRecord(
    [property: JsonPropertyName("witness_id")] string WitnessId,
    [property: JsonPropertyName("owner_tenant")] string? OwnerTenant,
    [property: JsonPropertyName("license_kind")] string LicenseKind,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

internal interface IWitnessPayeeStore
{
    Task<IReadOnlyList<WitnessPayeeRecord>> ListAsync(
        string? ownerTenant = null, string? witnessId = null, CancellationToken ct = default);
    Task<WitnessPayeeRecord?> TryGetAsync(string witnessId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, WitnessPayeeRecord>> GetByWitnessIdsAsync(
        IEnumerable<string> witnessIds, CancellationToken ct = default);
    Task<WitnessPayeeRecord> UpsertAsync(
        string witnessId, string? ownerTenant, string licenseKind, string? displayName,
        CancellationToken ct = default);
}

internal sealed class WitnessPayeeService : IWitnessPayeeStore
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ConcurrentDictionary<string, WitnessPayeeRecord> _payees = new(StringComparer.OrdinalIgnoreCase);

    public WitnessPayeeService() : this((NpgsqlDataSource?)null) { }

    public WitnessPayeeService(NpgsqlDataSource? dataSource) => _dataSource = dataSource;

    [ActivatorUtilitiesConstructor]
    public WitnessPayeeService(IServiceProvider services)
        : this(KnowledgePackageService.ResolvePostgresDataSource(services)) { }

    public static string NormalizeWitnessId(string? witnessId)
    {
        if (string.IsNullOrWhiteSpace(witnessId) || !Hash128.TryParseHex(witnessId.Trim(), out var parsed))
            throw new ArgumentException("Field 'witness_id' must be a 32-hex entity id.", nameof(witnessId));
        return parsed.ToHex();
    }

    public static string NormalizeLicenseKind(string? licenseKind)
    {
        if (string.IsNullOrWhiteSpace(licenseKind))
            return "royalty_bearing";
        var normalized = licenseKind.Trim().ToLowerInvariant();
        if (normalized is not ("royalty_bearing" or "open_license"))
            throw new ArgumentException(
                "Field 'license_kind' must be 'royalty_bearing' or 'open_license'.", nameof(licenseKind));
        return normalized;
    }

    public async Task<IReadOnlyList<WitnessPayeeRecord>> ListAsync(
        string? ownerTenant = null, string? witnessId = null, CancellationToken ct = default)
    {
        string? normalizedWitness = string.IsNullOrWhiteSpace(witnessId) ? null : NormalizeWitnessId(witnessId);
        string? normalizedOwner = string.IsNullOrWhiteSpace(ownerTenant) ? null : ownerTenant.Trim();

        if (_dataSource is not null)
            return await ListPostgresAsync(normalizedOwner, normalizedWitness, ct).ConfigureAwait(false);

        return _payees.Values
            .Where(p => normalizedWitness is null || string.Equals(p.WitnessId, normalizedWitness, StringComparison.OrdinalIgnoreCase))
            .Where(p => normalizedOwner is null
                        || string.Equals(p.OwnerTenant, normalizedOwner, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.LicenseKind, "open_license", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.WitnessId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<WitnessPayeeRecord?> TryGetAsync(string witnessId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(witnessId) || !Hash128.TryParseHex(witnessId.Trim(), out var parsed))
            return null;
        var key = parsed.ToHex();

        if (_dataSource is not null)
        {
            var map = await GetByWitnessIdsAsync([key], ct).ConfigureAwait(false);
            return map.TryGetValue(key, out var found) ? found : null;
        }
        return _payees.TryGetValue(key, out var item) ? item : null;
    }

    public async Task<IReadOnlyDictionary<string, WitnessPayeeRecord>> GetByWitnessIdsAsync(
        IEnumerable<string> witnessIds, CancellationToken ct = default)
    {
        var keys = witnessIds
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, WitnessPayeeRecord>(StringComparer.OrdinalIgnoreCase);
        if (keys.Length == 0)
            return result;

        if (_dataSource is not null)
            return await GetByWitnessIdsPostgresAsync(keys, ct).ConfigureAwait(false);

        foreach (var k in keys)
        {
            if (_payees.TryGetValue(k, out var item))
                result[k] = item;
        }
        return result;
    }

    public async Task<WitnessPayeeRecord> UpsertAsync(
        string witnessId, string? ownerTenant, string licenseKind, string? displayName,
        CancellationToken ct = default)
    {
        var id = NormalizeWitnessId(witnessId);
        var kind = NormalizeLicenseKind(licenseKind);
        string? owner = string.IsNullOrWhiteSpace(ownerTenant) ? null : ownerTenant.Trim();
        if (kind == "royalty_bearing" && string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Field 'owner_tenant' is required for royalty_bearing payees.", nameof(ownerTenant));
        if (kind == "open_license")
            owner = null;

        var name = string.IsNullOrWhiteSpace(displayName) ? (owner ?? id) : displayName.Trim();
        if (name.Length > 160 || name.Any(char.IsControl))
            throw new ArgumentException("Field 'display_name' must be 1..160 non-control characters.", nameof(displayName));

        var record = new WitnessPayeeRecord(id, owner, kind, name, DateTimeOffset.UtcNow);
        if (_dataSource is not null)
            return await UpsertPostgresAsync(record, ct).ConfigureAwait(false);

        _payees[id] = record;
        return record;
    }

    private async Task<IReadOnlyList<WitnessPayeeRecord>> ListPostgresAsync(
        string? ownerTenant, string? witnessId, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT witness_id, owner_tenant, license_kind, display_name, updated_at
            FROM app.witness_payees
            WHERE ($1::text IS NULL OR witness_id = $1)
              AND ($2::text IS NULL OR owner_tenant = $2 OR license_kind = 'open_license')
            ORDER BY witness_id
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)witnessId ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)ownerTenant ?? DBNull.Value
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<WitnessPayeeRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadPayee(reader));
        return list;
    }

    private async Task<IReadOnlyDictionary<string, WitnessPayeeRecord>> GetByWitnessIdsPostgresAsync(
        string[] keys, CancellationToken ct)
    {
        var result = new Dictionary<string, WitnessPayeeRecord>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT witness_id, owner_tenant, license_kind, display_name, updated_at
            FROM app.witness_payees
            WHERE witness_id = ANY($1)
            """, conn);
        cmd.Parameters.AddWithValue(keys);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var payee = ReadPayee(reader);
            result[payee.WitnessId] = payee;
        }
        return result;
    }

    private async Task<WitnessPayeeRecord> UpsertPostgresAsync(WitnessPayeeRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO app.witness_payees (witness_id, owner_tenant, license_kind, display_name, updated_at)
            VALUES ($1, $2, $3, $4, now())
            ON CONFLICT (witness_id) DO UPDATE SET
                owner_tenant = EXCLUDED.owner_tenant,
                license_kind = EXCLUDED.license_kind,
                display_name = EXCLUDED.display_name,
                updated_at = now()
            RETURNING witness_id, owner_tenant, license_kind, display_name, updated_at
            """, conn);
        cmd.Parameters.AddWithValue(record.WitnessId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)record.OwnerTenant ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue(record.LicenseKind);
        cmd.Parameters.AddWithValue(record.DisplayName);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadPayee(reader);
    }

    private static WitnessPayeeRecord ReadPayee(NpgsqlDataReader reader) => new(
        WitnessId: reader.GetString(0),
        OwnerTenant: reader.IsDBNull(1) ? null : reader.GetString(1),
        LicenseKind: reader.GetString(2),
        DisplayName: reader.GetString(3),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(4));
}

internal static class BillingServiceFallback
{
    private sealed class Holder(IServiceProvider sp)
    {
        private readonly Lazy<IKnowledgePackageStore> _packages = new(() =>
            new KnowledgePackageService(KnowledgePackageService.ResolvePostgresDataSource(sp)));
        private readonly Lazy<IWitnessPayeeStore> _payees = new(() =>
            new WitnessPayeeService(KnowledgePackageService.ResolvePostgresDataSource(sp)));
        private readonly Lazy<IPatreonEntitlementService> _patreon = new(() =>
            new PatreonEntitlementService(
                sp,
                sp.GetRequiredService<IBillingCatalog>(),
                sp.GetRequiredService<IBillingEntitlementStore>(),
                sp.GetRequiredService<IBillingWebhookEventStore>(),
                sp.GetService<IKnowledgePackageStore>() ?? GetPackages(sp),
                sp.GetService<Microsoft.Extensions.Options.IOptions<PatreonOptions>>()));

        public IKnowledgePackageStore Packages => _packages.Value;
        public IWitnessPayeeStore Payees => _payees.Value;
        public IPatreonEntitlementService Patreon => _patreon.Value;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IServiceProvider, Holder> Table = new();

    public static IKnowledgePackageStore? GetPackages(IServiceProvider? sp) =>
        sp is null ? null : sp.GetService<IKnowledgePackageStore>() ?? Table.GetValue(sp, static s => new Holder(s)).Packages;

    public static IWitnessPayeeStore? GetPayees(IServiceProvider? sp) =>
        sp is null ? null : sp.GetService<IWitnessPayeeStore>() ?? Table.GetValue(sp, static s => new Holder(s)).Payees;

    public static IPatreonEntitlementService? GetPatreon(IServiceProvider? sp) =>
        sp is null ? null : sp.GetService<IPatreonEntitlementService>() ?? Table.GetValue(sp, static s => new Holder(s)).Patreon;
}
