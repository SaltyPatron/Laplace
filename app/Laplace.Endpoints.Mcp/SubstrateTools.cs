using System.Diagnostics;
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

    /// <summary>
    /// One catalog, two views: <see cref="ListTools"/> sends every agent a name +
    /// one-line summary (cheap, always in context); <c>help</c> looks up the full
    /// rationale + schema for one name on demand — the same shape as
    /// laplace.api('substring') on the SQL side, so the tool surface doesn't repeat
    /// the mistake it fixed there (a verbose catalog nobody reads because it's
    /// expensive to hold in context every turn).
    /// </summary>
    private sealed record ToolSpec(string Name, string Summary, string Description, Func<JsonObject> BuildSchema);

    // Registered CLI ingest source names (app/Laplace.Cli/IngestDispatchTable.cs).
    // Kept as a plain list rather than a live lookup: the dispatch table is a
    // compile-time registry in a sibling project this one doesn't reference, and
    // this tool is a valet over the CLI process, not a reimplementation of it.
    // Declared BEFORE ToolCatalog: its ingest entry's Description references this
    // array, and static field initializers run in declaration order.
    private static readonly string[] KnownIngestSources =
    [
        "atomic2020", "chess", "chess-analyze", "chess-books", "chess-eval", "cili", "code",
        "conceptnet", "document", "framenet", "iso639", "mapnet", "omw", "omw-probe", "openings",
        "opensubtitles", "parquet", "propbank", "recipe", "repo", "semlink", "stack", "tabular",
        "tatoeba", "tiny-codes", "ud", "unicode", "verbnet", "wiktionary", "wordframenet", "wordnet",
    ];

    private static readonly ToolSpec[] ToolCatalog =
    [
        new("api", "Search the installed SQL function catalog by substring.",
            "Search the substrate's installed SQL function catalog (laplace.api). Returns name, args, returns for every function matching the substring. Use before assuming a helper doesn't exist.",
            () => Schema(("query", "string", "substring to match, '' lists everything", true))),
        new("sql", "Run a read-only SQL query against the substrate.",
            "Run a read-only SQL query against the substrate (schema laplace on the search_path). The whole api() catalog is callable. Enforced read-only with a 15s statement timeout; rows capped (default 200).",
            () => Schema(("query", "string", "SQL SELECT/WITH to execute", true),
                         ("max_rows", "integer", "row cap, default 200", false))),
        new("recall", "Ask the substrate about a topic (default read, session-carried).",
            "Ask the substrate about a topic (laplace.recall_session). A bare prompt gets the default read — gloss then the strongest chain — with session topic carry. There is NO English question routing (the regex router was removed): for a specific read shape use the `query` tool instead. Returns reply rows with eff_mu (conservative Glicko-2 estimate) and witness counts.",
            () => Schema(("prompt", "string", "the topic (a word or phrase; phrasing is not parsed)", true),
                         ("session", "string", "session key for topic carry across turns", false))),
        new("query", "A structural read naming an explicit shape (define, is_a, walk, ...).",
            "A structural read (laplace.recall_intent): the caller names the SHAPE — define, what_is, describe, synonyms, translate, languages, examples, related, related_in, is_a, reason, walk, complete, fallback (SELECT * FROM laplace.query_shapes() for the live list). Language-agnostic by construction: nothing is inferred from phrasing. related/related_in need relation_type (canonical, e.g. HAS_PART); is_a/reason need topic2; translate accepts lang.",
            () => Schema(("shape", "string", "the read shape (see query_shapes())", true),
                         ("topic", "string", "the subject — word, phrase, or hex entity id", true),
                         ("topic2", "string", "second topic for is_a / reason", false),
                         ("relation_type", "string", "canonical relation for related / related_in", false),
                         ("lang", "string", "target language for translate", false))),
        new("taxonomy", "The IS_A tree around a topic (up to root, or child kinds).",
            "The IS_A tree around a topic: dir='up' rows climb the parent chain to the root (via walk_strongest over the IS_A arena, from the topic's top synset — taxonomy lives on concepts, not spellings), dir='child' rows are the strongest sub-kinds. Every row carries the entity id to continue from. dir='child' is the closest thing to a \"bubble down\" the substrate has today (there is no general sense/synset -> every-surface primitive symmetric with bubble's surface -> sense -> synset climb) -- it is IS_A-specific, not a reverse of bubble. Rows use label_or_hex (a cleaned display name), not render (the actual content) -- see the bubble tool's note on that distinction.",
            () => Schema(("term", "string", "the topic (omit if entity given)", false),
                         ("entity", "string", "hex entity id to root at", false))),
        new("translate", "Cross-lingual surfaces for a topic via the ILI hub.",
            "Cross-lingual surfaces for a topic (laplace.translations): the ILI hub meshing languages — OMW multilingual lemmas converging on the same concept ids. Each row is a surface + its language, rated.",
            () => Schema(("term", "string", "the topic", true),
                         ("limit", "integer", "max rows, default 24", false))),
        new("leaders", "Per-band leaderboards of the strongest consensus edges.",
            "Per-band leaderboards (laplace.consensus_band_edges): the strongest consensus edges in each salience band, fully labeled. Bands 0-12 (1 definitional, 2 taxonomic, 3 equivalence, 4 partitive, 5 causal, 6 oppositional, 7 associative, 9 lexical, 11 standards); SELECT * FROM laplace.relation_bands() for live counts.",
            () => Schema(("bands", "string", "comma-separated band numbers, default '1,2,4,5'", false),
                         ("per_band", "integer", "rows per band, default 5", false))),
        new("chat", "One conversational turn; reply is walk-driven and self-witnessing.",
            "One conversational turn against the substrate (laplace.chat): walk-driven prose composed from rated consensus. Structural steering, no phrasing tricks: shape names the read, bands lenses it (e.g. '4' parts, '2' kinds, '5' causes), elaborate advances fact layers on a carried topic. Closes the loop: prompt and reply deposit as witnessed content (UserPrompt/Response trust classes) and fold, so the turn is visible to the next walk.",
            () => Schema(("prompt", "string", "the message", true),
                         ("session", "string", "session key for continuity", false),
                         ("shape", "string", "optional read shape (see query_shapes())", false),
                         ("bands", "string", "optional comma-separated salience bands to lens the reply", false),
                         ("elaborate", "boolean", "advance to the next fact layer of the carried topic", false))),
        new("witness", "Deposit a fact as witnessed content (the write lane).",
            "Deposit a fact into the substrate as witnessed content (the write lane). The text is minted as content-addressed entities through the writer spine under the UserPrompt trust class — outranked by curated sources BY DESIGN, one voice among many — and folds immediately, so the very next walk/recall can read it. Returns the minted root id. This is how an agent remembers something for every other agent.",
            () => Schema(("text", "string", "the fact/note to witness (plain prose)", true),
                         ("origin", "string", "provenance tag, default 'agent/note'", false))),
        new("feedback", "Confirm or refute a claim (Glicko win/loss on an edge).",
            "Confirm or refute a claim (the Gödel-engine feedback lane, same implementation as the CLI attest). Terms resolve at the SURFACE/word layer — use bubble first when the claim lives on a synset/hub (same text renders at three layers; feedback lands where you aim it). Triple mode: subject + relation (canonical, e.g. IS_A, RELATED_TO) + object — a confirm is a Glicko win for the edge, a refute is a loss that can drive it signed-negative until walks drop it. Chain mode: tokens (comma-separated, 2+) attest PRECEDES pairs. Folds immediately; returns consensus before/after so you can watch the rating move.",
            () => Schema(("verdict", "string", "'confirm' or 'refute'", true),
                         ("subject", "string", "triple mode: subject term", false),
                         ("relation", "string", "triple mode: canonical relation type", false),
                         ("object", "string", "triple mode: object term", false),
                         ("tokens", "string", "chain mode: comma-separated tokens (2+)", false))),
        new("walk", "Beam-walk the consensus graph from a prompt or entity.",
            "Beam-walk the consensus graph from a prompt (laplace.walk_branches), ranked by relation_rank x eff_mu x exp(-k*rd) x witness-saturation, gated by the highway mask when relation_type narrows it. UNFILTERED walk_branches (no relation_type) Append-scans every relation-type partition -- measured ~24s -- so pass relation_type whenever you have one; the `query` tool's `beam` shape falls back to the cheaper walk_strongest (relation_rank x eff_mu only, no highway gating) greedy chain when neither a relation type nor a band lens is given, and this tool should get the same treatment when speed matters. Pass entity (hex id from bubble) to start from a resolved node rather than re-resolving text. Paths render via realize_path (label_or_hex per step), not render -- see the bubble tool's render-vs-label note.",
            () => Schema(("prompt", "string", "starting content (omit if entity given)", false),
                         ("entity", "string", "hex entity id to start from, e.g. from bubble", false),
                         ("relation_type", "string", "canonical relation name to constrain the walk", false),
                         ("depth", "integer", "walk depth, default 4", false),
                         ("breadth", "integer", "beam breadth, default 5", false))),
        new("bubble", "Bubble a surface term up the mesh to its concept hub.",
            "Bubble a surface term up the mesh to the highway (laplace.bubble_up): surface -> sense -> synset (ranked by base_eff_mu x domain-log-boost from geometry adjacency, not consensus rows), then the hub above it (IS_INSTANCE_OF/IS_A) and every relation channel available there with edge counts. Returns entity ids, so the next step continues from where this one landed instead of re-entering from text. Use this before facts/walk when a term may resolve at the wrong layer — all three layers render with the SAME text, so a query aimed at the wrong one returns zero rows and looks like missing knowledge. There is no bubble_down (see the taxonomy tool for the closest, IS_A-specific, downward move). Note the render/label split: this tool's rows use render() (canonical name -> tier-0 codepoint -> resolve_name -> full recursive content rebuild -> hex fallback) because a sense/synset's actual gloss text is the point; most other tools (taxonomy, facts, walk, leaders) use label_or_hex() instead (resolve_name, else render() with internal canonical-key scaffolding regex-stripped for readability, else hex) because they want a short display tag, not content. Pick the wrong one and you get either a wall of text where a tag was wanted, or a stripped tag where the actual definition was wanted.",
            () => Schema(("term", "string", "the surface word or phrase", true),
                         ("k", "integer", "sense frontier width, default 5", false))),
        new("facts", "Salient rated facts about a word or entity.",
            "Salient rated facts about a word (laplace.salient_facts): typed relations ranked by eff_mu with witness counts. Pass entity (hex id from bubble/walk) to read facts at a specific mesh layer instead of resolving text at the surface.",
            () => Schema(("term", "string", "the word (omit if entity given)", false),
                         ("entity", "string", "hex entity id to read from, e.g. from bubble", false),
                         ("limit", "integer", "max facts, default 24", false))),
        new("health", "Substrate health and row-count inventory.",
            "Substrate health and inventory: laplace.substrate_health() plus laplace.substrate_counts().",
            () => Schema()),
        new("ingest", "Run a corpus ingest through the CLI's tested pipeline.",
            "Run a corpus ingest through the CLI's own tested pipeline (unpack -> records -> client-side dedup/fold -> COPY) -- the exact 'laplace ingest <source> <path>' entrypoint a terminal run uses, so results are identical either way. Substrate-wide only one ingest runs at a time (a global advisory lock); if another is active this call waits for it rather than fighting the lock, up to timeout_seconds, and is killed (not left running) on timeout. Returns the process exit code and captured output so a stalled or failed run is visible, never silently swallowed. Known sources: "
            + string.Join(", ", KnownIngestSources) + ".",
            () => Schema(("source", "string", "registered ingest source name (code, repo, wordnet, tabular, ...)", true),
                         ("path", "string", "file or directory to ingest", true),
                         ("timeout_seconds", "integer", "max seconds to wait before killing the child process, default 600", false))),
        new("help", "List every tool (one-line each), or full detail for one name.",
            "Catalog introspection for THIS tool surface, same idea as laplace.api('substring') for the SQL catalog: with no name, lists every tool's one-line summary; with name, returns the full rationale and input schema for that one tool. Call this before guessing at a tool's arguments from its one-line summary alone.",
            () => Schema(("name", "string", "tool name for full detail; omit to list every tool", false))),
    ];

    public JsonArray ListTools() => new(
        ToolCatalog.Select(t => (JsonNode)Tool(t.Name, t.Summary, t.BuildSchema())).ToArray());

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
                    FROM laplace.recall_session(@p, @s)
                    """,
                    DefaultRowCap, ("p", Req(args, "prompt")), ("s", SessionParam(Opt(args, "session")))),
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
                    WITH root AS MATERIALIZED (
                        SELECT COALESCE(laplace.top_synset(r.id), r.id) AS id
                        FROM (SELECT CASE WHEN @e IS NULL THEN laplace.resolve_ref(@term)
                                          ELSE decode(@e, 'hex') END AS id) r)
                    SELECT 'up' AS dir, w.step AS ord,
                           encode(w.entity_id, 'hex') AS entity,
                           laplace.label_or_hex(w.entity_id) AS label,
                           round(w.eff_mu, 1) AS eff_mu
                    FROM root, laplace.walk_strongest(root.id, laplace.relation_type_id('IS_A'), 10) w
                    UNION ALL
                    SELECT 'child',
                           row_number() OVER (ORDER BY laplace.eff_mu_display(c.rating, c.rd) DESC)::int,
                           encode(c.subject_id, 'hex'),
                           laplace.label_or_hex(c.subject_id),
                           round(laplace.eff_mu_display(c.rating, c.rd), 1)
                    FROM root JOIN laplace.consensus c
                      ON c.object_id = root.id AND c.type_id = laplace.relation_type_id('IS_A')
                    ORDER BY dir DESC, ord
                    LIMIT 40
                    """,
                    DefaultRowCap, ("term", NodeText(args, "term")), ("e", Opt(args, "entity"))),
                "translate" => Rows(_db,
                    """
                    -- translations() emits eff_mu in RAW FIXED-POINT (an extension
                    -- inconsistency; the display conversion lives in eff_mu_display and
                    -- is not re-derived here). Column named honestly until the
                    -- extension-side fix lands.
                    SELECT t.translation, t.language, t.eff_mu AS eff_mu_fp, t.witnesses
                    FROM laplace.translations(laplace.resolve_ref(@term), @limit) t
                    """,
                    DefaultRowCap, ("term", Req(args, "term")), ("limit", Int(args, "limit", 24))),
                "leaders" => Rows(_dbReadOnly,
                    """
                    SELECT b.band,
                           laplace.label_or_hex(e.subject_id) AS subject,
                           laplace.relation_canonical(e.type_id) AS relation,
                           laplace.label_or_hex(e.object_id) AS object,
                           round(laplace.eff_mu_display(e.rating, e.rd), 1) AS eff_mu,
                           e.witness_count
                    FROM unnest(string_to_array(@bands, ',')::int[]) AS b(band)
                    CROSS JOIN LATERAL laplace.consensus_band_edges(b.band, NULL, @per) e
                    """,
                    DefaultRowCap,
                    ("bands", Opt(args, "bands") ?? "1,2,4,5"), ("per", Int(args, "per_band", 5))),
                "bubble" => Rows(_dbReadOnly,
                    """
                    WITH b AS MATERIALIZED (
                        SELECT * FROM laplace.bubble_up(laplace.resolve(@term), NULL, @k)
                    ),
                    top AS MATERIALIZED (SELECT synset_id AS id FROM b LIMIT 1)
                    SELECT 'sense' AS kind, b.via_relation AS via,
                           encode(b.sense_id, 'hex') AS entity,
                           laplace.render(b.sense_id) AS label,
                           b.witnesses
                    FROM b
                    UNION ALL
                    SELECT 'synset', b.via_relation, encode(b.synset_id, 'hex'),
                           laplace.render(b.synset_id), b.witnesses
                    FROM b
                    UNION ALL
                    SELECT 'hub', laplace.relation_canonical(c.type_id),
                           encode(c.object_id, 'hex'), laplace.render(c.object_id),
                           c.witness_count
                    FROM top JOIN laplace.consensus c ON c.subject_id = top.id
                    WHERE c.type_id IN (laplace.relation_type_id('IS_INSTANCE_OF'),
                                        laplace.relation_type_id('IS_A'))
                    UNION ALL
                    SELECT 'channel', laplace.relation_canonical(c.type_id),
                           encode(top.id, 'hex'), NULL, count(*)
                    FROM top JOIN laplace.consensus c ON c.subject_id = top.id
                    GROUP BY laplace.relation_canonical(c.type_id), top.id
                    """,
                    DefaultRowCap, ("term", Req(args, "term")), ("k", Int(args, "k", 5))),
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
                "ingest" => Ingest(args),
                "help" => Help(args),
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
    // turn is deposited through the writer spine — full content mint + turn-level
    // evidence + inline fold under the mcp-local tenant's sources, session as
    // context on every row (spec 34). This is chat()'s OODA close; the SQL function
    // itself stays read-only (session state aside).
    private const string McpTenant = "mcp-local";
    private readonly string _processSessionKey = $"s-{Guid.NewGuid():N}";
    private ISubstrateWriter? _turnWriter;
    private ConversationContent.TenantScope _turnScope;
    private bool _turnBootstrapped;
    private bool _turnDepositBroken;

    /// <summary>
    /// A tool session key resolves through the canonical mint — the same id law the
    /// API surface uses, so an MCP session and an endpoint session with the same
    /// tenant+key are the SAME context entity. Null stays null (recall.c's own
    /// per-backend fallback applies).
    /// </summary>
    private static NpgsqlParameter SessionParam(string? sessionKey) =>
        new("s", NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = sessionKey is null
                ? DBNull.Value
                : ConversationContent.SessionId(McpTenant, sessionKey).ToBytes()
        };

    private (string, bool) ChatTurn(JsonObject? args)
    {
        var prompt = Req(args, "prompt");
        var sessionKey = Opt(args, "session") ?? _processSessionKey;
        var sessionId = ConversationContent.SessionId(McpTenant, sessionKey);
        var shape = Opt(args, "shape");
        var bands = Opt(args, "bands");
        var elaborate = args?["elaborate"]?.GetValue<bool>() ?? false;

        string? reply;
        using (var cmd = _db.CreateCommand(
            """
            SELECT laplace.chat(@p, @s, NULL, @shape,
                CASE WHEN @bands IS NULL THEN NULL ELSE string_to_array(@bands, ',')::int[] END,
                NULL, NULL, NULL, @elab)
            """))
        {
            cmd.Parameters.AddWithValue("p", prompt);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Bytea)
                { Value = sessionId.ToBytes() });
            cmd.Parameters.Add(new NpgsqlParameter("shape", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)shape ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("bands", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)bands ?? DBNull.Value });
            cmd.Parameters.AddWithValue("elab", elaborate);
            reply = cmd.ExecuteScalar() as string;
        }

        DepositTurn(prompt, reply, sessionId);

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject { ["reply"] = reply }),
            ["session"] = sessionKey
        };
        return (result.ToJsonString(), false);
    }

    // The agent write lane: mint a note as witnessed content and fold it, so the
    // substrate is the shared memory between every agent on this repo. Same spine
    // and trust class as a chat turn; the note is one outrankable voice, not truth.
    // Plain (untenanted) UserPrompt/Response sources — the same path the CLI and
    // OpenAICompat/TurnWitness.cs use — deliberately distinct from the tenant-scoped
    // ConversationContent path a chat turn deposits through (spec 34): a standalone
    // note has no session/tenant to scope, so it rides the shared base sources.
    private (string, bool) WitnessFact(JsonObject? args)
    {
        var text = Req(args, "text");
        var origin = Opt(args, "origin") ?? "agent/note";

        EnsurePlainWriter();
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

    // Bootstraps the plain (untenanted) UserPrompt/Response sources for the witness
    // lane — a separate flag from the tenant-scoped turn bootstrap below, since the
    // two register different sources and must not short-circuit each other.
    private bool _plainBootstrapped;

    private void EnsurePlainWriter()
    {
        if (_turnDepositBroken || _turnWriter is not null && _plainBootstrapped) return;
        try
        {
            if (_turnWriter is null)
            {
                CodepointPerfcache.LoadDefault();
                _turnWriter = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }
            if (!_plainBootstrapped)
            {
                _turnWriter.ApplyAsync(UserPromptContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _turnWriter.ApplyAsync(ResponseContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _plainBootstrapped = true;
            }
        }
        catch (Exception ex)
        {
            _turnDepositBroken = true;
            Console.Error.WriteLine($"laplace-mcp: writer spine offline: {ex.Message}");
        }
    }

    private void DepositTurn(string prompt, string? reply, Hash128 sessionId)
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
                _turnScope = ConversationContent.Resolve(McpTenant);
                foreach (var change in ConversationContent.BuildTenantBootstrapChanges(_turnScope))
                    _turnWriter.ApplyAsync(change).GetAwaiter().GetResult();
                _turnBootstrapped = true;
            }

            if (ConversationContent.TryBuildTurnChange(
                    _turnScope, sessionId,
                    Encoding.UTF8.GetBytes(prompt),
                    string.IsNullOrWhiteSpace(reply) ? null : Encoding.UTF8.GetBytes(reply),
                    userKey: null,
                    out var turnChange, out _, out _))
            {
                _turnWriter.ApplyAsync(turnChange).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            // The reply still flows; the missing deposit is reported, not hidden.
            _turnDepositBroken = true;
            Console.Error.WriteLine($"laplace-mcp: turn deposit disabled: {ex.Message}");
        }
    }

    private static (string, bool) Ingest(JsonObject? args)
    {
        var source = Req(args, "source").Trim();
        var path = Req(args, "path").Trim();
        var timeoutSeconds = Int(args, "timeout_seconds", 600);

        if (!KnownIngestSources.Contains(source, StringComparer.Ordinal))
            return ($"unknown ingest source '{source}'. Known: {string.Join(", ", KnownIngestSources)}", true);
        if (!File.Exists(path) && !Directory.Exists(path))
            return ($"path not found: {path}", true);

        var cliPath = ResolveCliBinary();
        if (cliPath is null)
            return ("Laplace.Cli binary not found (expected app/Laplace.Cli/bin/{Release,Debug}/net10.0/Laplace.Cli next to the repo root; override with LAPLACE_CLI_BIN)", true);

        var psi = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ingest");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exited = proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return ($"ingest '{source}' timed out after {timeoutSeconds}s and was killed (another ingest may " +
                     "be holding the substrate-wide lock — retry with a longer timeout_seconds once it clears). " +
                     $"Partial output:\n{Tail(stdout.ToString(), 4000)}", true);
        }

        var combined = stdout.ToString();
        if (stderr.Length > 0) combined += "\n--- stderr ---\n" + stderr;
        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["source"] = source,
                ["path"] = path,
                ["exit_code"] = proc.ExitCode,
                ["output"] = Tail(combined, 4000),
            })
        };
        return (result.ToJsonString(), proc.ExitCode != 0);
    }

    private static string Tail(string s, int maxChars) =>
        s.Length <= maxChars ? s : "...[truncated]...\n" + s[^maxChars..];

    private static string? ResolveCliBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_CLI_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        if (!LaplaceInstall.TryRepoRoot(out var root)) return null;
        var exeName = OperatingSystem.IsWindows() ? "Laplace.Cli.exe" : "Laplace.Cli";
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(root, "app", "Laplace.Cli", "bin", config, "net10.0", exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (string, bool) Help(JsonObject? args)
    {
        var name = Opt(args, "name");
        if (name is null)
        {
            var listing = new JsonArray(
                ToolCatalog.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["summary"] = t.Summary }).ToArray());
            return (new JsonObject { ["rows"] = listing }.ToJsonString(), false);
        }

        var hit = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (hit is null)
            return ($"unknown tool '{name}'. Call help with no arguments for the full list.", true);

        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["name"] = hit.Name,
                ["description"] = hit.Description,
                ["input_schema"] = hit.BuildSchema(),
            })
        };
        return (result.ToJsonString(), false);
    }

    private (string, bool) Rows(NpgsqlDataSource source, string sql, int rowCap,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = source.CreateCommand(sql);
        foreach (var (pName, value) in parameters)
        {
            // A null optional is always a text arg here; DBNull without a
            // declared type leaves the parameter untyped at the server (42P08).
            // A pre-typed NpgsqlParameter (bytea session ids) passes through.
            if (value is NpgsqlParameter typed)
                cmd.Parameters.Add(typed);
            else if (value is null)
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
