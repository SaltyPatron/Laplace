using System.Text;
using System.Text.Json.Nodes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.Mcp;

/// <summary>
/// The MCP tool surface over the substrate's installed SQL functions. Typed
/// tools compose laplace.* helpers so bytea ids never cross the MCP boundary
/// (resolve/word_id/relation_type_id on the way in, realize/realize_path on
/// the way out); the sql tool is a read-only escape hatch to the whole api()
/// catalog. Two data sources: request/response-bounded for typed tools, and a
/// server-enforced read-only one (default_transaction_read_only) for sql.
/// </summary>
internal sealed class SubstrateTools
{
    private const int DefaultRowCap = 200;
    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlDataSource _dbReadOnly;

    public SubstrateTools()
    {
        var conn = LaplaceInstall.PostgresConnectionString();
        // The install default is the ingest string (unbounded Command Timeout);
        // this is a request/response surface — re-bound it, and let repeated
        // tool queries auto-prepare.
        _db = new NpgsqlDataSourceBuilder(
            conn + ";Command Timeout=30;Max Auto Prepare=50;Auto Prepare Min Usages=2").Build();
        _dbReadOnly = new NpgsqlDataSourceBuilder(
            conn + ";Command Timeout=20;Options='-c default_transaction_read_only=on -c statement_timeout=15000'").Build();
    }

    public JsonArray ListTools() => new(
        Tool("api",
            "Search the substrate's installed SQL function catalog (laplace.api). Returns name, args, returns for every function matching the substring. Use before assuming a helper doesn't exist.",
            Schema(("query", "string", "substring to match, '' lists everything", true))),
        Tool("sql",
            "Run a read-only SQL query against the substrate (schema laplace on the search_path). The whole api() catalog is callable. Enforced read-only with a 15s statement timeout; rows capped (default 200).",
            Schema(("query", "string", "SQL SELECT/WITH to execute", true),
                   ("max_rows", "integer", "row cap, default 200", false))),
        Tool("recall",
            "Ask the substrate a question in natural language (laplace.recall_session): definitions, translations, relations, walks. Returns reply rows with eff_mu (conservative Glicko-2 estimate) and witness counts.",
            Schema(("prompt", "string", "the question", true),
                   ("session", "string", "session key for follow-up context (pronouns, possessives)", false))),
        Tool("chat",
            "One conversational turn against the substrate (laplace.chat): walk-driven prose composed from rated consensus. Closes the loop: the prompt and reply are deposited as witnessed content (UserPrompt/Response trust classes) and folded, so the turn is visible to the next walk.",
            Schema(("prompt", "string", "the message", true),
                   ("session", "string", "session key for continuity", false))),
        Tool("walk",
            "Beam-walk the consensus graph from a prompt (laplace.walk_branches) and realize each path as text. Optionally constrain to one relation type (canonical name, e.g. IS_A).",
            Schema(("prompt", "string", "starting content", true),
                   ("relation_type", "string", "canonical relation name to constrain the walk", false),
                   ("depth", "integer", "walk depth, default 4", false),
                   ("breadth", "integer", "beam breadth, default 5", false))),
        Tool("facts",
            "Salient rated facts about a word (laplace.salient_facts): typed relations ranked by eff_mu with witness counts.",
            Schema(("term", "string", "the word", true),
                   ("limit", "integer", "max facts, default 24", false))),
        Tool("health",
            "Substrate health and inventory: laplace.substrate_health() plus laplace.substrate_counts().",
            Schema()));

    public (string Text, bool IsError) Call(string name, JsonObject? args)
    {
        try
        {
            return name switch
            {
                "api" => Rows(_dbReadOnly,
                    "SELECT name, args, returns FROM laplace.api(@q) ORDER BY name",
                    DefaultRowCap, ("q", Req(args, "query"))),
                "sql" => Rows(_dbReadOnly,
                    Req(args, "query"),
                    Int(args, "max_rows", DefaultRowCap)),
                "recall" => Rows(_db,
                    """
                    SELECT reply, round(eff_mu, 1) AS eff_mu, witnesses
                    FROM laplace.recall_session(@p,
                        CASE WHEN @s IS NULL THEN NULL ELSE convert_to(@s, 'UTF8') END)
                    """,
                    DefaultRowCap, ("p", Req(args, "prompt")), ("s", Opt(args, "session"))),
                "chat" => ChatTurn(args),
                "walk" => Rows(_dbReadOnly,
                    """
                    SELECT w.depth,
                           laplace.realize_path(w.path, w.types) AS path,
                           round(w.eff_mu, 1) AS eff_mu,
                           round(w.path_mu, 1) AS path_mu,
                           w.witnesses
                    FROM laplace.walk_branches(
                             laplace.resolve(@p),
                             CASE WHEN @t IS NULL THEN NULL ELSE laplace.relation_type_id(@t) END,
                             @depth, @breadth) w
                    ORDER BY w.depth, w.path_mu DESC
                    """,
                    DefaultRowCap,
                    ("p", Req(args, "prompt")), ("t", Opt(args, "relation_type")),
                    ("depth", Int(args, "depth", 4)), ("breadth", Int(args, "breadth", 5))),
                "facts" => Rows(_dbReadOnly,
                    """
                    SELECT type, fact, round(eff_mu, 1) AS eff_mu, witnesses
                    FROM laplace.salient_facts(laplace.word_id(@term), NULL, @limit)
                    """,
                    DefaultRowCap, ("term", Req(args, "term")), ("limit", Int(args, "limit", 24))),
                "health" => Rows(_dbReadOnly,
                    """
                    SELECT x.metric, x.value
                    FROM laplace.substrate_health() h,
                         LATERAL (VALUES ('ok', h.ok::text),
                                         ('fake_tier_bands', h.fake_tier_bands::text),
                                         ('identity_violations', h.identity_violations::text),
                                         ('bootstrap_entities', h.bootstrap_entities::text)) x(metric, value)
                    UNION ALL
                    SELECT metric, value::text FROM laplace.substrate_counts()
                    """,
                    DefaultRowCap),
                _ => ($"unknown tool: {name}", true),
            };
        }
        catch (PostgresException ex)
        {
            return ($"substrate error [{ex.SqlState}]: {ex.MessageText}", true);
        }
        catch (NpgsqlException ex)
        {
            return ($"substrate unavailable: {ex.Message}", true);
        }
        catch (ArgumentException ex)
        {
            return (ex.Message, true);
        }
    }

