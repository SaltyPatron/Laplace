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

internal sealed partial class SubstrateTools : IMcpTools
{
    // Surface -> sense -> synset via taxonomy.bubble_up. Renders sense and synset
    // with realize.render() — the gloss text is the point here, not a display tag (see
    // the catalog's render-vs-label note) — and returns hex ids so the next call
    // reads from the resolved layer instead of re-entering text.
    private (string, bool) Bubble(JsonObject? args)
    {
        var id = NpgsqlSubstrateReads.ResolveRefAsync(
                _dbReadOnly, ChessPositionRef.RewriteFenToHex(Req(args, "term"))!, default)
            .GetAwaiter().GetResult();
        if (id is null)
            return JsonRows(Array.Empty<JsonObject>());

        using var cmd = NpgsqlCatalog.Command(_dbReadOnly, "taxonomy.bubble_up_rendered", id, Int(args, "k", 5));
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
            // 'procedure' means CALL, not SELECT — the op tool switches on this, and
            // a caller reading the catalog by hand needs the same warning.
            ["kind"] = r.Kind,
        }), int.MaxValue);
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
    /// Read-only is the DEFAULT, not the only mode. The datasource is chosen by the
    /// same <see cref="InstalledOpInvoker.WritableOps"/> allow-list the HTTP surface
    /// uses, because binding the read-only source unconditionally made every entry on
    /// that list unreachable from MCP: ops.analyze_substrate died as
    /// "substrate unavailable" against default_transaction_read_only and a 15s
    /// statement_timeout, which reads as a broken substrate rather than a tool that
    /// declined to route. Anything NOT on the list still resolves read-only and fails
    /// at the backend, so the allow-list is the grant and the name check is not a
    /// substitute for it.
    /// </summary>
    private (string, bool) Op(JsonObject? args)
    {
        var name = Req(args, "name");
        var supplied = args?["args"] as JsonObject;
        var dict = supplied?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var writable = InstalledOpInvoker.IsWritable(name);
        // Maintenance duration follows live relation size. Zero is Npgsql's explicit
        // unbounded value; the request remains cancellable and backend cancellation is
        // also exposed through the ops catalog.
        var timeout = Int(args, "timeout_seconds",
            writable ? 0
                     : InstalledOpInvoker.DefaultCommandTimeoutSeconds);
        if (timeout < 0)
            return ("timeout_seconds must be zero (unbounded) or a positive number of seconds.", true);

        var result = InstalledOpInvoker.InvokeAsync(
                writable ? _db : _dbReadOnly, name, dict,
                Int(args, "max_rows", DefaultRowCap), timeout, ct: default)
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
        // JsonValue.Create(null) preserves an unmeasured metric as JSON null.
        var rows = health.Select(h => new JsonObject
            {
                ["metric"] = h.Metric,
                ["value"] = h.Value is null ? null : JsonValue.Create(h.Value),
            })
            .Concat(counts.Select(c => new JsonObject { ["metric"] = c.Metric,
                ["value"] = c.Value is { } measured ? JsonValue.Create(measured) : null }))
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
            ["throughput_status"] = s.ThroughputStatus is null ? null : JsonValue.Create(s.ThroughputStatus),
            ["throughput_compared"] = s.ThroughputCompared,
            ["throughput_rows_per_s"] = s.ThroughputRowsPerS is null ? null : JsonValue.Create(s.ThroughputRowsPerS.Value),
            ["throughput_baseline_rows_per_s"] = s.ThroughputBaselineRowsPerS is null ? null : JsonValue.Create(s.ThroughputBaselineRowsPerS.Value),
            ["throughput_slowdown_ratio"] = s.ThroughputSlowdownRatio is null ? null : JsonValue.Create(s.ThroughputSlowdownRatio.Value),
        }));
    }

    private static (string, bool) JsonRows(IEnumerable<JsonObject> rows, int cap = DefaultRowCap)
    {
        var arr = new JsonArray();
        var truncated = false;
        foreach (var row in rows)
        {
            if (arr.Count >= cap) { truncated = true; break; }
            arr.Add(row);
        }
        var result = new JsonObject { ["rows"] = arr };
        if (truncated) result["truncated_at"] = cap;
        return (result.ToJsonString(), false);
    }

    // One turn of the session, answered by the forward program and witnessed by the Engine (laplace turn),
    // the same program the HTTP chat surface and the CLI run. The reply is what the turn realized; the
    // receipt says the turn's ID, its place in the session and the program's disposition.
    private (string, bool) ChatTurn(JsonObject? args)
    {
        var prompt = Req(args, "prompt");
        var sessionKey = Opt(args, "session") ?? _processSessionKey;
        if (!ConversationContent.IsValidIdentifier(sessionKey))
            return ("session must match [A-Za-z0-9._@-]{1,128}", true);
        NativeTurnReceipt turn;
        try { turn = _turns.Value.TurnAsync(McpTenant, sessionKey, prompt).GetAwaiter().GetResult(); }
        catch (Exception failure) when (failure is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
            return ($"The turn did not run: {failure.Message}", true);
        }
        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject { ["reply"] = turn.Response.Length == 0 ? null : turn.Response }),
            ["session"] = sessionKey,
            ["witnessed"] = turn.Witnessed,
            ["forward"] = new JsonObject
            {
                ["disposition"] = turn.Disposition,
                ["prompt_id"] = turn.PromptId,
                ["response_id"] = turn.ResponseId,
                ["turn_id"] = turn.TurnId,
                ["ordinal"] = turn.Ordinal,
                ["turns_before"] = turn.TurnsBefore,
                ["emitted"] = turn.Steps.Count,
            },
        };
        if (!turn.Witnessed && turn.Response.Length > 0) result["error"] = "The turn did not commit to the substrate: " + turn.Reason;
        return (result.ToJsonString(), !turn.Witnessed && turn.Response.Length > 0);
    }

    // The agent write lane: a note is content this agent gives, recorded by the Engine as what the runtime witness
    // mcp-local states at UserPromptContent (laplace witness): an observation, outranked by every curated source,
    // the same content wherever its text stands. The same note given again is the same file and adds nothing.
    private (string, bool) WitnessFact(JsonObject? args)
    {
        var text = Req(args, "text");
        if (text.Length == 0) return ("text is empty", true);
        RuntimeWitnessReceipt receipt;
        try
        {
            receipt = _witnesses.Value.RecordAsync(new NativeRuntimeWitness(McpTenant, FeedbackStatements.TrustClass),
                "note.txt", Encoding.UTF8.GetBytes(text)).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException failure) { return ($"The note was not recorded: {failure.Message}", true); }
        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["witness"] = receipt.WitnessId,
                ["file"] = receipt.FileId,
                ["witnessed"] = true,
            })
        };
        return (result.ToJsonString(), false);
    }

    // Confirm or refute one claim: this agent states its verdict through the one feedback lane
    // (FeedbackStatements, as HTTP /v1/feedback and the CLI attest), and the claim's Native standing is read
    // before and after.
    private (string, bool) Feedback(JsonObject? args)
    {
        var verdict = Req(args, "verdict").Trim().ToLowerInvariant();
        if (verdict is not ("confirm" or "refute"))
            return ("verdict must be 'confirm' or 'refute'", true);
        var subject = Opt(args, "subject");
        var relation = Opt(args, "relation");
        var obj = Opt(args, "object") ?? "";
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(relation))
            return ("feedback names one claim: subject and relation, and object unless the claim has none", true);
        FeedbackStatements.Result applied;
        try
        {
            applied = FeedbackStatements.StateAsync(_witnesses.Value, _db, McpTenant, subject, relation, obj,
                verdict == "confirm").GetAwaiter().GetResult();
        }
        catch (InvalidOperationException failure) { return ($"The verdict was not recorded: {failure.Message}", true); }

        static JsonObject? State(FeedbackStatements.Standing? s) => s is null ? null : new JsonObject
        {
            ["rating"] = s.Rating,
            ["deviation"] = s.Deviation,
            ["matches"] = s.Matches,
            ["confidence"] = s.Confidence,
        };
        var result = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["verdict"] = verdict,
                ["claim"] = applied.ClaimId,
                ["witness"] = applied.WitnessId,
                ["before"] = State(applied.Before),
                ["after"] = State(applied.After),
            })
        };
        return (result.ToJsonString(), false);
    }

    // The outbound lane: this substrate as a CLIENT of other models, symmetric with
    // Laplace.Endpoints.OpenAICompat serving them. Routing, credentials and per-wire
    // request shaping live in Laplace.Agents so the CLI and the HTTP surface reach
    // the same table rather than each growing a second one that drifts.
    //
    // One client for the process. A fresh HttpClient per call strands sockets in
    // TIME_WAIT, and this server can outlive a whole agent session. The CATALOG, by
    // contrast, is re-read per call — see the `ask` description.
    private ExternalAgentClient? _agents;
    private ExternalAgentClient AgentClient => _agents ??= new ExternalAgentClient();
}
