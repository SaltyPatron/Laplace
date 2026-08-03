using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Call any installed <c>laplace.*</c> operation by name. The catalog from
/// <c>laplace.api()</c> is the allow-list — nothing outside it is callable.
/// Shared by MCP <c>op</c> and OpenAICompat <c>POST /v1/op</c> (GH #812).
/// </summary>
public static class InstalledOpInvoker
{
    public const int DefaultRowCap = 200;

    public sealed record OpParam(string Name, string Type, bool Optional);

    public sealed record OpResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        int? TruncatedAt,
        string? Error);

    /// <summary>
    /// Resolve <paramref name="name"/> against the live catalog, bind named args
    /// with declared-type casts, and return rows under a read-only data source.
    /// </summary>
    public static async Task<OpResult> InvokeAsync(
        NpgsqlDataSource readOnlyDb,
        string name,
        IReadOnlyDictionary<string, JsonNode?>? args,
        int maxRows = DefaultRowCap,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var rowCap = Math.Clamp(maxRows, 1, 2000);
        var keys = args?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];

        var catalog = await NpgsqlSubstrateReads.ApiCatalogAsync(readOnlyDb, name, ct)
            .ConfigureAwait(false);
        var overloads = catalog.Where(r => r.Name == name).ToArray();
        if (overloads.Length == 0)
        {
            var err = catalog.Count == 0
                ? $"no installed operation named '{name}', and nothing in the catalog matches that substring — try api('<shorter substring>')"
                : $"no installed operation named '{name}' — did you mean: {string.Join(", ", catalog.Select(r => r.Name).Distinct().Take(8))}";
            return new OpResult([], null, err);
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

        var bound = new List<(string Slot, object? Value)>();
        var call = new List<string>();
        foreach (var p in chosen.Where(p => keys.Contains(p.Name)))
        {
            var slot = $"a{bound.Count}";
            call.Add($"{QuoteIdent(p.Name)} => @{slot}::{p.Type}");
            bound.Add((slot, OpValue(args![p.Name])));
        }

        // LIMIT rowCap + 1 so truncation is observable.
        var sql = $"SELECT * FROM laplace.{QuoteIdent(name)}({string.Join(", ", call)}) LIMIT {rowCap + 1}";
        await using var cmd = readOnlyDb.CreateCommand(sql);
        foreach (var (slot, value) in bound)
        {
            if (value is null)
                cmd.Parameters.Add(new NpgsqlParameter(slot, NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
            else
                cmd.Parameters.AddWithValue(slot, value);
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

            foreach (var mode in (string[])["VARIADIC ", "INOUT ", "OUT ", "IN "])
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

    private static object? OpValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => "{" + string.Join(",", a.Select(e => OpValue(e) as string ?? "NULL")) + "}",
        _ => node.ToJsonString(),
    };

    private static object? Normalize(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => @"\x" + Convert.ToHexStringLower(bytes),
        _ => value,
    };
}