    // One conversational turn: SQL chat() composes the reply (read-side), then the
    // turn is deposited through the writer spine — full content mint + evidence +
    // inline fold under the UserPrompt/Response trust classes. This is chat()'s
    // OODA close; the SQL function itself stays read-only (session state aside).
    private ISubstrateWriter? _turnWriter;
    private bool _turnBootstrapped;
    private bool _turnDepositBroken;

    private (string, bool) ChatTurn(JsonObject? args)
    {
        var prompt = Req(args, "prompt");
        var session = Opt(args, "session");

        string? reply;
        using (var cmd = _db.CreateCommand(
            """
            SELECT laplace.chat(@p,
                CASE WHEN @s IS NULL THEN NULL ELSE convert_to(@s, 'UTF8') END)
            """))
        {
            cmd.Parameters.AddWithValue("p", prompt);
            if (session is null)
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
            else
                cmd.Parameters.AddWithValue("s", session);
            reply = cmd.ExecuteScalar() as string;
        }

        DepositTurn(prompt, reply);

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject { ["reply"] = reply })
        };
        return (result.ToJsonString(), false);
    }

    private void DepositTurn(string prompt, string? reply)
    {
        if (_turnDepositBroken)
            return;
        try
        {
            if (_turnWriter is null)
            {
                CodepointPerfcache.LoadDefault();
                _turnWriter = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }
            if (!_turnBootstrapped)
            {
                _turnWriter.ApplyAsync(UserPromptContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _turnWriter.ApplyAsync(ResponseContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _turnBootstrapped = true;
            }

            var promptRoot = Hash128.Zero;
            if (UserPromptContent.TryBuildWitnessChange(
                    Encoding.UTF8.GetBytes(prompt), "turn/prompt", out var promptChange, out var pr))
            {
                _turnWriter.ApplyAsync(promptChange).GetAwaiter().GetResult();
                promptRoot = pr;
            }
            if (!string.IsNullOrWhiteSpace(reply) &&
                ResponseContent.TryBuildWitnessChange(
                    Encoding.UTF8.GetBytes(reply), "turn/reply",
                    promptRoot == Hash128.Zero ? null : promptRoot, out var replyChange, out _))
            {
                _turnWriter.ApplyAsync(replyChange).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            // The reply still flows; the missing deposit is reported, not hidden.
            _turnDepositBroken = true;
            Console.Error.WriteLine($"laplace-mcp: turn deposit disabled: {ex.Message}");
        }
    }

    private (string, bool) Rows(NpgsqlDataSource source, string sql, int rowCap,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = source.CreateCommand(sql);
        foreach (var (pName, value) in parameters)
        {
            // A null optional is always a text arg here; DBNull without a
            // declared type leaves the parameter untyped at the server (42P08).
            if (value is null)
                cmd.Parameters.Add(new NpgsqlParameter(pName, NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
            else
                cmd.Parameters.AddWithValue(pName, value);
        }

        using var reader = cmd.ExecuteReader();
        var rows = new JsonArray();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count >= rowCap) { truncated = true; break; }
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = ToJson(reader.GetValue(i));
            rows.Add(row);
        }

        var result = new JsonObject { ["rows"] = rows };
        if (truncated) result["truncated_at"] = rowCap;
        return (result.ToJsonString(), false);
    }

    private static JsonNode? ToJson(object value) => value switch
    {
        DBNull => null,
        bool b => b,
        short s => s,
        int i => i,
        long l => l,
        decimal m => m,
        double d => d,
        float f => f,
        string s => s,
        byte[] bytes => @"\x" + Convert.ToHexStringLower(bytes),
        Array a => new JsonArray([.. a.Cast<object>().Select(ToJson)]),
        _ => value.ToString(),
    };

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (pName, type, description, isRequired) in props)
        {
            properties[pName] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired) required.Add(pName);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static string Req(JsonObject? args, string name) =>
        args?[name]?.GetValue<string>()
        ?? throw new ArgumentException($"missing required argument: {name}");

    private static string? Opt(JsonObject? args, string name) => args?[name]?.GetValue<string>();

    private static int Int(JsonObject? args, string name, int fallback) =>
        args?[name]?.GetValue<int>() ?? fallback;
}
