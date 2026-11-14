using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Agents;
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
internal sealed partial class SubstrateTools : IMcpTools
{
    private const int DefaultRowCap = 200;
    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlDataSource _dbReadOnly;
    private readonly bool _ownsDataSources;

    public SubstrateTools(string? connectionString = null)
    {
        // Request/response surface — Serving policy (bounded timeout + auto-prepare).
        _db = LaplaceDataSource.Create(SubstrateAccess.Serving, connectionString);
        _dbReadOnly = LaplaceDataSource.Create(SubstrateAccess.Serving, dsb =>
        {
            dsb.ConnectionStringBuilder.CommandTimeout = 20;
            dsb.ConnectionStringBuilder.Options =
                "-c default_transaction_read_only=on";
        }, connectionString);
        _ownsDataSources = true;
        _turns = new Lazy<ConversationTurns>(() =>
            new ConversationTurns(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest, connectionString)));
        _witnesses = new Lazy<RuntimeWitnessRecorder>(() =>
            new RuntimeWitnessRecorder(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest, connectionString)));
    }

    public SubstrateTools(NpgsqlDataSource db, NpgsqlDataSource dbReadOnly)
    {
        _db = db;
        _dbReadOnly = dbReadOnly;
        _ownsDataSources = false;
        _turns = new Lazy<ConversationTurns>(ConversationTurns.Installed);
        _witnesses = new Lazy<RuntimeWitnessRecorder>(RuntimeWitnessRecorder.Installed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSources)
        {
            await _dbReadOnly.DisposeAsync();
            await _db.DisposeAsync();
        }
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
            "Search the substrate's installed SQL function catalog (ops.api). Returns name, args, returns, kind for every callable function or procedure matching the substring. Use before assuming a helper doesn't exist.",
            () => Schema(("query", "string", "substring to match, '' lists everything", true)),
            (s, a) => s.Api(a)),
        new("recall", "Ask the substrate about a topic (default read, session-carried).",
            "Ask the substrate about a topic (converse.recall_session). A bare prompt gets the default read — gloss then the strongest chain — with session topic carry. There is NO English question routing (the regex router was removed): for a specific read shape use the `query` tool instead. Returns reply rows with consensus.eff_mu(conservative Glicko-2 estimate) and witness counts.",
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
            "The IS_A tree around a topic: dir='up' rows climb the parent chain to the root (via public.laplace_pull's cheapest deepest chain over hypernym claims, from the topic's top synset — taxonomy lives on concepts, not spellings), dir='child' rows are the strongest sub-kinds. Every row carries the entity id to continue from. For concept -> surface realizations use taxonomy.bubble_down via `op`; dir='child' here is IS_A sub-kinds. Rows are labeled by public.laplace_labels, the Native substrate's labels.",
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
        new("chat", "One turn of a session, answered by the forward program and witnessed.",
            "One turn of a session (Engine laplace turn): the forward program RESOLVE..WITNESS answers under its firmware, the session's earlier turns its discourse. The Engine witnesses the turn itself: this agent's [session, turn] at its ordinal under UserPromptContent and Laplace's [response, prompt] under ResponseContent, so the next turn reads it. Returns the reply and the receipt (turn id, ordinal, disposition).",
            () => Schema(("prompt", "string", "the message", true),
                         ("session", "string", "session key for continuity ([A-Za-z0-9._@-]{1,128}); default, this server process's session", false)),
            (s, a) => s.ChatTurn(a)),
        new("witness", "Record a note as content this agent gives (the write lane).",
            "Record a note as content: the text is a file the Engine records as what the runtime witness mcp-local gives at UserPromptContent (laplace witness), an observation outranked by every curated source, the same content wherever its text stands. Returns the witness trunk and the file trunk. The same note given again adds nothing.",
            () => Schema(("text", "string", "the note, as it is", true)),
            (s, a) => s.WitnessFact(a)),
        new("feedback", "Confirm or refute one claim (a statement this agent witnesses).",
            "Confirm or refute one claim, [subject, relation, object] or [subject, relation]: each part is the text it is, the relation this agent's own word for it. The verdict is a statement of the runtime witness mcp-local at UserPromptContent (laplace witness), confirm 1 and refute 0, folded into the claim's standing in the database; the same verdict stated again moves nothing. Returns the claim, the witness and the claim's Native standing before and after.",
            () => Schema(("verdict", "string", "'confirm' or 'refute'", true),
                         ("subject", "string", "the claim's subject, as text", true),
                         ("relation", "string", "the relation, the word as you would write it", true),
                         ("object", "string", "the claim's object, as text; omit for a claim of two parts", false)),
            (s, a) => s.Feedback(a)),
        new("walk", "Beam-walk the consensus graph from a prompt or entity.",
            "Best-first walk over rated claims from a prompt (public.laplace_pull), each chain costed by its claims' consensus confidence. Pass relation_type to constrain the walk to one relation: a Native relation word, or IS_A (WordNet's hypernym); other legacy relation names walk unconstrained. Pass entity (hex id from bubble) to start from a resolved node rather than re-resolving text. Paths render via realize.path(label_or_hex per step), not render -- see the bubble tool's render-vs-label note.",
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
            "Bubble a surface term up the mesh (taxonomy.bubble_up): surface -> sense -> synset. Ranking is consensus-derived base_eff_mu (from the fold's rating/rd) multiplied by a domain-log geometry boost — not a non-consensus score. Each row is one candidate sense with its synset, the relation that admitted it, and the score/witness fields the election ranked on. It does NOT climb past the synset — no hub row, no per-channel edge counts; continue upward with the taxonomy tool from the returned synset id, or downward to surface realizations via `op` (`taxonomy.bubble_down`). Returns entity ids, so the next step continues from where this one landed instead of re-entering from text. Use this before facts/walk when a term may resolve at the wrong layer — all three layers render with the SAME text, so a query aimed at the wrong one returns zero rows and looks like missing content. Rows are labeled by public.laplace_labels, the Native substrate's labels.",
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
            "Ingest state per source (ops.source_status): known, ingested, approximate evidence, the last run, and the substrate-owned throughput verdict/rate/baseline/slowdown. Call this instead of assembling an answer — every hand-rolled version of this question is wrong in a specific way. An evidence>0 test reports the DOCUMENT lane as absent, because it is content-only by design (entities and geometry, zero distributional attestations); a source name you typed returns nothing when the spelling differs from the decomposer's declared SourceName; and ingest_run_journal is ops metadata that does not survive a dump/restore, so a missing row is not absence. Asking with a name ALWAYS returns exactly one row: `ingested=false` means the source wrote nothing, and `known=false` means this substrate has no record of that source id at all — which on a mesh this dense usually means the name is wrong rather than the corpus missing. `throughput_compared=false` is not green. Absence is an answer here, never an empty result set.",
            () => Schema(("source", "string", "declared source name, e.g. WordNet or Unicode; omit for every source", false)),
            (s, a) => s.SourceStatus(a)),
        new("ingest", "Run a corpus ingest through the CLI's tested pipeline.",
            "Run a corpus ingest through the CLI's own tested pipeline (unpack -> records -> client-side dedup/fold -> COPY) -- the exact 'laplace ingest <source> <path>' entrypoint a terminal run uses, so results are identical either way. Substrate-wide only one ingest runs at a time (a global advisory lock); if another is active this call waits for it rather than fighting the lock, up to timeout_seconds, and is killed (not left running) on timeout. Returns the process exit code and captured output so a stalled or failed run is visible, never silently swallowed. For the live source list run `laplace ingest` with no arguments, or pass an unknown source here -- the CLI answers with its own registry rather than a copy kept in this process.",
            () => Schema(("source", "string", "registered ingest source name (code, repo, wordnet, tabular, ...)", true),
                         ("path", "string", "file or directory to ingest", true),
                         ("timeout_seconds", "integer", "max seconds to wait before killing the child process, default 600", false)),
            (_, a) => Ingest(a)),
        new("op", "Call an installed SQL operation by name, with bound arguments.",
            "Call any operation in the installed catalog BY NAME (ops.api is the allow-list; nothing outside it is callable). Arguments are bound as parameters and cast to the signature's declared types -- no SQL text crosses this boundary in either direction, which is what makes this narrower than `sql` rather than a nicer spelling of it. Overloads resolve from the argument names you supply. Enforced read-only with a 15s statement timeout, rows capped (default 200). This exists because a per-function tool is written by hand and therefore forgotten, and because a hand-written tool is invisible until the server restarts -- which nothing owns. `op` resolves against the LIVE catalog, so an operation is callable the moment it is installed. If you are about to hand-write a SELECT, the operation you want probably exists: ops.api('<substring>') first.",
            () => Schema(("name", "string", "installed function name, exactly as ops.api() reports it", true),
                         ("args", "object", "argument name -> value, e.g. {\"p_source\": \"WordNet\"}", false),
                         ("max_rows", "integer", "row cap, default 200", false),
                         ("timeout_seconds", "integer", "exact command timeout; defaults to 15 for reads and 0 (unbounded, cancellable) for allow-listed writes", false)),
            (s, a) => s.Op(a)),
        new("pipeline", "Inspect Laplace pipeline build stamps and deployment status.",
            "Inspect Laplace pipeline build stamps, deployed binary directory (/opt/laplace/app), and component readiness.",
            () => Schema(),
            (s, a) => s.PipelineStatus(a)),
        new("ask", "Put one turn to an external model (OpenAI, Anthropic, xAI, Google, local).",
            "Put one turn to a model OUTSIDE this substrate and return its answer. `model` selects the route three ways, checked in that order: an alias from agents.json ('house'), an explicit 'provider/model' pair ('xai/grok-4', 'openrouter/anthropic/claude-opus-5' -- split once, so a nested vendor id survives), or a bare vendor-branded name whose prefix identifies its provider ('claude-*', 'gpt-*', 'grok-*', 'gemini-*'). A bare name with no vendor prefix (llama, qwen) is REFUSED rather than guessed: a dozen hosts serve those, and inferring one bills the wrong vendor silently. Omit `model` to use LAPLACE_AGENT_DEFAULT, else agents.json's \"default\". `provider` FORCES the route and makes `model` a literal vendor model id -- the alias table is skipped, so 'which provider ran this' stays answerable from the arguments alone. Credentials resolve from the process environment first, then secrets/agents.env through the same reader the API service uses for Stripe and Lichess, because an agent client launches this server over stdio with an empty environment and no way to inject one. The config is re-read PER CALL: nothing owns a restart of a stdio child, so an edited agents.json must take effect without one. Per-wire correctness is not the caller's problem -- Anthropic gets a required max_tokens with headroom for thinking and NO sampling parameters unless you name one (Claude 4.7+ returns 400 for temperature); OpenAI gets max_completion_tokens while its clones get max_tokens; Google gets contents/generationConfig. A refusal or an upstream block is returned as an EMPTY reply with a finish_reason and a note, not as an error: the reason is what decides the next move. 429 and the transient 5xx family retry up to 3 attempts honouring Retry-After; 401/403/404 do not, and say which variable or model id to fix.",
            () => Schema(("prompt", "string", "the message to send", true),
                         ("model", "string", "alias, 'provider/model', or a vendor-branded model id; omit for the default", false),
                         ("provider", "string", "force a provider (openai, anthropic, xai, google, vertex, openrouter, groq, deepseek, mistral, ollama, openai-compatible, laplace)", false),
                         ("system", "string", "system prompt for this turn; overrides the alias's own", false),
                         ("max_tokens", "integer", "output cap; Anthropic defaults to 16000 because thinking counts against it", false),
                         ("temperature", "number", "sampling temperature; omitted entirely when unset (current Anthropic models reject it)", false),
                         ("timeout_seconds", "integer", "wall-clock budget for the whole call including retries, default 180", false)),
            (s, a) => s.Ask(a)),
        new("agents", "Which external models are routable from here, and which have credentials.",
            "The external-agent routing table: every alias from agents.json plus every installed provider, each with the model it would call, the base URL it would call, the environment variable its key is read from, and whether that key is actually resolvable right now. Call this BEFORE concluding an external model is unavailable -- the two failures look identical from a failed `ask` and have different fixes: `credentialed=false` means the named variable is unset in both the process environment and secrets/agents.env, while a null `model` on a provider row means nothing has said which model to call (only anthropic ships a default, because it is the only vendor whose current model id this repository states from a checked-in reference rather than from memory). KEY VALUES ARE NEVER RETURNED, only the variable name that would supply one, so this is safe to read into a model's context. `config` reports which agents.json was loaded, or null when none was found -- a null there with missing aliases means the file is not where the search looked, not that its contents are wrong.",
            () => Schema(),
            (s, _) => s.AgentRoutes()),
        new("embeddings", "Deterministic 4-D S^3 coordinate and Merkle content id for text or code.",
            "Decompose text or code in-process via TextDecomposer and HashComposer over the T0 perfcache to compute its deterministic 128-bit content id, 4-D S^3 centroid coordinate [x, y, z, m, radius], and trajectory vertex count. When include_meaning is true, also queries the substrate for rated consensus neighbours.",
            () => Schema(("input", "string", "text or code snippet to embed (or pass inputs as a JSON array of strings)", false),
                         ("inputs", "array", "array of text or code strings to embed in batch", false),
                         ("include_meaning", "boolean", "include rated consensus neighbours from the substrate, default false", false),
                         ("meaning_limit", "integer", "max consensus neighbours per input when include_meaning is true, default 10", false)),
            (s, a) => s.Embeddings(a)),
        new("rerank", "Deterministic 4-D Fréchet trajectory and Merkle constituent overlap reranker.",
            "Rerank candidate documents or code snippets against a query in-process using exact 4-D trajectory Fréchet distance (math4d_frechet), 4-D centroid distance, and word-tier Merkle content-id overlap. Requires zero neural inference and zero database round-trips.",
            () => Schema(("query", "string", "the search query or code snippet", true),
                         ("documents", "array", "array of candidate strings or {\"text\": \"...\"} objects to rerank", true),
                         ("top_n", "integer", "maximum number of top-ranked results to return, default all", false)),
            (s, a) => s.Rerank(a)),
        new("code_search", "Search workspace source code and named substrate entities with 4-D Fréchet ranking.",
            "Decompose a code or structural query into its root 128-bit content id and word-tier constituent ids, search the in-memory workspace declaration chunk index (with IDF constituent postings and exact 4-D Fréchet trajectory distance) and structural.explore_browse_named_entities in the substrate, and return ranked code snippets with file paths, line ranges, entity ids, and Fréchet distances.",
            () => Schema(("query", "string", "code symbol, identifier, or natural-language query", true),
                         ("path", "string", "optional repository, directory, or file path substring filter", false),
                         ("limit", "integer", "maximum results to return, default 20", false),
                         ("offset", "integer", "pagination offset, default 0", false),
                         ("reindex", "boolean", "force a fresh scan of workspace source files, default false", false)),
            (s, a) => s.CodeSearch(a)),
        new("reindex", "Refresh the workspace declaration chunk index for code_search.",
            "Ensure or force-rebuild the in-memory workspace declaration chunk index (4-D S^3 trajectories and word-tier constituent postings) used by code_search.",
            () => Schema(("force", "boolean", "force re-indexing all workspace source files regardless of mtime/size cache, default false", false)),
            (_, a) => Reindex(a)),
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
        var clock = Stopwatch.StartNew();
        try
        {
            var result = spec.Handler(this, args);
            clock.Stop();
            return AttachPerformance(result, clock.Elapsed.TotalMilliseconds);
        }
        catch (PostgresException ex)
        {
            Serilog.Log.ForContext<SubstrateTools>().Error(ex, "MCP tool database query failed.");
            return ($"substrate error [{ex.SqlState}]: {ex.MessageText}", true);
        }
        catch (NpgsqlException ex)
        {
            Serilog.Log.ForContext<SubstrateTools>().Error(ex, "MCP tool database connection failed.");
            return ($"substrate unavailable: {ex.Message}", true);
        }
        catch (AgentException ex)
        {
            Serilog.Log.ForContext<SubstrateTools>().Warning(ex, "MCP agent operation failed.");
            return (ex.Message, true);
        }
        catch (ArgumentException ex)
        {
            Serilog.Log.ForContext<SubstrateTools>().Warning(ex, "MCP tool rejected its arguments.");
            return (ex.Message, true);
        }
    }

    private static (string Text, bool IsError) AttachPerformance(
        (string Text, bool IsError) result, double handlerMs)
    {
        if (result.IsError) return result;
        JsonObject? payload;
        try { payload = JsonNode.Parse(result.Text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return result; }
        if (payload is null) return result;

        // Measure the canonicalized inner tool result. Measuring the returned
        // envelope would be self-referential because the measurements themselves
        // change its serialized byte/code-point counts.
        var canonicalInner = payload.ToJsonString();
        int? rows = (payload["rows"] as JsonArray)?.Count;
        payload["performance"] = new JsonObject
        {
            // This clock intentionally covers only the catalog handler. JSON parsing,
            // receipt construction, and response serialization are wrapper overhead,
            // so naming it elapsed_ms incorrectly implied client-visible latency.
            ["handler_ms"] = handlerMs,
            ["row_count"] = rows,
            ["rows_per_second"] = rows is { } count && handlerMs > 0
                ? count * 1000.0 / handlerMs
                : null,
            ["inner_result_utf8_bytes"] = Encoding.UTF8.GetByteCount(canonicalInner),
            ["inner_result_codepoints"] = canonicalInner.EnumerateRunes().Count(),
        };
        return (payload.ToJsonString(), false);
    }

    // The MCP participant: the agent principal this server answers, a witness of its turns. A chat turn
    // is the Engine's (ConversationTurns): [mcp-local, session] is the session, the turn witnessed there.
    private const string McpTenant = "mcp-local";
    private readonly string _processSessionKey = $"s-{Guid.NewGuid():N}";
    private readonly Lazy<ConversationTurns> _turns;
    // What this agent states (notes and feedback) is the runtime witness mcp-local's, recorded by the Engine.
    private readonly Lazy<RuntimeWitnessRecorder> _witnesses;

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
        using var cmd = NpgsqlCatalog.Command(_dbReadOnly, "converse.infer",
            Req(args, "prompt"), Int(args, "limit", 8));
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
        using var cmd = NpgsqlCatalog.Command(_dbReadOnly, "lexical.sense_audit",
            Req(args, "term"), Int(args, "limit", 64));
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
        using var cmd = NpgsqlCatalog.Command(_dbReadOnly, "converse.prompt_language", Req(args, "prompt"));
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

        var rows = NpgsqlSubstrateReads.TaxonomyTreeAsync(
                _dbReadOnly, id,
                Math.Max(0, Int(args, "depth", 10)),
                Math.Max(0, Int(args, "children", 24)),
                default)
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
        var rows = NpgsqlSubstrateReads.ClaimLeadersNamedAsync(_dbReadOnly, bands, per, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Where(r => r.ClaimId is not null).Select(r => new JsonObject
        {
            ["band"] = r.Band,
            ["name"] = r.BandName,
            ["claim_id"] = r.ClaimId,
            ["parts"] = JsonNode.Parse(r.Parts!),
            ["confidence"] = r.Confidence,
            ["witnesses"] = r.Witnesses,
            ["trajectory"] = r.Trajectory,
        }));
    }

    private (string, bool) Facts(JsonObject? args)
    {
        var entity = Opt(args, "entity");
        byte[]? id;
        string via = "entity";
        if (entity is not null)
            id = Convert.FromHexString(entity);
        else
        {
            id = NpgsqlSubstrateReads.ResolveRefAsync(
                    _dbReadOnly, ChessPositionRef.RewriteFenToHex(NodeText(args, "term"))!, default)
                .GetAwaiter().GetResult();

            // LAYER, REPORTED, NEVER GUESSED. consensus.salient_facts is
            // converse.facts(p_syn bytea, ...) -- it wants a SYNSET. A surface term resolves
            // to the WORD, and salient_facts on a word returns its neighbourhood rather than
            // its facts: reading `cat` that way returns "transcribes as /bɜːd/", "[ɡaɪ]" and
            // "form of: dragon" at 8-17 witnesses, another word's facts under this one's
            // name, with no error to signal it.
            //
            // Auto-climbing to bubble_up's top synset is NOT the fix and was tried: the
            // top-ranked synset by eff_mu is wrong for most terms (measured 2026-08-16 over
            // ten common nouns -- dog -> "pole", fire -> "Lacking that which burdens or
            // makes heavy", book -> "absent"), so climbing silently substitutes a different
            // wrong answer. Ranking is unreliable because one gloss is deposited at several
            // granularities and consensus splits across them; that is an ingest question,
            // not something a read tool may paper over.
            //
            // So the layer is REPORTED and the caller decides. `bubble` returns the frontier
            // with scores and witnesses; pass the id you chose back as `entity`.
            via = "surface (NOT a synset — salient_facts expects one; "
                + "use the bubble tool and pass the chosen id as `entity`)";
        }
        if (id is null)
            return JsonRows(Array.Empty<JsonObject>());

        var limit = Int(args, "limit", 24);
        var rows = NpgsqlSubstrateReads.SalientFactsAsync(_dbReadOnly, id, limit, default)
            .GetAwaiter().GetResult();
        var entityHex = Convert.ToHexStringLower(id);
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["entity"] = entityHex,
            ["resolved_via"] = via,
            ["type"] = r.Type,
            ["fact"] = r.Fact,
            ["eff_mu"] = Math.Round(r.EffMu, 1),
            ["witnesses"] = r.Witnesses,
        }));
    }
}
