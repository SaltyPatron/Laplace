using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Call any installed substrate operation by name. The catalog from
/// <c>ops.api()</c> is the allow-list — nothing outside it is callable.
/// MCP <c>op</c> and the OpenAI-compatible <c>POST /v1/op</c> both call through here,
/// so every surface reaches the same installed operation.
/// </summary>
public static class InstalledOpInvoker
{
    public const int DefaultRowCap = 200;
    public const int DefaultCommandTimeoutSeconds = 15;

    /// <summary>
    /// Installed operations permitted to run against a writable connection.
    ///
    /// Every other op resolves onto a <c>default_transaction_read_only=on</c>
    /// datasource, so appearing in the catalog is not enough to make a mutation
    /// callable; it must also be named here. Adding a write op to the substrate
    /// therefore does not make it reachable from a surface until it is listed.
    ///
    /// <c>ops.ingest_run_close</c> closes a run's journal row, which pipelines wait on.
    /// The cancel/terminate pair signals the backend itself, so a closed row never
    /// reads cancelled while the ingest keeps writing. The reindex/analyze pair repairs
    /// what <c>ops.index_health</c> reports. <c>ops.evict_source</c> deletes one
    /// source's attested testimony and refolds the consensus that survives; it is the
    /// only entry here that removes data (see <see cref="DestructiveOps"/>).
    /// </summary>
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

    /// <summary>
    /// Operations that destroy or rewrite stored testimony. Callers surface a
    /// confirmation for these; naming them here keeps that judgement in one place
    /// instead of a string check in each UI.
    /// </summary>
    public static readonly IReadOnlySet<string> DestructiveOps =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ops.evict_source",
        };

    public static bool IsDestructive(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DestructiveOps.Contains(name);

    /// <summary>True when <paramref name="name"/> may run against a writable connection.</summary>
    public static bool IsWritable(string? name) =>
        !string.IsNullOrWhiteSpace(name) && WritableOps.Contains(name);

    public sealed record OpParam(string Name, string Type, bool Optional);

    public sealed record OpResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        int? TruncatedAt,
        string? Error);

    internal static int RequestedRowCount(int maxRows) => Math.Max(0, maxRows);

    /// <summary>
    /// The caller's command budget, unchanged. Npgsql defines zero as no command
    /// timeout; cancellation remains available through the request token.
    /// </summary>
    public static int RequestedCommandTimeout(int timeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);
        return timeoutSeconds;
    }

    /// <summary>
    /// Resolve <paramref name="name"/> against the live catalog, bind named args
    /// with declared-type casts, and return rows. Callers pass a read-only data
    /// source unless the name is on the <see cref="WritableOps"/> allow-list.
    /// </summary>
    public static async Task<OpResult> InvokeAsync(
        NpgsqlDataSource db,
        string name,
        IReadOnlyDictionary<string, JsonNode?>? args,
        int maxRows = DefaultRowCap,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // maxRows is the caller's transport budget, kept exactly (a negative count
        // is an empty request). One extra row is fetched only to report truncation.
        var rowCap = RequestedRowCount(maxRows);
        var rowsToFetch = (long)rowCap + 1L;
        var keys = args?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];

        var catalog = await NpgsqlSubstrateReads.ApiCatalogAsync(db, name, ct)
            .ConfigureAwait(false);
        var overloads = catalog.Where(r => r.Name == name).ToArray();
        if (overloads.Length == 0)
        {
            var err = catalog.Count == 0
                ? $"no installed operation named '{name}', and nothing in the catalog matches that substring — try ops.api('<shorter substring>')"
                : $"no installed operation named '{name}' — did you mean: {string.Join(", ", catalog.Select(r => r.Name).Distinct())}";
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

        var isProcedure = string.Equals(candidates[0].Row.Kind, "procedure", StringComparison.Ordinal);
        var argText = string.Join(", ", call);

        // A procedure is CALLed and returns no result set; SELECT ... FROM it is a
        // parse error. LIMIT rowCap + 1 on the function path so truncation is observable.
        var sql = isProcedure
            ? $"CALL {QualifiedCatalogName(name)}({argText})"
            : $"SELECT * FROM {QualifiedCatalogName(name)}({argText}) LIMIT {rowsToFetch}";

        await using var cmd = db.CreateCommand(sql);
        // Positive values are exact caller policy. Zero means run until completion
        // or cancellation — required for maintenance procedures whose duration is
        // a property of live relation size, not a constant in this client.
        cmd.CommandTimeout = RequestedCommandTimeout(commandTimeoutSeconds);
        foreach (var (slot, value) in bound)
            cmd.Parameters.Add(BindArg(slot, value));

        if (isProcedure)
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            // A procedure's progress is in the server log (RAISE LOG), readable as
            // SQL through ops.app_log. Returning an empty row set would read as
            // "ran, found nothing"; this states that it ran.
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
    /// The plain quote toggle handles a doubled quote (<c>''</c>) with no escape case:
    /// <c>DEFAULT 'a''b, c'</c> does not split at the inner comma, because the pair
    /// toggles twice with no character between the quotes, so the momentary
    /// unquoted state is never observed by any other branch. Pinned by
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
    /// Turn the exact name returned by <c>ops.api()</c> into a qualified SQL
    /// identifier. Operations in the <c>laplace</c> schema are catalogued without a
    /// schema; purpose-schema and public operations are catalogued as
    /// <c>schema.function</c>. Quote the two identifiers separately — quoting the
    /// whole catalog name would look for a function literally named
    /// <c>ops.substrate_counts</c> inside <c>laplace</c>.
    /// </summary>
    internal static string QualifiedCatalogName(string catalogName)
    {
        var dot = catalogName.IndexOf('.');
        return dot < 0
            ? $"laplace.{QuoteIdent(catalogName)}"
            : $"{QuoteIdent(catalogName[..dot])}.{QuoteIdent(catalogName[(dot + 1)..])}";
    }

    /// <summary>
    /// Map one JSON argument to a value Npgsql can bind. A JSON array becomes a
    /// <see cref="string"/> array, which <see cref="BindArg"/> binds as a typed
    /// <c>text[]</c> — never a hand-composed <c>{a,b}</c> literal. Composing the
    /// literal mis-parses <em>silently</em>: an element holding a comma splits into
    /// two members, and braces, quotes, backslashes or edge whitespace shift the
    /// parse with no error. It also cannot express SQL NULL apart from the
    /// four-character string <c>NULL</c>. Binding removes the quoting question.
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
