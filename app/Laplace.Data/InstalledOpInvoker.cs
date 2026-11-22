using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Data;

/// <summary>
/// Call any installed substrate operation by name from the <c>ops.api()</c> / public C catalog.
/// </summary>
public static class InstalledOpInvoker
{
    public const int DefaultRowCap = 200;
    public const int DefaultCommandTimeoutSeconds = 15;

    public static readonly IReadOnlySet<string> WritableOps =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ops.ingest_run_close",
            "ops.cancel_backend",
            "ops.terminate_backend",
            "ops.reindex_invalid",
            "ops.analyze_substrate",
            "ops.evict_source",
        };

    public static readonly IReadOnlySet<string> DestructiveOps =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ops.evict_source",
        };

    public static bool IsDestructive(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DestructiveOps.Contains(name);

    public static bool IsWritable(string? name) =>
        !string.IsNullOrWhiteSpace(name) && WritableOps.Contains(name);

    public sealed record OpParam(string Name, string Type, bool Optional);

    public sealed record OpResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        int? TruncatedAt,
        string? Error);

    internal static int RequestedRowCount(int maxRows) => Math.Max(0, maxRows);

    public static int RequestedCommandTimeout(int timeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);
        return timeoutSeconds;
    }

    private const int MaxApiCatalogCacheEntries = 4096;
    private static readonly ConcurrentDictionary<(string ConnectionString, string Name), NpgsqlSubstrateReads.ApiCatalogRow[]> ApiCatalogCache = new();

    private static void CacheCatalogOverloads(
        string connectionString, string name, NpgsqlSubstrateReads.ApiCatalogRow[] overloads)
    {
        if (overloads.Length == 0) return;
        if (ApiCatalogCache.Count >= MaxApiCatalogCacheEntries)
            ApiCatalogCache.Clear();
        ApiCatalogCache[(connectionString, name)] = overloads;
    }

    public static async Task<OpResult> InvokeAsync(
        NpgsqlDataSource db,
        string name,
        IReadOnlyDictionary<string, JsonNode?>? args,
        int maxRows = DefaultRowCap,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var rowCap = RequestedRowCount(maxRows);
        var rowsToFetch = (long)rowCap + 1L;
        var keys = args?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];

        NpgsqlConnection? connection = null;
        try
        {
            var cacheKey = (db.ConnectionString, name);
            if (!ApiCatalogCache.TryGetValue(cacheKey, out var overloads))
            {
                connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
                var catalog = await NpgsqlSubstrateReads.ApiCatalogAsync(connection, name, ct)
                    .ConfigureAwait(false);
                overloads = catalog.Where(r => r.Name == name).ToArray();
                if (overloads.Length == 0)
                {
                    var err = catalog.Count == 0
                        ? $"no installed operation named '{name}', and nothing in the catalog matches that substring — try ops.api('<shorter substring>')"
                        : $"no installed operation named '{name}' — did you mean: {string.Join(", ", catalog.Select(r => r.Name).Distinct())}";
                    return new OpResult([], null, err);
                }
                CacheCatalogOverloads(db.ConnectionString, name, overloads);
            }

            var candidates = overloads
                .Select(o => (Row: o, Params: ParseSignature(o.Args)))
                .Where(c => keys.All(k => c.Params.Any(p => p.Name == k))
                            && c.Params.All(p => p.Optional || keys.Contains(p.Name)))
                .ToArray();

            if (candidates.Length == 0)
                return new OpResult([], null,
                    $"'{name}' has no overload matching arguments [{string.Join(", ", keys)}]. Signatures: "
                    + string.Join(" | ", overloads.Select(o => $"{name}({o.Args})")));
            if (candidates.Length > 1)
                return new OpResult([], null,
                    $"'{name}' is ambiguous for arguments [{string.Join(", ", keys)}] — name more of them. Signatures: "
                    + string.Join(" | ", candidates.Select(c => $"{name}({c.Row.Args})")));

            var chosen = candidates[0].Params;
            if (chosen.Any(p => p.Name.Length == 0) && keys.Count > 0)
                return new OpResult([], null,
                    $"'{name}' has unnamed parameters and cannot be called by name: {name}({candidates[0].Row.Args})");

            var bound = new List<(string Name, string Type, NpgsqlParameter Parameter)>();
            foreach (var p in chosen.Where(p => keys.Contains(p.Name)))
            {
                var slot = $"a{bound.Count}";
                bound.Add((p.Name, p.Type,
                    BindArg(slot, CoerceForDeclaredType(OpValue(args![p.Name]), p.Type))));
            }

            connection ??= await db.OpenConnectionAsync(ct).ConfigureAwait(false);
            var isProcedure = string.Equals(candidates[0].Row.Kind, "procedure", StringComparison.Ordinal);
            await using var cmd = NpgsqlCatalog.InstalledOperationCommand(
                connection, null, isProcedure, QualifiedCatalogName(name), bound, rowsToFetch);
            cmd.CommandTimeout = RequestedCommandTimeout(commandTimeoutSeconds);
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
            if (isProcedure)
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new OpResult(
                    [new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["called"] = name,
                        ["kind"] = "procedure",
                        ["returns_rows"] = false,
                        ["progress"] = "RAISE LOG output — read it back through ops.app_log",
                    }],
                    null, null);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            int? truncatedAt = null;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows.Count >= rowCap)
                {
                    truncatedAt = rowCap;
                    break;
                }
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = Normalize(reader.GetValue(i));
                rows.Add(row);
            }
            return new OpResult(rows, truncatedAt, null);
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static List<OpParam> ParseSignature(string? argsText)
    {
        var result = new List<OpParam>();
        if (string.IsNullOrWhiteSpace(argsText)) return result;

        foreach (var part in SplitTopLevel(argsText))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;

            var optional = false;
            var d = text.IndexOf(" DEFAULT ", StringComparison.OrdinalIgnoreCase);
            if (d >= 0) { optional = true; text = text[..d].Trim(); }

            if (text.StartsWith("OUT ", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var mode in (string[])["VARIADIC ", "INOUT ", "IN "])
                if (text.StartsWith(mode, StringComparison.OrdinalIgnoreCase))
                    text = text[mode.Length..].TrimStart();

            var sp = text.IndexOf(' ');
            result.Add(sp < 0
                ? new OpParam(string.Empty, text, optional)
                : new OpParam(text[..sp], text[(sp + 1)..].Trim(), optional));
        }
        return result;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static string QuoteIdent(string ident) => '"' + ident.Replace("\"", "\"\"") + '"';

    internal static string QualifiedCatalogName(string catalogName)
    {
        var dot = catalogName.IndexOf('.');
        return dot < 0
            ? $"public.{QuoteIdent(catalogName)}"
            : $"{QuoteIdent(catalogName[..dot])}.{QuoteIdent(catalogName[(dot + 1)..])}";
    }

    internal static object? OpValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => a.Select(ElementText).ToArray(),
        _ => node.ToJsonString(),
    };

    private static string? ElementText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    internal static NpgsqlParameter BindArg(string slot, object? value) => value switch
    {
        null => new NpgsqlParameter(slot, NpgsqlDbType.Text) { Value = DBNull.Value },
        string?[] items => new NpgsqlParameter(slot, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items },
        _ => new NpgsqlParameter(slot, value),
    };

    internal static object? CoerceForDeclaredType(object? value, string declaredType)
    {
        if (string.Equals(declaredType, "bytea", StringComparison.OrdinalIgnoreCase) && value is string s)
            return CoerceHexBytea(s);
        if (string.Equals(declaredType, "bytea[]", StringComparison.OrdinalIgnoreCase) && value is string?[] arr)
            return arr.Select(item => item is null ? null : CoerceHexBytea(item)).ToArray();
        return value;
    }

    private static string CoerceHexBytea(string s)
    {
        if (s.StartsWith(@"\x", StringComparison.OrdinalIgnoreCase))
            return s;
        var trimmed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (trimmed.Length >= 32 && (trimmed.Length & 1) == 0 && trimmed.All(Uri.IsHexDigit))
            return @"\x" + trimmed;
        return s;
    }

    private static object? Normalize(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => @"\x" + Convert.ToHexStringLower(bytes),
        _ => value,
    };
}
