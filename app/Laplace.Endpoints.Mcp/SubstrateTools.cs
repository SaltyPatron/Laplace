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
/// The MCP tool surface over the substrate's installed operations. Typed
/// tools compose laplace.* helpers so bytea ids never cross the MCP boundary
/// (resolve/word_id/relation_type_id on the way in, realize/realize_path on
/// the way out). SQL text never crosses the MCP boundary; named tools and
/// <c>op</c> are the complete client contract.
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
    /// ops.api('substring') on the SQL side, so the tool surface doesn't repeat
    /// the mistake it fixed there (a verbose catalog nobody reads because it's
    /// expensive to hold in context every turn).
    /// </summary>
    private sealed record ToolSpec(string Name, string Summary, string Description,
        Func<JsonObject> BuildSchema,
        Func<SubstrateTools, JsonObject?, (string Text, bool IsError)> Handler);

    private static readonly ToolSpec[] ToolCatalog =
    [
        new("api", "Search the installed SQL function catalog by substring.",
            "Search the substrate's installed SQL function catalog (ops.api). Returns name, args, returns for every function matching the substring. Use before assuming a helper doesn't exist.",
            () => Schema(("query", "string", "substring to match, '' lists everything", true)),
            (s, a) => s.Api(a)),
        new("recall", "Ask the substrate about a topic (default read, session-carried).",
            "Ask the substrate about a topic (converse.recall_session). A bare prompt gets the default read — gloss then the strongest chain — with session topic carry. There is NO English question routing (the regex router was removed): for a specific read shape use the `query` tool instead. Returns reply rows with eff_mu (conservative Glicko-2 estimate) and witness counts.",
            () => Schema(("prompt", "string", "the topic (a word or phrase; phrasing is not parsed)", true),
                         ("session", "string", "session key for topic carry across turns", false)),
            (s, a) => s.Recall(a)),
        new("query", "A structural read naming an explicit shape (define, is_a, walk, ...).",
            "A structural read (converse.recall_intent): the caller names the SHAPE — define, what_is, describe, synonyms, translate, languages, examples, related, related_in, is_a, reason, walk, complete, fallback (SELECT * FROM converse.query_shapes() for the live list). Language-agnostic by construction: nothing is inferred from phrasing. related/related_in need relation_type (canonical, e.g. HAS_PART); is_a/reason need topic2; translate accepts lang.",
            () => Schema(("shape", "string", "the read shape (see converse.query_shapes())", true),
                         ("topic", "string", "the subject — word, phrase, or hex entity id", true),
                         ("topic2", "string", "second topic for is_a / reason", false),
                         ("relation_type", "string", "canonical relation for related / related_in", false),
                         ("lang", "string", "target language for translate", false)),
            (s, a) => s.Query(a)),
        new("taxonomy", "The IS_A tree around a topic (up to root, or child kinds).",
            "The IS_A tree around a topic: dir='up' rows climb the parent chain to the root (via walk_strongest over the IS_A arena, from the topic's top synset — taxonomy lives on concepts, not spellings), dir='child' rows are the strongest sub-kinds. Every row carries the entity id to continue from. dir='child' is the closest thing to a \"bubble down\" the substrate has today (there is no general sense/synset -> every-surface primitive symmetric with bubble's surface -> sense -> synset climb) -- it is IS_A-specific, not a reverse of bubble. Rows use converse.label_or_hex(a cleaned display name), not render (the actual content) -- see the bubble tool's note on that distinction.",
            () => Schema(("term", "string", "the topic (omit if entity given)", false),
                         ("entity", "string", "hex entity id to root at", false)),
            (s, a) => s.Taxonomy(a)),
        new("translate", "Cross-lingual surfaces for a topic via the ILI hub.",
            "Cross-lingual surfaces for a topic (converse.translations): the ILI hub meshing languages — OMW multilingual lemmas converging on the same concept ids. Each row is a surface + its language, rated.",
            () => Schema(("term", "string", "the topic", true),
                         ("limit", "integer", "max rows, default 24", false)),
            (s, a) => s.Translate(a)),
        new("leaders", "Per-band leaderboards of the strongest consensus edges.",
            "Per-band leaderboards (consensus.band_edges): the strongest consensus edges in each salience band, fully labeled. Bands 0-12 (1 definitional, 2 taxonomic, 3 equivalence, 4 partitive, 5 causal, 6 oppositional, 7 associative, 9 lexical, 11 standards); SELECT * FROM converse.relation_bands() for live counts.",
            () => Schema(("bands", "string", "comma-separated band numbers, default '1,2,4,5'", false),
                         ("per_band", "integer", "rows per band, default 5", false)),
            (s, a) => s.Leaders(a)),
        new("chat", "One conversational turn; reply is walk-driven and self-witnessing.",
            "One conversational turn against the substrate (converse.chat): walk-driven prose composed from rated consensus. Structural steering, no phrasing tricks: shape names the read, bands lenses it (e.g. '4' parts, '2' kinds, '5' causes), elaborate advances fact layers on a carried topic. Closes the loop: prompt and reply deposit as witnessed content (UserPrompt/Response trust classes) and fold, so the turn is visible to the next walk.",
            () => Schema(("prompt", "string", "the message", true),
                         ("session", "string", "session key for continuity", false),
                         ("shape", "string", "optional read shape (see converse.query_shapes())", false),
                         ("bands", "string", "optional comma-separated salience bands to lens the reply", false),
                         ("elaborate", "boolean", "advance to the next fact layer of the carried topic", false)),
            (s, a) => s.ChatTurn(a)),
        new("witness", "Deposit a fact as witnessed content (the write lane).",
            "Deposit a fact into the substrate as witnessed content (the write lane). The text is minted as content-addressed entities through the writer spine under the UserPrompt trust class — outranked by curated sources BY DESIGN, one voice among many — and folds immediately, so the very next walk/recall can read it. Returns the minted root id. This is how an agent remembers something for every other agent.",
            () => Schema(("text", "string", "the fact/note to witness (plain prose)", true),
                         ("origin", "string", "provenance tag, default 'agent/note'", false)),
            (s, a) => s.WitnessFact(a)),
        new("feedback", "Confirm or refute a claim (Glicko win/loss on an edge).",
            "Confirm or refute a claim (the Gödel-engine feedback lane, same implementation as the CLI attest). Terms resolve at the SURFACE/word layer — use bubble first when the claim lives on a synset/hub (same text renders at three layers; feedback lands where you aim it). Triple mode: subject + relation (canonical, e.g. IS_A, RELATED_TO) + object — a confirm is a Glicko win for the edge, a refute is a loss that can drive it signed-negative until walks drop it. Chain mode: tokens (comma-separated, 2+) attest PRECEDES pairs. Folds immediately; returns consensus before/after so you can watch the rating move.",
            () => Schema(("verdict", "string", "'confirm' or 'refute'", true),
                         ("subject", "string", "triple mode: subject term", false),
                         ("relation", "string", "triple mode: canonical relation type", false),
                         ("object", "string", "triple mode: object term", false),
                         ("tokens", "string", "chain mode: comma-separated tokens (2+)", false)),
            (s, a) => s.Feedback(a)),
        new("walk", "Beam-walk the consensus graph from a prompt or entity.",
            "Beam-walk the consensus graph from a prompt (consensus.walk_branches), ranked by relation_rank x eff_mu x exp(-k*rd) x witness-saturation, gated by the highway mask when relation_type narrows it. UNFILTERED consensus.walk_branches(no relation_type) Append-scans every relation-type partition -- measured ~24s -- so pass relation_type whenever you have one; the `query` tool's `beam` shape falls back to the cheaper consensus.walk_strongest(relation_rank x eff_mu only, no highway gating) greedy chain when neither a relation type nor a band lens is given, and this tool should get the same treatment when speed matters. Pass entity (hex id from bubble) to start from a resolved node rather than re-resolving text. Paths render via realize.path(label_or_hex per step), not render -- see the bubble tool's render-vs-label note.",
            () => Schema(("prompt", "string", "starting content (omit if entity given)", false),
                         ("entity", "string", "hex entity id to start from, e.g. from bubble", false),
                         ("relation_type", "string", "canonical relation name to constrain the walk", false),
                         ("depth", "integer", "walk depth, default 4", false),
                         ("breadth", "integer", "beam breadth, default 5", false)),
            (s, a) => s.Walk(a)),
        new("infer", "One forward pass: the topic's distribution reweighted by the prompt's bias tokens.",
            "One forward pass over the substrate (converse.infer): prompt_coherence elects the topic (attention), the topic's consensus objects are read as an uncollapsed ranked distribution, EVERY sense of every non-topic token reweights it by id-space intersection (the bias heads), and realize_batch renders once at the end. Returns prediction, weight (eff_mu/1e9), bias_hits — the whole ranked frontier, never just the argmax.",
            () => Schema(("prompt", "string", "the prompt to complete", true),
                         ("limit", "integer", "max candidates, default 8", false)),
            (s, a) => s.Infer(a)),
        new("sense_audit", "Why lexical.senses() returned what it returned — type, admitting relation, language, strength.",
            "Diagnose a term's candidate sense set (lexical.sense_audit). Per candidate: the target's ENTITY TYPE (bubble_up promotes any IS_SYNONYM_OF target into the synset slot, so the value is frequently typed Word or Sentence rather than WordNet_Synset), the RELATION that admitted it (a candidate arriving via IS_SYNONYM_OF is a TRANSLATION competing as a sense), its attested HAS_LANGUAGE, and the denote_mu + witness count the election actually ranks on. Use this when a reply is on the right concept in the wrong language, or on an unrelated sense.",
            () => Schema(("term", "string", "the surface word", true),
                         ("limit", "integer", "max candidates, default 64", false)),
            (s, a) => s.SenseAudit(a)),
        new("prompt_language", "Which language a prompt is written in, as a ranked tally.",
            "The request's language (converse.prompt_language): a weighted tally of eff_mu over EVERY HAS_LANGUAGE edge carried by the prompt's entities, at every tier that has one. Deliberately not converse.word_language() per token — that is LIMIT 1 and discards the distribution, making a token shared across languages look monolingual. Returns the ranked tally rather than just the winner, because an elector should BIAS toward a language, not hard-filter to it: a cross-lingual prompt must still work.",
            () => Schema(("prompt", "string", "the prompt", true)),
            (s, a) => s.PromptLanguage(a)),
        new("bubble", "Bubble a surface term to its sense/synset frontier.",
            "Bubble a surface term up the mesh (taxonomy.bubble_up): surface -> sense -> synset. Ranking is consensus-derived base_eff_mu (from the fold's rating/rd) multiplied by a domain-log geometry boost — not a non-consensus score. Each row is one candidate sense with its synset, the relation that admitted it, and the score/witness fields the election ranked on. It does NOT climb past the synset — no hub row, no per-channel edge counts; continue upward with the taxonomy tool from the returned synset id. Returns entity ids, so the next step continues from where this one landed instead of re-entering from text. Use this before facts/walk when a term may resolve at the wrong layer — all three layers render with the SAME text, so a query aimed at the wrong one returns zero rows and looks like missing knowledge. There is no bubble_down (see the taxonomy tool for the closest, IS_A-specific, downward move). Note the render/label split: this tool's rows use render() (canonical name -> tier-0 codepoint -> resolve_name -> full recursive content rebuild -> hex fallback) because a sense/synset's actual gloss text is the point; most other tools (taxonomy, facts, walk, leaders) use converse.label_or_hex() instead (resolve_name, else render() with internal canonical-key scaffolding regex-stripped for readability, else hex) because they want a short display tag, not content. Pick the wrong one and you get either a wall of text where a tag was wanted, or a stripped tag where the actual definition was wanted.",
            () => Schema(("term", "string", "the surface word or phrase", true),
                         ("k", "integer", "sense frontier width, default 5", false)),
            (s, a) => s.Bubble(a)),
        new("facts", "Salient rated facts about a word or entity.",
            "Salient rated facts about a word (consensus.salient_facts): typed relations ranked by eff_mu with witness counts. Pass entity (hex id from bubble/walk) to read facts at a specific mesh layer instead of resolving text at the surface.",
            () => Schema(("term", "string", "the word (omit if entity given)", false),
                         ("entity", "string", "hex entity id to read from, e.g. from bubble", false),
                         ("limit", "integer", "max facts, default 24", false)),
            (s, a) => s.Facts(a)),
        new("health", "Substrate health and row-count inventory.",
            "Substrate health and inventory: laplace.substrate_health() plus ops.substrate_counts(). Metric values are NULLABLE: a metric the health pass did not measure reports null, which is a different fact from zero. identity_violations is null whenever deep_checked is false — read them together or a skipped deep pass looks like a clean one.",
            () => Schema(),
            (s, _) => s.Health()),
        new("mcp_runtime", "Identity of the deployed MCP process.",
            "Answer 'what am I talking to?' for this MCP process: binary_path, process id, and start time. Use this to prove the client launched the deployed apphost rather than a repository-local build.",
            () => Schema(),
            (_, _) => McpRuntime()),
        new("source_status", "Is a source ingested, and how do we know.",
            "Ingest state per source (ops.source_status): known, ingested, approximate evidence, whether it observed entities, and the last run's status. Call this instead of assembling an answer — every hand-rolled version of this question is wrong in a specific way. An evidence>0 test reports the DOCUMENT lane as absent, because it is content-only by design (entities and geometry, zero distributional attestations); a source name you typed returns nothing when the spelling differs from the decomposer's declared SourceName; and ingest_run_journal is ops metadata that does not survive a dump/restore, so a missing row is not absence. Asking with a name ALWAYS returns exactly one row: `ingested=false` means the source wrote nothing, and `known=false` means this substrate has no record of that source id at all — which on a mesh this dense usually means the name is wrong rather than the corpus missing. Absence is an answer here, never an empty result set.",
            () => Schema(("source", "string", "declared source name, e.g. WordNetDecomposer; omit for every source", false)),
            (s, a) => s.SourceStatus(a)),
        new("ingest", "Run a corpus ingest through the CLI's tested pipeline.",
            "Run a corpus ingest through the CLI's own tested pipeline (unpack -> records -> client-side dedup/fold -> COPY) -- the exact 'laplace ingest <source> <path>' entrypoint a terminal run uses, so results are identical either way. Substrate-wide only one ingest runs at a time (a global advisory lock); if another is active this call waits for it rather than fighting the lock, up to timeout_seconds, and is killed (not left running) on timeout. Returns the process exit code and captured output so a stalled or failed run is visible, never silently swallowed. For the live source list run `laplace ingest` with no arguments, or pass an unknown source here -- the CLI answers with its own registry rather than a copy kept in this process.",
            () => Schema(("source", "string", "registered ingest source name (code, repo, wordnet, tabular, ...)", true),
                         ("path", "string", "file or directory to ingest", true),
                         ("timeout_seconds", "integer", "max seconds to wait before killing the child process, default 600", false)),
            (_, a) => Ingest(a)),
        new("op", "Call an installed SQL operation by name, with bound arguments.",
            "Call any operation in the installed catalog BY NAME (ops.api is the allow-list; nothing outside it is callable). Arguments are bound as parameters and cast to the signature's declared types -- no SQL text crosses this boundary in either direction, which is what makes this narrower than `sql` rather than a nicer spelling of it. Overloads resolve from the argument names you supply. Enforced read-only with a 15s statement timeout, rows capped (default 200). This exists because a per-function tool is written by hand and therefore forgotten (358 installed functions, 358 chances), and because a hand-written tool is invisible until the server restarts -- which nothing owns. `op` resolves against the LIVE catalog, so an operation is callable the moment it is installed. If you are about to hand-write a SELECT, the operation you want probably exists: api('<substring>') first.",
            () => Schema(("name", "string", "installed function name, exactly as api() reports it", true),
                         ("args", "object", "argument name -> value, e.g. {\"p_source\": \"WordNetDecomposer\"}", false),
                         ("max_rows", "integer", "row cap, default 200", false)),
            (s, a) => s.Op(a)),
        new("pipeline", "Inspect Laplace pipeline build stamps and deployment status.",
            "Inspect Laplace pipeline build stamps, deployed binary directory (/opt/laplace/app), and component readiness.",
            () => Schema(),
            (s, a) => s.PipelineStatus(a)),
        new("help", "List every tool (one-line each), or full detail for one name.",
            "Catalog introspection for THIS tool surface, same idea as ops.api('substring') for the SQL catalog: with no name, lists every tool's one-line summary; with name, returns the full rationale and input schema for that one tool. Call this before guessing at a tool's arguments from its one-line summary alone.",
            () => Schema(("name", "string", "tool name for full detail; omit to list every tool", false)),
            (_, a) => Help(a)),
    ];

    public JsonArray ListTools() => new(
        ToolCatalog.Select(t => (JsonNode)Tool(t.Name, t.Summary, t.BuildSchema())).ToArray());

    // Dispatch resolves through the catalog itself: a ToolSpec cannot be declared
    // without a Handler, so every advertised tool is callable by construction.
    public (string Text, bool IsError) Call(string name, JsonObject? args)
    {
        var spec = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (spec is null)
            return ($"unknown tool: {name}", true);
        try
        {
            return spec.Handler(this, args);
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

    // One conversational turn: SQL converse.chat() composes the reply (read-side), then the
    // turn is deposited through the writer spine — full content mint + turn-level
    // evidence + inline fold under the mcp-local tenant's sources, session as
    // context on every row (spec 34). This is converse.chat()'s OODA close; the SQL function
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
            "SELECT prediction, weight, bias_hits FROM converse.infer($1, $2)");
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

    // Both parameterized end to end. A typed tool that string-builds SQL is the
    // same hole wearing a schema.
    private (string, bool) SenseAudit(JsonObject? args)
    {
        using var cmd = _dbReadOnly.CreateCommand(
            "SELECT sense, sense_type, via_relation, language, denote_mu, witnesses FROM lexical.sense_audit($1, laplace.relation_type_id('HAS_LANGUAGE'), $2)");
        cmd.Parameters.Add(new() { Value = Req(args, "term") });
        cmd.Parameters.Add(new() { Value = Int(args, "limit", 64) });
        using var rd = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (rd.Read())
            rows.Add(new JsonObject
            {
                ["sense"] = rd.IsDBNull(0) ? null : rd.GetString(0),
                ["sense_type"] = rd.IsDBNull(1) ? null : rd.GetString(1),
                ["via_relation"] = rd.IsDBNull(2) ? null : rd.GetString(2),
                ["language"] = rd.IsDBNull(3) ? null : rd.GetString(3),
                ["denote_mu"] = rd.IsDBNull(4) ? null : (double)rd.GetDecimal(4),
                ["witnesses"] = rd.IsDBNull(5) ? null : rd.GetInt64(5),
            });
        return JsonRows(rows);
    }

    private (string, bool) PromptLanguage(JsonObject? args)
    {
        using var cmd = _dbReadOnly.CreateCommand(
            "SELECT realize.realize(lang_id) AS language, mass FROM converse.prompt_language($1)");
        cmd.Parameters.Add(new() { Value = Req(args, "prompt") });
        using var rd = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (rd.Read())
            rows.Add(new JsonObject
            {
                ["language"] = rd.IsDBNull(0) ? null : rd.GetString(0),
                ["mass"] = rd.IsDBNull(1) ? null : (double)rd.GetDecimal(1),
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

    // Surface -> sense -> synset via taxonomy.bubble_up. Renders sense and synset
    // with render() — the gloss text is the point here, not a display tag (see
    // the catalog's render-vs-label note) — and returns hex ids so the next call
    // reads from the resolved layer instead of re-entering text.
    private (string, bool) Bubble(JsonObject? args)
    {
        var id = NpgsqlSubstrateReads.ResolveRefAsync(
                _dbReadOnly, ChessPositionRef.RewriteFenToHex(Req(args, "term"))!, default)
            .GetAwaiter().GetResult();
        if (id is null)
            return JsonRows(Array.Empty<JsonObject>());

        using var cmd = _dbReadOnly.CreateCommand(
            "SELECT b.sense_id, realize.render(b.sense_id), b.synset_id, realize.render(b.synset_id), " +
            "b.via_relation, b.score, b.base_eff_mu, b.domain_hits, b.witnesses " +
            "FROM taxonomy.bubble_up($1, NULL::bytea[], $2) b");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = Int(args, "k", 5) });
        using var rd = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (rd.Read())
            rows.Add(new JsonObject
            {
                ["sense"] = rd.IsDBNull(0) ? null : Convert.ToHexStringLower(rd.GetFieldValue<byte[]>(0)),
                ["sense_text"] = rd.IsDBNull(1) ? null : rd.GetString(1),
                ["synset"] = rd.IsDBNull(2) ? null : Convert.ToHexStringLower(rd.GetFieldValue<byte[]>(2)),
                ["synset_text"] = rd.IsDBNull(3) ? null : rd.GetString(3),
                ["via_relation"] = rd.IsDBNull(4) ? null : rd.GetString(4),
                ["score"] = rd.IsDBNull(5) ? null : (double)rd.GetDecimal(5),
                ["base_eff_mu"] = rd.IsDBNull(6) ? null : (double)rd.GetDecimal(6),
                ["domain_hits"] = rd.IsDBNull(7) ? null : rd.GetInt64(7),
                ["witnesses"] = rd.IsDBNull(8) ? null : rd.GetInt64(8),
            });
        return JsonRows(rows);
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

    /// <summary>
    /// Call an installed operation by name. The installed catalog (ops.api) is the
    /// allow-list, so this is strictly narrower than the sql hatch: `sql` accepts arbitrary
    /// text, `op` accepts a name that must already exist as a reviewed, installed function.
    /// Arguments bind as parameters cast to the signature's declared types — no caller text
    /// reaches the planner as SQL.
    ///
    /// WHY A GENERIC INVOKER AND NOT A TOOL PER FUNCTION. A hand-written tool fails twice:
    /// it may never be written (358 installed functions is 358 chances to forget, and
    /// forgetting is silent), and once written it is invisible until this process restarts —
    /// which nothing owns, because the server is a stdio child of whatever client launched
    /// it (GH #809). Resolving against the LIVE catalog makes an operation callable the
    /// moment it is installed in the database, with no rebuild and no restart.
    ///
    /// Read-only is enforced by the server (default_transaction_read_only), not by
    /// inspecting the name — a volatile function that writes fails at the backend rather
    /// than passing a string check here.
    /// </summary>
    private (string, bool) Op(JsonObject? args)
    {
        var name = Req(args, "name");
        var supplied = args?["args"] as JsonObject;
        var dict = supplied?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var result = InstalledOpInvoker.InvokeAsync(
                _dbReadOnly, name, dict, Int(args, "max_rows", DefaultRowCap), ct: default)
            .GetAwaiter().GetResult();
        if (result.Error is not null)
            return (result.Error, true);

        var rows = new JsonArray();
        foreach (var row in result.Rows)
        {
            var obj = new JsonObject();
            foreach (var (k, v) in row)
                obj[k] = ToJson(v ?? DBNull.Value);
            rows.Add(obj);
        }
        var payload = new JsonObject { ["rows"] = rows };
        if (result.TruncatedAt is { } t) payload["truncated_at"] = t;
        return (payload.ToJsonString(), false);
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
        // JsonValue.Create(null) emits JSON null, which is the honest rendering of a metric
        // that was not measured. Rendering it as "0" or "" would make a skipped deep check
        // read as a clean one.
        var rows = health.Select(h => new JsonObject
            {
                ["metric"] = h.Metric,
                ["value"] = h.Value is null ? null : JsonValue.Create(h.Value),
            })
            .Concat(counts.Select(c => new JsonObject { ["metric"] = c.Metric, ["value"] = c.Value.ToString() }))
            .Append(new JsonObject { ["metric"] = "binary_path", ["value"] = Environment.ProcessPath ?? AppContext.BaseDirectory });
        return JsonRows(rows);
    }

    private static (string, bool) McpRuntime()
    {
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return (new JsonObject
        {
            ["binary_path"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
            ["pid"] = Environment.ProcessId,
            ["started_utc"] = started.ToString("o"),
        }.ToJsonString(), false);
    }

    /// <summary>
    /// Ingest state per source. Typed, so nobody has to compose this question in SQL —
    /// which is how it kept getting answered wrongly, including by this assistant, three
    /// times in one session.
    /// </summary>
    private (string, bool) SourceStatus(JsonObject? args)
    {
        var rows = NpgsqlSubstrateReads
            .SourceStatusAsync(_dbReadOnly, Opt(args, "source"), default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(s => new JsonObject
        {
            ["source"] = s.Source,
            ["source_id"] = Convert.ToHexString(s.SourceId).ToLowerInvariant(),
            // known=false says "no record of this source id here", which is a different
            // answer from ingested=false ("declared, wrote nothing"). Collapsing them is
            // how a misspelled name reads as a missing corpus.
            ["known"] = s.Known,
            ["ingested"] = s.Ingested,
            ["evidence_approx"] = s.EvidenceApprox,
            ["has_entities"] = s.HasEntities,
            ["last_run_status"] = s.LastRunStatus is null ? null : JsonValue.Create(s.LastRunStatus),
            ["last_run_at"] = s.LastRunAt is null ? null : JsonValue.Create(s.LastRunAt.Value),
        }));
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
            return (new JsonObject
            {
                ["binary_path"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
                ["rows"] = listing,
            }.ToJsonString(), false);
        }

        var hit = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (hit is null)
            return ($"unknown tool '{name}'. Call help with no arguments for the full list.", true);

        var result = new JsonObject
        {
            ["binary_path"] = Environment.ProcessPath ?? AppContext.BaseDirectory,
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
    /// converse.resolve() that would read as "the substrate doesn't know this".
    /// </summary>
    private static string? NodeText(JsonObject? args, string name)
    {
        var text = Opt(args, name);
        if (text is not null) return text;
        if (Opt(args, "entity") is not null) return null;
        throw new ArgumentException($"missing required argument: {name} (or entity)");
    }

    private (string, bool) PipelineStatus(JsonObject? args)
    {
        var appDir = Environment.GetEnvironmentVariable("LAPLACE_APP_DIR") ?? "/opt/laplace/app";
        var buildDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build");
        var stampsDir = Path.Combine(buildDir, ".stamps");

        var stampsObj = new JsonObject();
        if (Directory.Exists(stampsDir))
        {
            foreach (var file in Directory.GetFiles(stampsDir, "*.stamp"))
            {
                stampsObj[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file).Trim();
            }
        }

        var result = new JsonObject
        {
            ["app_directory"] = appDir,
            ["app_directory_exists"] = Directory.Exists(appDir),
            ["mcp_binary_exists"] = File.Exists(Path.Combine(appDir, "laplace-mcp")),
            ["uci_binary_exists"] = File.Exists(Path.Combine(appDir, "laplace-uci")),
            ["stamps_directory"] = stampsDir,
            ["stamps"] = stampsObj,
        };

        return (result.ToJsonString(), false);
    }

    private static int Int(JsonObject? args, string name, int fallback) =>
        args?[name]?.GetValue<int>() ?? fallback;
}
