using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.Data;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Mcp;

public sealed partial class SubstrateTools : IMcpTools
{
    private const int DefaultRowCap = 128;
    private const string McpTenant = "mcp-local";
    private const string FeedbackTrustClass = "UserPromptContent";

    private readonly NpgsqlDataSource _db;
    private readonly NpgsqlDataSource _dbReadOnly;
    private readonly bool _ownsDataSources;
    private readonly string? _explicitConnectionString;
    private readonly Lazy<LaplaceEngineRuntime> _engine;
    private readonly string _processSessionKey = $"s-{Guid.NewGuid():N}";
    private ExternalAgentClient? _agents;

    public SubstrateTools(string? connectionString = null)
    {
        var resolved = ManagedServiceDatabase.Resolve(connectionString);
        _explicitConnectionString = resolved;
        _db = LaplaceDataSource.Create(SubstrateAccess.Serving, resolved);
        _dbReadOnly = LaplaceDataSource.Create(SubstrateAccess.Serving, resolved);
        _ownsDataSources = true;
        _engine = new Lazy<LaplaceEngineRuntime>(() => new LaplaceEngineRuntime(
            string.IsNullOrWhiteSpace(connectionString) ? null : LaplaceEngineRuntime.ResolveLibpqConnInfo()),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public SubstrateTools(NpgsqlDataSource db, NpgsqlDataSource dbReadOnly)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dbReadOnly);
        _db = db;
        _dbReadOnly = dbReadOnly;
        _ownsDataSources = false;
        _engine = new Lazy<LaplaceEngineRuntime>(() => new LaplaceEngineRuntime(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask DisposeAsync()
    {
        _agents?.Dispose();
        if (_engine.IsValueCreated)
            _engine.Value.Dispose();
        if (_ownsDataSources)
        {
            await _db.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_dbReadOnly, _db))
                await _dbReadOnly.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record ToolSpec(
        string Name,
        string Summary,
        string Description,
        Func<JsonObject> BuildSchema,
        Func<SubstrateTools, JsonObject?, (string Text, bool IsError)> Invoke);

    private static readonly ToolSpec[] ToolCatalog =
    [
        new("api", "Search the installed SQL function catalog by substring.",
            "Search public.laplace_* / blake3* and ops.* routines by substring.",
            () => Schema(("query", "string", "substring to match, '' lists everything", true)),
            (s, a) => s.Api(a)),
        new("recall", "Ask the substrate about a topic (default read, session-carried).",
            "Couples the topic across attested claims inside the canonical database.",
            () => Schema(
                ("prompt", "string", "the topic (a word or phrase)", true),
                ("session", "string", "optional session key", false)),
            (s, a) => s.Recall(a)),
        new("query", "A structural read naming an explicit shape (define, is_a, walk, ...).",
            "Executes a structural pull over rated claims matching a named query shape.",
            () => Schema(
                ("shape", "string", "the read shape (define, what_is, related, is_a, reason, walk, complete)", true),
                ("topic", "string", "the primary topic", true),
                ("topic2", "string", "optional second topic", false),
                ("relation_type", "string", "optional relation constraint", false),
                ("lang", "string", "optional language code", false)),
            (s, a) => s.Query(a)),
        new("taxonomy", "The IS_A tree around a topic (up to root, or child kinds).",
            "Climbs hypernym chains from a resolved synset/entity up to root via public.laplace_pull.",
            () => Schema(
                ("term", "string", "the topic (omit if entity given)", false),
                ("entity", "string", "hex entity id", false),
                ("dir", "string", "up or child (default up)", false),
                ("depth", "integer", "max hops (default 4)", false),
                ("limit", "integer", "max child rows (default 24)", false)),
            (s, a) => s.Taxonomy(a)),
        new("translate", "Cross-lingual surfaces for a topic via the ILI hub.",
            "Pulls translation_of / has_translation surfaces for a term.",
            () => Schema(
                ("term", "string", "the topic", true),
                ("limit", "integer", "max rows (default 24)", false)),
            (s, a) => s.Translate(a)),
        new("leaders", "Per-band leaderboards of the strongest consensus edges.",
            "Returns highest-confidence consensus claims per relation band.",
            () => Schema(
                ("bands", "string", "comma-separated band numbers, default '1,2,4,5'", false),
                ("limit", "integer", "rows per band (default 10)", false)),
            (s, a) => s.Leaders(a)),
        new("chat", "One turn of a session, answered by the forward program and witnessed.",
            "Runs in-process Engine lp_engine_turn across consensus and witnesses the turn.",
            () => Schema(
                ("prompt", "string", "the message", true),
                ("session", "string", "optional session key", false),
                ("user", "string", "optional participant identifier", false),
                ("read_only", "boolean", "do not witness turn when true", false)),
            (s, a) => s.ChatTurn(a)),
        new("witness", "Record a note as content this agent gives (the write lane).",
            "Writes content through native `laplace witness` as mcp-local.",
            () => Schema(("text", "string", "the note, as it is", true)),
            (s, a) => s.WitnessFact(a)),
        new("feedback", "Confirm or refute one claim (a statement this agent witnesses).",
            "Records a confirm/refute statement via native `laplace witness` and returns before/after standing.",
            () => Schema(
                ("verdict", "string", "'confirm' or 'refute'", true),
                ("subject", "string", "claim subject", true),
                ("relation", "string", "claim relation", true),
                ("object", "string", "optional claim object", false)),
            (s, a) => s.Feedback(a)),
        new("walk", "Beam-walk the consensus graph from a prompt or entity.",
            "Best-first walk over rated claims via public.laplace_pull.",
            () => Schema(
                ("prompt", "string", "starting content (omit if entity given)", false),
                ("entity", "string", "hex entity id to start from", false),
                ("relation_type", "string", "optional relation constraint", false),
                ("depth", "integer", "max depth (default 3)", false),
                ("breadth", "integer", "beam breadth (default 8)", false)),
            (s, a) => s.Walk(a)),
        new("infer", "One forward pass: the topic's distribution reweighted by the prompt's bias tokens.",
            "Computes forward continuation candidates via public.laplace_forward.",
            () => Schema(
                ("prompt", "string", "the prompt to complete", true),
                ("limit", "integer", "max completions (default 12)", false)),
            (s, a) => s.Infer(a)),
        new("sense_audit", "Why lexical senses returned what they returned — type, relation, strength.",
            "Inspects lexical sense candidates and consensus standings for a term.",
            () => Schema(("term", "string", "the surface word", true)),
            (s, a) => s.SenseAudit(a)),
        new("prompt_language", "Which language a prompt is written in, as a ranked tally.",
            "Estimates prompt language from attested word senses and script codepoints.",
            () => Schema(("prompt", "string", "the prompt", true)),
            (s, a) => s.PromptLanguage(a)),
        new("bubble", "Bubble a surface term to its sense/synset frontier.",
            "Resolves sense and neighbor links for a term so downstream queries can target exact IDs.",
            () => Schema(
                ("term", "string", "the surface word or phrase", true),
                ("k", "integer", "max rows (default 12)", false)),
            (s, a) => s.Bubble(a)),
        new("facts", "Salient rated facts about a word or entity.",
            "Reads salient consensus facts via public.laplace_claims_each.",
            () => Schema(
                ("term", "string", "the word (omit if entity given)", false),
                ("entity", "string", "hex entity id", false),
                ("limit", "integer", "max facts (default 24)", false)),
            (s, a) => s.Facts(a)),
        new("health", "Substrate health and row-count inventory.",
            "Reports fingerprint, entity presence, and estimated substrate counts.",
            () => Schema(),
            (s, _) => s.Health()),
        new("mcp_runtime", "Identity of the deployed MCP process.",
            "Returns PID, binary path, and .NET runtime identity.",
            () => Schema(),
            (_, _) => McpRuntime()),
        new("source_status", "Is a source ingested, and how do we know.",
            "Checks witness reading/part inventory from public.laplace_witnesses_named().",
            () => Schema(("source", "string", "declared source name; omit for all sources", false)),
            (s, a) => s.SourceStatus(a)),
        new("ingest", "Run a corpus ingest through the CLI's tested pipeline.",
            "Launches `Laplace.Cli ingest` under bounded supervision.",
            () => Schema(
                ("source", "string", "registered ingest source name", true),
                ("path", "string", "optional file or directory path", false),
                ("timeout_seconds", "integer", "timeout in seconds (default 600)", false)),
            (_, a) => Ingest(a)),
        new("op", "Call an installed SQL operation by name, with bound arguments.",
            "Invokes whitelisted read-only SQL operations in public.* / ops.*.",
            () => Schema(
                ("name", "string", "installed function name", true),
                ("args", "object", "named argument object", false),
                ("max_rows", "integer", "max rows (default 200)", false),
                ("timeout_seconds", "integer", "timeout in seconds (default 15)", false)),
            (s, a) => s.Op(a)),
        new("pipeline", "Inspect Laplace pipeline build stamps and deployment status.",
            "Reports binary stamps, schema readiness, and substrate counts.",
            () => Schema(),
            (s, a) => s.PipelineStatus(a)),
        new("ask", "Put one turn to an external model (OpenAI, Anthropic, xAI, Google, local).",
            "Routes one prompt to a configured external agent in agents.json.",
            () => Schema(
                ("prompt", "string", "the message to send", true),
                ("agent", "string", "optional agent alias or provider/model", false),
                ("system", "string", "optional system prompt", false),
                ("max_tokens", "integer", "optional token cap", false),
                ("temperature", "number", "optional sampling temperature", false)),
            (s, a) => s.Ask(a)),
        new("agents", "Which external models are routable from here, and which have credentials.",
            "Lists configured routes from AgentCatalog.",
            () => Schema(),
            (s, _) => s.AgentRoutes()),
        new("embeddings", "Deterministic 4-D S^3 coordinate and Merkle content id for text or code.",
            "Decomposes text via TextDecomposer over tier0.bin to return canonical ID and 4D coords.",
            () => Schema(
                ("input", "string", "text or code snippet", false),
                ("inputs", "array", "array of strings", false),
                ("include_meaning", "boolean", "include substrate neighbors when true", false),
                ("meaning_limit", "integer", "max neighbors per item (default 8)", false)),
            (s, a) => s.Embeddings(a)),
        new("rerank", "Deterministic 4-D Fréchet trajectory and Merkle constituent overlap reranker.",
            "Scores candidate documents using Math4d.SubTrajectoryFrechet and constituent overlap.",
            () => Schema(
                ("query", "string", "the search query or code snippet", true),
                ("documents", "array", "array of candidate strings", true),
                ("top_n", "integer", "max results", false)),
            (s, a) => s.Rerank(a)),
        new("code_search", "Search workspace source code and named substrate entities with 4-D Fréchet ranking.",
            "Searches LAPLACE_SRC workspace files and substrate entities.",
            () => Schema(
                ("query", "string", "code symbol, identifier, or query", true),
                ("limit", "integer", "max hits (default 16)", false),
                ("include_peers", "boolean", "include semantic peers from DB", false)),
            (s, a) => s.CodeSearch(a)),
        new("reindex", "Refresh the workspace declaration index for code_search.",
            "Rebuilds the in-memory workspace source file index.",
            () => Schema(("force", "boolean", "force full reindex regardless of cache", false)),
            (_, a) => Reindex(a)),
        new("help", "List every tool (one-line each), or full detail for one name.",
            "Prints catalog summaries or schema details for one tool.",
            () => Schema(("name", "string", "tool name for full detail", false)),
            (_, a) => Help(a)),
    ];

    public JsonArray ListTools() => new(
        ToolCatalog.Select(t => (JsonNode)Tool(t.Name, t.Summary, t.BuildSchema())).ToArray());

    public (string Text, bool IsError) Call(string name, JsonObject? args)
    {
        var spec = ToolCatalog.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (spec is null)
            return ($"unknown tool: {name}", true);

        var clock = Stopwatch.StartNew();
        try
        {
            var result = spec.Invoke(this, args);
            return AttachPerformance(result, clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext<SubstrateTools>().Warning(ex, "MCP tool {ToolName} threw.", name);
            return ($"{name} error: {ex.Message}", true);
        }
    }

    private static (string Text, bool IsError) AttachPerformance((string Text, bool IsError) result, double elapsedMs)
    {
        if (result.IsError || string.IsNullOrWhiteSpace(result.Text))
            return result;
        try
        {
            var node = JsonNode.Parse(result.Text);
            if (node is JsonObject obj && !obj.ContainsKey("elapsed_ms"))
            {
                obj["elapsed_ms"] = Math.Round(elapsedMs, 2);
                return (obj.ToJsonString(JsonSerializerOptions.Default), false);
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static byte[]? SessionBytes(string? sessionKey)
    {
        var key = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey.Trim();
        return key is null ? null : SubstrateCanonicalIds.ConversationSession(McpTenant, key).ToBytes();
    }

    private (string, bool) TurnAsync(string prompt, string session, string participant, bool readOnly)
    {
        EngineTurnResponse response = _engine.Value.TurnAsync(new EngineTurnRequest(
            FirmwarePath: null,
            ModelName: "program",
            Prompt: prompt,
            Messages: null,
            SystemPrompt: null,
            User: participant,
            Session: session,
            ReadOnly: readOnly)).GetAwaiter().GetResult();

        var obj = new JsonObject
        {
            ["reply"] = response.Text,
            ["prompt_id"] = response.PromptId.ToHex(),
            ["response_id"] = response.ResponseId.ToHex(),
            ["turn_id"] = response.TurnId == Hash128.Zero ? null : response.TurnId.ToHex(),
            ["disposition"] = response.Disposition,
            ["top_salience"] = response.TopSalience,
            ["mean_confidence"] = response.MeanConfidence,
            ["wall_us"] = response.Work.wall_us,
            ["db_probes"] = response.Work.db_probes,
            ["rows_scanned"] = response.Work.rows_scanned,
        };
        if (response.StatusCode != 0 && string.IsNullOrEmpty(response.Text) && !string.IsNullOrWhiteSpace(response.Error))
            return (response.Error, true);
        return (obj.ToJsonString(), false);
    }

    private (string, bool) Recall(JsonObject? args)
    {
        string prompt = Req(args, "prompt");
        var session = SessionBytes(Opt(args, "session") ?? _processSessionKey);
        var rows = NpgsqlSubstrateReads.RecallSessionAsync(_dbReadOnly, prompt, session, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["reply"] = r.Reply,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) Infer(JsonObject? args)
    {
        string prompt = Req(args, "prompt");
        int limit = Math.Clamp(Int(args, "limit", 12), 1, 64);
        using var conn = _dbReadOnly.OpenConnection();
        var rows = NpgsqlSubstrateReads.CompletionsAsync(conn, prompt, limit, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["entity_id"] = r.ObjectIdHex,
            ["type_id"] = r.TypeIdHex,
            ["label"] = r.ObjectLabel,
            ["score"] = r.EffectiveMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) SenseAudit(JsonObject? args)
    {
        string term = Req(args, "term");
        var id = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, term, default).GetAwaiter().GetResult()
            ?? Hash128.Text(term).ToBytes();
        using var conn = _dbReadOnly.OpenConnection();
        var rows = NpgsqlSubstrateReads.SensesAsync(conn, id, default).GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["sense_id"] = r.SenseIdHex,
            ["synset_id"] = r.SynsetIdHex,
            ["synset"] = r.SynsetLabel,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }

    private (string, bool) PromptLanguage(JsonObject? args)
    {
        string prompt = Req(args, "prompt");
        var wordIds = System.Text.RegularExpressions.Regex.Matches(prompt, @"\p{L}+")
            .Select(static m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .Select(static w => Hash128.Text(w).ToBytes())
            .ToArray();

        if (wordIds.Length > 0)
        {
            const string sql = """
                WITH q AS (
                    SELECT public.blake3_from_bytea(x) AS wid
                    FROM unnest($1::bytea[]) AS x
                ),
                holders AS (
                    SELECT h.entity, h.path
                    FROM public.laplace_holders(ARRAY(SELECT wid FROM q), '{}'::public.blake3[], false, -1::smallint, 64::bigint, 1::smallint) h
                    WHERE NOT h.hub AND h.path IS NOT NULL
                ),
                parts AS (
                    SELECT public.laplace_parts(h.path) AS p
                    FROM holders h
                ),
                lang_hits AS (
                    SELECT public.laplace_label_text(public.blake3_send(p[2]), true) AS lang_tag
                    FROM parts
                    WHERE cardinality(p) = 3
                      AND public.laplace_label_text(public.blake3_send(p[2]), true) ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$'
                )
                SELECT lang_tag, count(*)::int AS score
                FROM lang_hits
                WHERE lang_tag IS NOT NULL
                GROUP BY lang_tag
                ORDER BY score DESC, lang_tag ASC
                LIMIT 10
                """;
            try
            {
                using var conn = _dbReadOnly.OpenConnection();
                using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                cmd.Parameters.Add(new Npgsql.NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea,
                    Value = wordIds
                });
                var rows = new List<JsonObject>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add(new JsonObject
                    {
                        ["language"] = r.GetString(0),
                        ["score"] = r.GetInt32(1),
                        ["source"] = "substrate"
                    });
                }
                if (rows.Count > 0)
                    return JsonRows(rows);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException)
            {
                // Fall through to honest Unicode script classification when substrate is unreachable.
            }
        }

        var tallies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Rune rune in prompt.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsDigit(rune))
                continue;
            string bucket = rune.Value switch
            {
                <= 0x024F => "und-Latn",
                >= 0x0400 and <= 0x04FF => "und-Cyrl",
                >= 0x0600 and <= 0x06FF => "und-Arab",
                >= 0x4E00 and <= 0x9FFF => "und-Hani",
                >= 0x3040 and <= 0x30FF => "und-Jpan",
                >= 0xAC00 and <= 0xD7AF => "und-Hang",
                >= 0x0900 and <= 0x097F => "und-Deva",
                _ => "und",
            };
            tallies[bucket] = tallies.TryGetValue(bucket, out int n) ? n + 1 : 1;
        }
        return JsonRows(tallies.OrderByDescending(kv => kv.Value).Select(kv => new JsonObject
        {
            ["language"] = kv.Key,
            ["score"] = kv.Value,
            ["source"] = "script_fallback"
        }));
    }

    private (string, bool) Query(JsonObject? args)
    {
        string shape = Req(args, "shape");
        string topicRef = Req(args, "topic");
        var topic = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, topicRef, default).GetAwaiter().GetResult();
        if (topic is null)
            return ($"unknown topic: {topicRef}", true);

        byte[]? topic2 = null;
        if (Opt(args, "topic2") is { } t2Ref && !string.IsNullOrWhiteSpace(t2Ref))
            topic2 = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, t2Ref, default).GetAwaiter().GetResult();

        try
        {
            var rows = NpgsqlSubstrateReads.RecallIntentAsync(
                _dbReadOnly, shape, topic, topic2, Opt(args, "relation_type"), Opt(args, "lang"), null, default)
                .GetAwaiter().GetResult();
            return JsonRows(rows.Select(r => new JsonObject
            {
                ["reply"] = r.Reply,
                ["eff_mu"] = r.EffMu,
                ["witnesses"] = r.Witnesses,
            }));
        }
        catch (ArgumentException ex)
        {
            return (ex.Message, true);
        }
    }

    private (string, bool) Taxonomy(JsonObject? args)
    {
        byte[]? id = null;
        if (Opt(args, "entity") is { } entityHex && Hash128.TryParseHex(entityHex, out var parsed))
            id = parsed.ToBytes();
        else if (Opt(args, "term") is { } term && !string.IsNullOrWhiteSpace(term))
            id = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, term, default).GetAwaiter().GetResult();

        if (id is null)
            return ("taxonomy requires 'term' or valid hex 'entity'", true);

        int depth = Math.Clamp(Int(args, "depth", 4), 1, 12);
        int limit = Math.Clamp(Int(args, "limit", 24), 1, 100);
        var rows = NpgsqlSubstrateReads.TaxonomyTreeAsync(_dbReadOnly, id, depth, limit, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["dir"] = r.Dir,
            ["ord"] = r.Ord,
            ["entity_id"] = r.IdHex,
            ["label"] = r.Label,
            ["eff_mu"] = r.EffMu,
        }));
    }

