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
            "Ask the substrate about a topic (laplace.recall_session). A bare prompt gets the default read — gloss then the strongest chain — with session topic carry. There is NO English question routing (the regex router was removed): for a specific read shape use the `query` tool instead. Returns reply rows with eff_mu (conservative Glicko-2 estimate) and witness counts.",
            Schema(("prompt", "string", "the topic (a word or phrase; phrasing is not parsed)", true),
                   ("session", "string", "session key for topic carry across turns", false))),
        Tool("query",
            "A structural read (laplace.recall_intent): the caller names the SHAPE — define, what_is, describe, synonyms, translate, languages, examples, related, related_in, is_a, reason, walk, complete, fallback (SELECT * FROM laplace.query_shapes() for the live list). Language-agnostic by construction: nothing is inferred from phrasing. related/related_in need relation_type (canonical, e.g. HAS_PART); is_a/reason need topic2; translate accepts lang.",
            Schema(("shape", "string", "the read shape (see query_shapes())", true),
                   ("topic", "string", "the subject — word, phrase, or hex entity id", true),
                   ("topic2", "string", "second topic for is_a / reason", false),
                   ("relation_type", "string", "canonical relation for related / related_in", false),
                   ("lang", "string", "target language for translate", false))),
        Tool("taxonomy",
            "The IS_A tree around a topic: dir='up' rows climb the parent chain to the root (via walk_strongest over the IS_A arena, from the topic's top synset — taxonomy lives on concepts, not spellings), dir='child' rows are the strongest sub-kinds. Every row carries the entity id to continue from.",
            Schema(("term", "string", "the topic (omit if entity given)", false),
                   ("entity", "string", "hex entity id to root at", false))),
        Tool("translate",
            "Cross-lingual surfaces for a topic (laplace.translations): the ILI hub meshing languages — OMW multilingual lemmas converging on the same concept ids. Each row is a surface + its language, rated.",
            Schema(("term", "string", "the topic", true),
                   ("limit", "integer", "max rows, default 24", false))),
        Tool("leaders",
            "Per-band leaderboards (laplace.consensus_band_edges): the strongest consensus edges in each salience band, fully labeled. Bands 0-12 (1 definitional, 2 taxonomic, 3 equivalence, 4 partitive, 5 causal, 6 oppositional, 7 associative, 9 lexical, 11 standards); SELECT * FROM laplace.relation_bands() for live counts.",
            Schema(("bands", "string", "comma-separated band numbers, default '1,2,4,5'", false),
                   ("per_band", "integer", "rows per band, default 5", false))),
        Tool("chat",
            "One conversational turn against the substrate (laplace.chat): walk-driven prose composed from rated consensus. Structural steering, no phrasing tricks: shape names the read, bands lenses it (e.g. '4' parts, '2' kinds, '5' causes), elaborate advances fact layers on a carried topic. Closes the loop: prompt and reply deposit as witnessed content (UserPrompt/Response trust classes) and fold, so the turn is visible to the next walk.",
            Schema(("prompt", "string", "the message", true),
                   ("session", "string", "session key for continuity", false),
                   ("shape", "string", "optional read shape (see query_shapes())", false),
                   ("bands", "string", "optional comma-separated salience bands to lens the reply", false),
                   ("elaborate", "boolean", "advance to the next fact layer of the carried topic", false))),
        Tool("witness",
            "Deposit a fact into the substrate as witnessed content (the write lane). The text is minted as content-addressed entities through the writer spine under the UserPrompt trust class — outranked by curated sources BY DESIGN, one voice among many — and folds immediately, so the very next walk/recall can read it. Returns the minted root id. This is how an agent remembers something for every other agent.",
            Schema(("text", "string", "the fact/note to witness (plain prose)", true),
                   ("origin", "string", "provenance tag, default 'agent/note'", false))),
        Tool("feedback",
            "Confirm or refute a claim (the Gödel-engine feedback lane, same implementation as the CLI attest). Terms resolve at the SURFACE/word layer — use bubble first when the claim lives on a synset/hub (same text renders at three layers; feedback lands where you aim it). Triple mode: subject + relation (canonical, e.g. IS_A, RELATED_TO) + object — a confirm is a Glicko win for the edge, a refute is a loss that can drive it signed-negative until walks drop it. Chain mode: tokens (comma-separated, 2+) attest PRECEDES pairs. Folds immediately; returns consensus before/after so you can watch the rating move.",
            Schema(("verdict", "string", "'confirm' or 'refute'", true),
                   ("subject", "string", "triple mode: subject term", false),
                   ("relation", "string", "triple mode: canonical relation type", false),
                   ("object", "string", "triple mode: object term", false),
                   ("tokens", "string", "chain mode: comma-separated tokens (2+)", false))),
        Tool("walk",
            "Beam-walk the consensus graph from a prompt (laplace.walk_branches) and realize each path as text. Optionally constrain to one relation type (canonical name, e.g. IS_A). Pass entity (hex id from bubble) to start from a resolved node rather than re-resolving text.",
            Schema(("prompt", "string", "starting content (omit if entity given)", false),
                   ("entity", "string", "hex entity id to start from, e.g. from bubble", false),
                   ("relation_type", "string", "canonical relation name to constrain the walk", false),
                   ("depth", "integer", "walk depth, default 4", false),
                   ("breadth", "integer", "beam breadth, default 5", false))),
        Tool("bubble",
            "Bubble a surface term up the mesh to the highway (laplace.bubble_up): surface -> sense -> synset, then the hub above it (IS_INSTANCE_OF/IS_A) and every relation channel available there with edge counts. Returns entity ids, so the next step continues from where this one landed instead of re-entering from text. Use this before facts/walk when a term may resolve at the wrong layer — all three layers render with the SAME text, so a query aimed at the wrong one returns zero rows and looks like missing knowledge.",
            Schema(("term", "string", "the surface word or phrase", true),
                   ("k", "integer", "sense frontier width, default 5", false))),
        Tool("facts",
            "Salient rated facts about a word (laplace.salient_facts): typed relations ranked by eff_mu with witness counts. Pass entity (hex id from bubble/walk) to read facts at a specific mesh layer instead of resolving text at the surface.",
            Schema(("term", "string", "the word (omit if entity given)", false),
                   ("entity", "string", "hex entity id to read from, e.g. from bubble", false),
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
                "witness" => WitnessFact(args),
                "feedback" => Feedback(args),
                "query" => Rows(_db,
                    """
                    SELECT reply, round(eff_mu, 1) AS eff_mu, witnesses
                    FROM laplace.recall_intent(@shape, laplace.resolve_ref(@topic),
                        CASE WHEN @t2 IS NULL THEN NULL ELSE laplace.resolve_ref(@t2) END,
                        @rt, @lang, NULL)
                    """,
                    DefaultRowCap,
                    ("shape", Req(args, "shape")), ("topic", Req(args, "topic")),
                    ("t2", Opt(args, "topic2")), ("rt", Opt(args, "relation_type")),
                    ("lang", Opt(args, "lang"))),
                "taxonomy" => Rows(_dbReadOnly,
                    """
                    WITH node AS (SELECT CASE WHEN @e IS NULL THEN laplace.resolve_ref(@term)
                                              ELSE decode(@e, 'hex') END AS id)
                    SELECT t.dir, t.ord, encode(t.id, 'hex') AS entity, t.label,
                           round(t.eff_mu, 1) AS eff_mu
                    FROM node, laplace.taxonomy_tree(node.id) t
                    ORDER BY t.dir DESC, t.ord
                    """,
                    DefaultRowCap, ("term", NodeText(args, "term")), ("e", Opt(args, "entity"))),
                "translate" => Rows(_db,
                    """
                    SELECT t.translation, t.language, t.eff_mu, t.witnesses
                    FROM laplace.translations(laplace.resolve_ref(@term), @limit) t
                    """,
                    DefaultRowCap, ("term", Req(args, "term")), ("limit", Int(args, "limit", 24))),
                "leaders" => Rows(_dbReadOnly,
                    """
                    SELECT band, subject, relation, object, eff_mu, witnesses
                    FROM laplace.band_leaders(string_to_array(@bands, ',')::int[], @per)
                    """,
                    DefaultRowCap,
                    ("bands", Opt(args, "bands") ?? "1,2,4,5"), ("per", Int(args, "per_band", 5))),
                "walk" => Rows(_dbReadOnly,
                    """
                    WITH node AS (SELECT CASE WHEN @e IS NULL THEN laplace.resolve(@p)
                                              ELSE decode(@e, 'hex') END AS id)
                    SELECT w.depth,
                           laplace.realize_path(w.path, w.types) AS path,
                           round(w.eff_mu, 1) AS eff_mu,
                           round(w.path_mu, 1) AS path_mu,
                           w.witnesses
                    FROM node, laplace.walk_branches(
                             node.id,
                             CASE WHEN @t IS NULL THEN NULL ELSE laplace.relation_type_id(@t) END,
                             @depth, @breadth) w
                    ORDER BY w.depth, w.path_mu DESC
                    """,
                    DefaultRowCap,
                    ("p", NodeText(args, "prompt")), ("e", Opt(args, "entity")),
                    ("t", Opt(args, "relation_type")),
                    ("depth", Int(args, "depth", 4)), ("breadth", Int(args, "breadth", 5))),
                "facts" => Rows(_dbReadOnly,
                    """
                    WITH node AS (SELECT CASE WHEN @e IS NULL THEN laplace.word_id(@term)
                                              ELSE decode(@e, 'hex') END AS id)
                    SELECT encode(node.id, 'hex') AS entity,
                           f.type, f.fact, round(f.eff_mu, 1) AS eff_mu, f.witnesses
                    FROM node, laplace.salient_facts(node.id, NULL, @limit) f
                    """,
                    DefaultRowCap, ("term", NodeText(args, "term")), ("e", Opt(args, "entity")),
                    ("limit", Int(args, "limit", 24))),
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
        var shape = Opt(args, "shape");
        var bands = Opt(args, "bands");
        var elaborate = args?["elaborate"]?.GetValue<bool>() ?? false;

        string? reply;
        using (var cmd = _db.CreateCommand(
            """
            SELECT laplace.chat(@p,
                CASE WHEN @s IS NULL THEN NULL ELSE convert_to(@s, 'UTF8') END,
                NULL, @shape,
                CASE WHEN @bands IS NULL THEN NULL ELSE string_to_array(@bands, ',')::int[] END,
                NULL, NULL, NULL, @elab)
            """))
        {
            cmd.Parameters.AddWithValue("p", prompt);
            if (session is null)
                cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
            else
                cmd.Parameters.AddWithValue("s", session);
            cmd.Parameters.Add(new NpgsqlParameter("shape", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)shape ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("bands", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)bands ?? DBNull.Value });
            cmd.Parameters.AddWithValue("elab", elaborate);
            reply = cmd.ExecuteScalar() as string;
        }

        DepositTurn(prompt, reply);

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject { ["reply"] = reply })
        };
        return (result.ToJsonString(), false);
    }

    // The agent write lane: mint a note as witnessed content and fold it, so the
    // substrate is the shared memory between every agent on this repo. Same spine
    // and trust class as a chat turn; the note is one outrankable voice, not truth.
    private (string, bool) WitnessFact(JsonObject? args)
    {
        var text = Req(args, "text");
        var origin = Opt(args, "origin") ?? "agent/note";

        EnsureWriter();
        if (_turnDepositBroken)
            return ("witness lane offline (writer spine failed earlier in this session)", true);

        if (!UserPromptContent.TryBuildWitnessChange(
                Encoding.UTF8.GetBytes(text), origin, out var change, out var root))
            return ("text produced no witnessable content", true);

        _turnWriter!.ApplyAsync(change).GetAwaiter().GetResult();

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["root"] = Convert.ToHexStringLower(root.ToBytes()),
                ["origin"] = origin,
                ["witnessed"] = true,
            })
        };
        return (result.ToJsonString(), false);
    }

    // Confirm/refute through the one canonical implementation (FeedbackContent —
    // the same lane as HTTP /v1/feedback and the CLI attest). Immediate fold;
    // the next walk reads the moved rating.
    private (string, bool) Feedback(JsonObject? args)
    {
        var verdict = Req(args, "verdict").Trim().ToLowerInvariant();
        if (verdict is not ("confirm" or "refute"))
            return ("verdict must be 'confirm' or 'refute'", true);
        bool confirm = verdict == "confirm";

        CodepointPerfcache.LoadDefault();
        var subject = Opt(args, "subject");
        var relation = Opt(args, "relation");
        var obj = Opt(args, "object");
        var tokensCsv = Opt(args, "tokens");

        if (subject is not null || relation is not null || obj is not null)
        {
            if (subject is null || relation is null || obj is null)
                return ("triple mode needs subject, relation and object", true);
            if (!FeedbackContent.TryResolveRelation(relation, out var rel))
                return ($"'{relation}' is not a canonical relation type", true);

            var resolved = FeedbackContent.ResolveTokensAsync(_db, [subject, obj]).GetAwaiter().GetResult();
            foreach (var t in resolved)
                if (!t.Usable)
                    return ($"'{t.Token}' has no substrate entity", true);
            var subjectId = resolved[0].Id!.Value;
            var objectId = resolved[1].Id!.Value;

            var before = FeedbackContent.ConsensusStateAsync(_db, subjectId, rel.Id, objectId).GetAwaiter().GetResult();
            var applied = FeedbackContent.ApplyAsync(
                _db, FeedbackContent.BuildTriple(subjectId, rel.Canonical, objectId, confirm)).GetAwaiter().GetResult();
            var after = FeedbackContent.ConsensusStateAsync(_db, subjectId, rel.Id, objectId).GetAwaiter().GetResult();

            static JsonObject? State(FeedbackContent.ConsensusState? s) => s is null ? null : new JsonObject
            {
                ["rating"] = s.Rating,
                ["rd"] = s.Rd,
                ["witnesses"] = s.WitnessCount,
            };

            var result = new JsonObject
            {
                ["rows"] = new JsonArray(new JsonObject
                {
                    ["mode"] = "triple",
                    ["verdict"] = verdict,
                    ["relation"] = rel.Canonical,
                    ["attestations_inserted"] = applied.AttestationsInserted,
                    ["consensus_updated"] = applied.ConsensusUpdated,
                    ["before"] = State(before),
                    ["after"] = State(after),
                })
            };
            return (result.ToJsonString(), false);
        }

        if (string.IsNullOrWhiteSpace(tokensCsv))
            return ("provide subject/relation/object, or tokens for chain mode", true);
        var tokens = tokensCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return ("chain mode needs 2+ comma-separated tokens", true);

        var chainResolved = FeedbackContent.ResolveTokensAsync(_db, tokens).GetAwaiter().GetResult();
        var ids = chainResolved.Where(t => t.Usable).Select(t => t.Id!.Value).ToList();
        if (ids.Count < 2)
            return ($"need 2+ tokens with substrate entities (got {ids.Count})", true);

        var chainApplied = FeedbackContent.ApplyAsync(
            _db, FeedbackContent.BuildPrecedesChain(ids, confirm)).GetAwaiter().GetResult();

        var chainResult = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["mode"] = "chain",
                ["verdict"] = verdict,
                ["relation"] = "PRECEDES",
                ["pairs"] = ids.Count - 1,
                ["attestations_inserted"] = chainApplied.AttestationsInserted,
                ["consensus_updated"] = chainApplied.ConsensusUpdated,
            })
        };
        return (chainResult.ToJsonString(), false);
    }

    private void EnsureWriter()
    {
        if (_turnDepositBroken || _turnWriter is not null && _turnBootstrapped) return;
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
        }
        catch (Exception ex)
        {
            _turnDepositBroken = true;
            Console.Error.WriteLine($"laplace-mcp: writer spine offline: {ex.Message}");
        }
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

    /// <summary>
    /// Text half of a text-or-entity tool: either is accepted, but not neither.
    /// Returning null when an entity was supplied keeps the SQL CASE honest — the
    /// text branch is never evaluated, so an absent term is not a silent empty
    /// resolve() that would read as "the substrate doesn't know this".
    /// </summary>
    private static string? NodeText(JsonObject? args, string name)
    {
        var text = Opt(args, name);
        if (text is not null) return text;
        if (Opt(args, "entity") is not null) return null;
        throw new ArgumentException($"missing required argument: {name} (or entity)");
    }

    private static int Int(JsonObject? args, string name, int fallback) =>
        args?[name]?.GetValue<int>() ?? fallback;
}
