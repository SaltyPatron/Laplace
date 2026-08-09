using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Call any installed operation by name. The catalog from <c>ops.api()</c> is
/// the allow-list — nothing outside it is callable. Catalog names are bare for
/// <c>laplace.*</c> and <c>schema.proname</c> elsewhere (see <c>ops.api</c>).
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
    /// Accepts catalog names (<c>ops.source_counts</c>) or a bare proname that
    /// uniquely suffix-matches one catalog entry.
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
        var overloads = ResolveOverloads(catalog, name);
        if (overloads.Length == 0)
        {
            var err = catalog.Count == 0
                ? $"no installed operation named '{name}', and nothing in the catalog matches that substring — try api('<shorter substring>')"
                : $"no installed operation named '{name}' — did you mean: {string.Join(", ", catalog.Select(r => r.Name).Distinct().Take(8))}";
            return new OpResult([], null, err);
        }

        var callableName = overloads[0].Name;
        var candidates = overloads
            .Select(o => (Row: o, Params: ParseSignature(o.Args)))
            .Where(c => keys.All(k => c.Params.Any(p => p.Name == k))
                        && c.Params.All(p => p.Optional || keys.Contains(p.Name)))
            .ToArray();

        if (candidates.Length == 0)
            return new OpResult([], null,
                $"'{callableName}' has no overload matching arguments [{string.Join(", ", keys)}]. Signatures: "
                + string.Join(" | ", overloads.Select(o => $"{o.Name}({o.Args})")));
        if (candidates.Length > 1)
            return new OpResult([], null,
                $"'{callableName}' is ambiguous for arguments [{string.Join(", ", keys)}] — name more of them. Signatures: "
                + string.Join(" | ", candidates.Select(c => $"{c.Row.Name}({c.Row.Args})")));

        var chosen = candidates[0].Params;
        callableName = candidates[0].Row.Name;
        if (chosen.Any(p => p.Name.Length == 0) && keys.Count > 0)
            return new OpResult([], null,
                $"'{callableName}' has unnamed parameters and cannot be called by name: {callableName}({candidates[0].Row.Args})");

        var bound = new List<(string Slot, object? Value)>();
        var call = new List<string>();
        foreach (var p in chosen.Where(p => keys.Contains(p.Name)))
        {
            var slot = $"a{bound.Count}";
            call.Add($"{QuoteIdent(p.Name)} => @{slot}::{p.Type}");
            bound.Add((slot, OpValue(args![p.Name])));
        }

        // LIMIT rowCap + 1 so truncation is observable.
        var sql = $"SELECT * FROM {QualifyCallable(callableName)}({string.Join(", ", call)}) LIMIT {rowCap + 1}";
        await using var cmd = readOnlyDb.CreateCommand(sql);
        foreach (var (slot, value) in bound)
            cmd.Parameters.Add(BindArg(slot, value));

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

    /// <summary>
    /// Split an argument list on top-level commas.
    ///
    /// The plain toggle handles a doubled quote (<c>''</c>) correctly and needs no
    /// escape case — reviewers have flagged it twice (GH #843) on the theory that
    /// <c>DEFAULT 'a''b, c'</c> splits at the inner comma. It does not. The pair
    /// toggles twice with no character between the quotes, so the momentary
    /// unquoted state is never observed by any other branch; an explicit
    /// doubled-quote arm is byte-for-byte equivalent on every input. Pinned by
    /// <c>ParseSignature_DoubledQuoteInDefaultDoesNotSplitTheParameter</c>.
    ///
    /// What this does NOT handle: dollar-quoting and <c>E'\''</c> backslash escapes.
    /// <c>pg_get_function_arguments</c> emits neither, so neither reaches here.
    /// </summary>
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

    /// <summary>
    /// Match catalog rows by exact name, or — when <paramref name="name"/> is bare —
    /// by a unique <c>schema.name</c> suffix. Ambiguous bare names return empty so the
    /// caller surfaces the catalog suggestions.
    /// </summary>
    private static NpgsqlSubstrateReads.ApiCatalogRow[] ResolveOverloads(
        IReadOnlyList<NpgsqlSubstrateReads.ApiCatalogRow> catalog, string name)
    {
        var exact = catalog.Where(r => r.Name == name).ToArray();
        if (exact.Length > 0 || name.Contains('.', StringComparison.Ordinal))
            return exact;

        var suffix = "." + name;
        var bySuffix = catalog.Where(r => r.Name.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        var distinct = bySuffix.Select(r => r.Name).Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 1 ? bySuffix : [];
    }

    /// <summary>
    /// Turn a catalog name into a SQL callable ref. Bare names are <c>laplace</c>
    /// (identity helpers); qualified names quote each part.
    /// </summary>
    private static string QualifyCallable(string catalogName)
    {
        var parts = catalogName.Split('.');
        return parts.Length switch
        {
            1 => $"laplace.{QuoteIdent(parts[0])}",
            2 => $"{QuoteIdent(parts[0])}.{QuoteIdent(parts[1])}",
            _ => throw new ArgumentException(
                $"catalog name '{catalogName}' is not schema.function or bare proname",
                nameof(catalogName)),
        };
    }

    /// <summary>
    /// Map one JSON argument to a value Npgsql can bind. A JSON array becomes a
    /// <see cref="string"/> array, which <see cref="BindArg"/> binds as a typed
    /// <c>text[]</c> — never a hand-composed <c>{a,b}</c> literal. Composing the
    /// literal mis-parses <em>silently</em>: an element holding a comma splits into
    /// two members, and braces, quotes, backslashes or edge whitespace shift the
    /// parse with no error. It also cannot express SQL NULL apart from the
    /// four-character string <c>NULL</c>. Binding removes the quoting question
    /// rather than answering it (GH #843).
    /// </summary>
    internal static object? OpValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => a.Select(ElementText).ToArray(),
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// One array element as text. JSON null stays <see langword="null"/> so it reaches
    /// the server as SQL NULL, distinct from the string <c>"NULL"</c>.
    /// </summary>
    private static string? ElementText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// Bind one argument. Arrays carry an explicit element type so the server parses
    /// the array from the binary protocol; nulls carry <c>text</c> because an untyped
    /// <c>DBNull</c> leaves the parameter undeclared at the server (42P08). The
    /// declared-type cast in the call text converts from there.
    /// </summary>
    internal static NpgsqlParameter BindArg(string slot, object? value) => value switch
    {
        null => new NpgsqlParameter(slot, NpgsqlDbType.Text) { Value = DBNull.Value },
        string?[] items => new NpgsqlParameter(slot, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items },
        _ => new NpgsqlParameter(slot, value),
    };

    private static object? Normalize(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => @"\x" + Convert.ToHexStringLower(bytes),
        _ => value,
    };
}