    private (string, bool) Leaders(JsonObject? args)
    {
        var bandsStr = Opt(args, "bands") ?? "1,2,4,5";
        var bands = bandsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
            .Where(v => v >= 0)
            .Distinct()
            .ToArray();
        if (bands.Length == 0) bands = [1, 2, 4, 5];
        int per = Math.Clamp(Int(args, "limit", 10), 1, 50);

        var rows = NpgsqlSubstrateReads.ClaimLeadersNamedAsync(_dbReadOnly, bands, per, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Where(r => r.ClaimId is not null).Select(r => new JsonObject
        {
            ["band"] = r.Band,
            ["name"] = r.BandName,
            ["claim_id"] = r.ClaimId,
            ["parts"] = r.Parts,
            ["confidence"] = r.Confidence,
            ["witnesses"] = r.Witnesses,
            ["trajectory"] = r.Trajectory,
        }));
    }

    private (string, bool) Facts(JsonObject? args)
    {
        byte[]? id = null;
        if (Opt(args, "entity") is { } entityHex && Hash128.TryParseHex(entityHex, out var parsed))
            id = parsed.ToBytes();
        else if (Opt(args, "term") is { } term && !string.IsNullOrWhiteSpace(term))
            id = NpgsqlSubstrateReads.ResolveRefAsync(_dbReadOnly, term, default).GetAwaiter().GetResult()
                ?? Hash128.Text(term).ToBytes();

        if (id is null)
            return ("facts requires 'term' or valid hex 'entity'", true);

        int limit = Math.Clamp(Int(args, "limit", 24), 1, 200);
        var rows = NpgsqlSubstrateReads.SalientFactsAsync(_dbReadOnly, id, limit, default)
            .GetAwaiter().GetResult();
        return JsonRows(rows.Select(r => new JsonObject
        {
            ["type"] = r.Type,
            ["fact"] = r.Fact,
            ["eff_mu"] = r.EffMu,
            ["witnesses"] = r.Witnesses,
        }));
    }
}
