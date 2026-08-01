using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
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
        // Request/response surface — Serving policy (bounded timeout + auto-prepare).
        _db = LaplaceDataSource.Create(SubstrateAccess.Serving);
        _dbReadOnly = LaplaceDataSource.Create(SubstrateAccess.Serving, dsb =>
        {
            dsb.ConnectionStringBuilder.CommandTimeout = 20;
            dsb.ConnectionStringBuilder.Options =
                "-c default_transaction_read_only=on -c statement_timeout=15000";
        });
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

    /// <summary>
    /// The raw-SQL escape hatch is OPERATOR-LANE ONLY. It is server-enforced
    /// read-only (default_transaction_read_only) so it cannot write, but it can
    /// read every table — including witnessed session prompts — and no product
    /// client should hold that. Closed by default; scripts/laplace-mcp opts the
    /// local operator in. A client deployment that wants it must say so out loud.
    /// </summary>
    private static readonly bool OperatorLane =
        Environment.GetEnvironmentVariable("LAPLACE_MCP_OPERATOR") == "1";

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
        new("infer", "One forward pass: the topic's distribution reweighted by the prompt's bias tokens.",
            "One forward pass over the substrate (laplace.infer): prompt_coherence elects the topic (attention), the topic's consensus objects are read as an uncollapsed ranked distribution, EVERY sense of every non-topic token reweights it by id-space intersection (the bias heads), and realize_batch renders once at the end. Returns prediction, weight (eff_mu/1e9), bias_hits — the whole ranked frontier, never just the argmax.",
            () => Schema(("prompt", "string", "the prompt to complete", true),
                         ("limit", "integer", "max candidates, default 8", false))),
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
            "Run a corpus ingest through the CLI's own tested pipeline (unpack -> records -> client-side dedup/fold -> COPY) -- the exact 'laplace ingest <source> <path>' entrypoint a terminal run uses, so results are identical either way. Substrate-wide only one ingest runs at a time (a global advisory lock); if another is active this call waits for it rather than fighting the lock, up to timeout_seconds, and is killed (not left running) on timeout. Returns the process exit code and captured output so a stalled or failed run is visible, never silently swallowed. For the live source list run `laplace ingest` with no arguments, or pass an unknown source here -- the CLI answers with its own registry rather than a copy kept in this process.",
            () => Schema(("source", "string", "registered ingest source name (code, repo, wordnet, tabular, ...)", true),
                         ("path", "string", "file or directory to ingest", true),
                         ("timeout_seconds", "integer", "max seconds to wait before killing the child process, default 600", false))),
        new("help", "List every tool (one-line each), or full detail for one name.",
            "Catalog introspection for THIS tool surface, same idea as laplace.api('substring') for the SQL catalog: with no name, lists every tool's one-line summary; with name, returns the full rationale and input schema for that one tool. Call this before guessing at a tool's arguments from its one-line summary alone.",
            () => Schema(("name", "string", "tool name for full detail; omit to list every tool", false))),
    ];

    public JsonArray ListTools() => new(
        ToolCatalog.Where(t => OperatorLane || t.Name != "sql")
            .Select(t => (JsonNode)Tool(t.Name, t.Summary, t.BuildSchema())).ToArray());

    public (string Text, bool IsError) Call(string name, JsonObject? args)
    {
        try
        {
            return name switch
            {
                "api" => Api(args),
                "sql" => OperatorLane
                    ? Rows(_dbReadOnly,
                        Req(args, "query"),
                        Int(args, "max_rows", DefaultRowCap))
                    : ("sql is operator-lane only (launch with LAPLACE_MCP_OPERATOR=1); product reads go through the typed tools", true),
                "infer" => Infer(args),
                "recall" => Recall(args),
                "chat" => ChatTurn(args),
                "witness" => WitnessFact(args),
                "feedback" => Feedback(args),
                "query" => Query(args),
                "taxonomy" => Taxonomy(args),
                "translate" => Translate(args),
                "leaders" => Leaders(args),
                "walk" => Walk(args),
                "facts" => Facts(args),
                "health" => Health(),
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

    // TWO DISTINCT LANES, no longer sharing a writer or a broken-latch.
    //   _turnCloser  — the spec-34 conversational close: tenant scope, session as
    //                  context, attribution. Owned entirely by TurnCloser.
    //   _plainWriter — the untenanted agent-note lane (WitnessFact), which rides the
    //                  shared UserPrompt/Response sources because a standalone note
    //                  has no session or tenant to scope.
    // These were one writer and one `_turnDepositBroken` flag, so a failure in the
    // note lane silently disabled conversational deposits and vice versa.
    private TurnCloser? _turnCloser;
    private ISubstrateWriter? _plainWriter;
    private bool _plainBootstrapped;
    private bool _plainWriterBroken;

    /// <summary>
    /// A tool session key resolves through the canonical mint — the same id law the
    /// API surface uses, so an MCP session and an endpoint session with the same
    /// tenant+key are the SAME context entity. Null stays null (recall.c's own
    /// per-backend fallback applies).
    /// </summary>
    private static byte[]? SessionBytes(string? sessionKey) =>
        sessionKey is null
            ? null
            : ConversationContent.SessionId(McpTenant, sessionKey).ToBytes();

    private (string, bool) Recall(JsonObject? args)
    {
        // GH #575: FEN topics rewrite to composed position hex before lexical resolve.
        var prompt = ChessPositionRef.RewriteFenToHex(Req(args, "prompt"))!;
        var rows = NpgsqlSubstrateReads.RecallSessionAsync(
            _db, prompt, SessionBytes(Opt(args, "session")), default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["reply"] = r.Reply,
            ["eff_mu"] = r.EffMu is null ? null : Math.Round(r.EffMu.Value, 1),
            ["witnesses"] = r.Witnesses,
        }));
    }

    // The forward pass as a typed surface: parameterized end to end — this tool
    // exists so no client composes SQL strings, so it does not compose one itself.
    private (string, bool) Infer(JsonObject? args)
    {
        using var cmd = _dbReadOnly.CreateCommand(
            "SELECT prediction, weight, bias_hits FROM laplace.infer($1, $2)");
        cmd.Parameters.Add(new() { Value = Req(args, "prompt") });
        cmd.Parameters.Add(new() { Value = Int(args, "limit", 8) });
        using var rd = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (rd.Read())
            rows.Add(new JsonObject
            {
                ["prediction"] = rd.IsDBNull(0) ? null : rd.GetString(0),
                ["weight"] = rd.IsDBNull(1) ? null : Math.Round(rd.GetDouble(1), 1),
                ["bias_hits"] = rd.IsDBNull(2) ? null : rd.GetInt64(2),
            });
        return JsonRows(rows);
    }

    private (string, bool) Query(JsonObject? args)
    {
        var topicRef = ChessPositionRef.RewriteFenToHex(Req(args, "topic"))!;
        var topic = NpgsqlSubstrateReads.ResolveRefAsync(_db, topicRef, default)
            .GetAwaiter().GetResult();
        if (topic is null)
            return JsonRows(Array.Empty<JsonObject>());

        byte[]? topic2 = null;
        if (Opt(args, "topic2") is { } t2)
        {
            var t2Ref = ChessPositionRef.RewriteFenToHex(t2)!;
            topic2 = NpgsqlSubstrateReads.ResolveRefAsync(_db, t2Ref, default).GetAwaiter().GetResult();
        }

        var rows = NpgsqlSubstrateReads.RecallIntentAsync(
            _db, Req(args, "shape"), topic, topic2,
            Opt(args, "relation_type"), Opt(args, "lang"), contextIds: null, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["reply"] = r.Reply,
            ["eff_mu"] = r.EffMu is null ? null : Math.Round(r.EffMu.Value, 1),
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Taxonomy(JsonObject? args)
    {
        var entity = Opt(args, "entity");
        byte[]? id;
        if (entity is not null)
            id = Convert.FromHexString(entity);
        else
            id = NpgsqlSubstrateReads.ResolveRefAsync(
                    _dbReadOnly, ChessPositionRef.RewriteFenToHex(NodeText(args, "term"))!, default)
                .GetAwaiter().GetResult();
        if (id is null)
            return JsonRows(Array.Empty<JsonObject>());

        var rows = NpgsqlSubstrateReads.TaxonomyTreeAsync(_dbReadOnly, id, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["dir"] = r.Dir,
            ["ord"] = r.Ord,
            ["entity"] = r.IdHex,
            ["label"] = r.Label,
            ["eff_mu"] = r.EffMu is null ? null : Math.Round(r.EffMu.Value, 1),
        }));
    }

    private (string, bool) Leaders(JsonObject? args)
    {
        var bandsCsv = Opt(args, "bands") ?? "1,2,4,5";
        var bands = bandsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        var per = Int(args, "per_band", 5);
        var rows = NpgsqlSubstrateReads.BandLeadersAsync(_dbReadOnly, bands, per, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["band"] = r.Band,
            ["subject"] = r.Subject,
            ["relation"] = r.Relation,
            ["object"] = r.Object,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Facts(JsonObject? args)
    {
        var entity = Opt(args, "entity");
        byte[]? id;
        if (entity is not null)
            id = Convert.FromHexString(entity);
        else
            id = NpgsqlSubstrateReads.ResolveRefAsync(
                    _dbReadOnly, ChessPositionRef.RewriteFenToHex(NodeText(args, "term"))!, default)
                .GetAwaiter().GetResult();
        if (id is null)
            return JsonRows(Array.Empty<JsonObject>());

        var limit = Int(args, "limit", 24);
        var rows = NpgsqlSubstrateReads.SalientFactsAsync(_dbReadOnly, id, limit, default)
            .GetAwaiter().GetResult();
        var entityHex = Convert.ToHexStringLower(id);
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["entity"] = entityHex,
            ["type"] = r.Type,
            ["fact"] = r.Fact,
            ["eff_mu"] = Math.Round(r.EffMu, 1),
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Api(JsonObject? args)
    {
        var rows = NpgsqlSubstrateReads.ApiCatalogAsync(_dbReadOnly, Req(args, "query"), default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["name"] = r.Name,
            ["args"] = r.Args,
            ["returns"] = r.Returns,
        }));
    }

    private (string, bool) Translate(JsonObject? args)
    {
        var term = ChessPositionRef.RewriteFenToHex(Req(args, "term"))!;
        var rows = NpgsqlSubstrateReads.TranslationsAsync(
            _db, term, Int(args, "limit", 24), default).GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["translation"] = r.Translation,
            ["language"] = r.Language,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Walk(JsonObject? args)
    {
        var prompt = ChessPositionRef.RewriteFenToHex(NodeText(args, "prompt"));
        var rows = NpgsqlSubstrateReads.WalkBranchesAsync(
            _dbReadOnly, prompt, Opt(args, "entity"), Opt(args, "relation_type"),
            Int(args, "depth", 4), Int(args, "breadth", 5), default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["depth"] = r.Depth,
            ["path"] = r.Path,
            ["eff_mu"] = Math.Round(r.EffMu, 1),
            ["path_mu"] = Math.Round(r.PathMu, 1),
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Health()
    {
        var health = NpgsqlSubstrateReads.SubstrateHealthAsync(_dbReadOnly, default)
            .GetAwaiter().GetResult();
        var counts = NpgsqlSubstrateReads.SubstrateCountsAsync(_dbReadOnly, default)
            .GetAwaiter().GetResult();
        var rows = health.Select(h => new JsonObject { ["metric"] = h.Metric, ["value"] = h.Value })
            .Concat(counts.Select(c => new JsonObject { ["metric"] = c.Metric, ["value"] = c.Value.ToString() }));
        return JsonRows(rows);
    }

    private static (string, bool) JsonRows(IEnumerable<JsonObject> rows)
    {
        var arr = new JsonArray();
        var truncated = false;
        foreach (var row in rows)
        {
            if (arr.Count >= DefaultRowCap) { truncated = true; break; }
            arr.Add(row);
        }
        var result = new JsonObject { ["rows"] = arr };
        if (truncated) result["truncated_at"] = DefaultRowCap;
        return (result.ToJsonString(), false);
    }

    private (string, bool) ChatTurn(JsonObject? args)
    {
        var prompt = ChessPositionRef.RewriteFenToHex(Req(args, "prompt"))!;
        var sessionKey = Opt(args, "session") ?? _processSessionKey;
        var sessionId = ConversationContent.SessionId(McpTenant, sessionKey);
        var shape = Opt(args, "shape");
        var bandsCsv = Opt(args, "bands");
        int[]? bands = bandsCsv is null
            ? null
            : bandsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToArray();
        var elaborate = args?["elaborate"]?.GetValue<bool>() ?? false;

        var reply = NpgsqlSubstrateReads.ChatAsync(
            _db, prompt, sessionId.ToBytes(), default,
            shape: shape, bands: bands, elaborate: elaborate).GetAwaiter().GetResult();

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
        if (_plainWriterBroken)
            return ("witness lane offline (writer spine failed earlier in this session)", true);

        if (!UserPromptContent.TryBuildWitnessChange(
                Encoding.UTF8.GetBytes(text), origin, out var change, out var root))
            return ("text produced no witnessable content", true);

        _plainWriter!.ApplyAsync(change).GetAwaiter().GetResult();

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

    private void EnsurePlainWriter()
    {
        if (_plainWriterBroken || (_plainWriter is not null && _plainBootstrapped)) return;
        try
        {
            if (_plainWriter is null)
            {
                CodepointPerfcache.LoadDefault();
                _plainWriter = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(_db), _db);
            }
            if (!_plainBootstrapped)
            {
                _plainWriter.ApplyAsync(UserPromptContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _plainWriter.ApplyAsync(ResponseContent.BuildBootstrapChange()).GetAwaiter().GetResult();
                _plainBootstrapped = true;
            }
        }
        catch (Exception ex)
        {
            _plainWriterBroken = true;
            Console.Error.WriteLine($"laplace-mcp: writer spine offline: {ex.Message}");
        }
    }

    // The OODA close runs through the shared TurnCloser (Laplace.Ingestion), which
    // owns the whole sequence — floor gate, writer, tenant scope cache, bootstrap,
    // attribution, build, apply. This lane used to re-derive it and was missing the
    // floor gate, so an MCP turn against a floorless database deposited testimony
    // with no tier-0 constituents to anchor it.
    private TurnCloser TurnCloser => _turnCloser ??= new TurnCloser(
        _db, warn => Console.Error.WriteLine($"laplace-mcp: {warn}"));

    private void DepositTurn(string prompt, string? reply, Hash128 sessionId)
        => TurnCloser.CloseAsync(McpTenant, sessionId, prompt, reply).GetAwaiter().GetResult();

    private static (string, bool) Ingest(JsonObject? args)
    {
        var source = Req(args, "source").Trim();
        var path = Req(args, "path").Trim();
        var timeoutSeconds = Int(args, "timeout_seconds", 600);

        // NO LOCAL SOURCE GATE. This used to check `source` against a hand-copied
        // array of CLI keys, which had already drifted: it lacked chess-trajectory,
        // so this tool REJECTED a source the CLI routes fine. A valet over another
        // process must not keep its own copy of that process's menu -- the CLI owns
        // IngestDispatchTable, validates against it, and its error already names
        // every supported source. Forwarding an unknown name costs one process
        // start and returns the authoritative answer instead of a stale one.
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
