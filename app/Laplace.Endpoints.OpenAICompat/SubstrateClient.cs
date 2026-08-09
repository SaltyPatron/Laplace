using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient : ISubstrateClient, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlDataSource _dataSourceReadOnly;
    private readonly NpgsqlDataSource _dataSourceReadOnlyLong;

    public SubstrateClient()
    {
        _dataSource = LaplaceDataSource.Create(SubstrateAccess.Serving);
        // Same posture as MCP op: server-enforced read-only + statement timeout.
        _dataSourceReadOnly = LaplaceDataSource.Create(SubstrateAccess.Serving, dsb =>
        {
            dsb.ConnectionStringBuilder.CommandTimeout =
                InstalledOpInvoker.DefaultCommandTimeoutSeconds;
            dsb.ConnectionStringBuilder.Options =
                "-c default_transaction_read_only=on -c statement_timeout=15000";
        });
        // Explicitly requested long-running installed operations (generation
        // evals on a cold cache) remain read-only and server-bounded. Normal op
        // traffic stays on the 15-second datasource above.
        _dataSourceReadOnlyLong = LaplaceDataSource.Create(SubstrateAccess.Serving, dsb =>
        {
            dsb.ConnectionStringBuilder.CommandTimeout = InstalledOpInvoker.MaxCommandTimeoutSeconds;
            dsb.ConnectionStringBuilder.MaxPoolSize = 2;
            dsb.ConnectionStringBuilder.Options =
                "-c default_transaction_read_only=on -c statement_timeout=600000";
        });
    }

    internal NpgsqlDataSource DataSource => _dataSource;








    public async Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, CancellationToken ct)
        => await ConverseAsync(prompt, session, default, ct);

    public async Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, ConverseOptions options, CancellationToken ct)
    {
        try
        {
            // GH #575: bare FEN prompt → composed position hex before lexical recall_session.
            prompt = ChessPositionRef.RewriteFenToHex(prompt) ?? prompt;
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            return await RecallSessionAsync(conn, prompt, session, options, ct);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"recall_session query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }

    /// <summary>
    /// Tenant-isolated converse.converse(spec 34, opt-in): re-fold the tenant's own witnessed
    /// world via scoped_consensus into pg_temp.consensus, which shadows
    /// laplace.consensus for every unqualified read on THIS connection (the Build-A-
    /// Bear scoped-pour mechanism), then run the same session read. One connection
    /// per request; Npgsql's pool reset (DISCARD ALL) drops the temp table on return.
    /// </summary>
    public async Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources, CancellationToken ct)
        => await ConverseTenantScopedAsync(prompt, session, scopeSources, default, ct);

    public async Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources,
        ConverseOptions options, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using (var scopeCmd = new NpgsqlCommand(
                "CREATE TEMP TABLE consensus AS SELECT * FROM consensus.scoped_consensus(@sources)", conn))
            {
                scopeCmd.Parameters.AddWithValue("sources", scopeSources);
                await scopeCmd.ExecuteNonQueryAsync(ct);
            }
            return await RecallSessionAsync(conn, prompt, session, options, ct);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"scoped recall_session query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable.", ex);
        }
    }

    /// <summary>
    /// Multi-turn call shape kept for interface parity with older stateless clients;
    /// session state is substrate-resident here (spec 34), so only the newest turn
    /// is consumed — same rule RecallSessionAsync's comment documents.
    /// </summary>
    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        ConverseAsync(userTurns.Count > 0 ? userTurns[^1] : "", session, ct);

    private static async Task<IReadOnlyList<ConverseRow>> RecallSessionAsync(
        NpgsqlConnection conn, string prompt, byte[]? session,
        ConverseOptions options, CancellationToken ct)
    {
        // One turn in, one read out. Conversation state is substrate-resident
        // (session context + session_topics carry) — clients never resend history,
        // and a resent history would be ignored here by construction (spec 34).
        //
        // converse.chat() is the conversational entry point — orientation over the
        // prompt's candidate senses, session carry, shape/band lenses, and its own
        // internal fallbacks — the same lane the MCP chat tool and CLI ride. This
        // endpoint predated converse.chat() and was still reading recall_session directly,
        // which treats the prompt as a phrase lookup: measured on the deployed box,
        // "What is a dog?" answered "I hold \"a dog\" but no gloss or continuation
        // witnessed yet" on this lane while converse.chat() answered with the dog gloss.
        // recall_session stays as the fallback when chat yields nothing, so the
        // no-consensus case still reports truthfully instead of faking prose.
        // Tenant scoping is unaffected: converse.chat() reads `consensus` unqualified, so
        // the pg_temp.consensus shadow on THIS connection governs it the same way.
        var reply = await NpgsqlSubstrateReads.ChatAsync(
            conn, prompt, session, ct,
            shape: options.Shape,
            bands: options.Bands,
            elaborate: options.Elaborate);
        if (!string.IsNullOrWhiteSpace(reply))
            return [new ConverseRow(reply, null, null)];

        var rows = await NpgsqlSubstrateReads.RecallSessionAsync(conn, prompt, session, ct);
        return [.. rows.Select(r => new ConverseRow(r.Reply, r.EffMu, r.Witnesses))];
    }








    public async IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in NpgsqlSubstrateReads.WalkTextAsync(
            _dataSource, prompt, steps, maxOrder, temperature, topK, ct))
        {
            if (row.Entity.Length == 0) continue;
            yield return new GenerateToken(row.Step, row.Entity, row.StrideUsed);
        }
    }

    public async Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await NpgsqlSubstrateReads.CompletionsAsync(conn, prompt, Math.Max(1, limit), ct);
            return [.. rows.Select(r => new CompletionRow(
                r.ObjectIdHex, r.TypeIdHex, r.EffectiveMu, r.Witnesses, r.ObjectLabel))];
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate completions query failed.", ex);
        }
    }

    public async Task<SubstrateAuditReport> AuditReportAsync(bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            var counts = (await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, ct))
                .Select(r => new SubstrateCount(r.Metric, r.Value))
                .ToList();

            ConsensusHealth? consensus = null;
            if (includeConsensus)
                consensus = await ReadConsensusHealthAsync(conn, exactBudgetSeconds: 20, ct);

            long? multiSource = null;
            if (includeConvergence)
                multiSource = await TryReadMultiSourceCountAsync(conn, budgetSeconds: 20, ct);

            var topRelations = await ReadTopRelationsAsync(conn, Math.Clamp(topRelationLimit, 1, 200), ct);
            return new SubstrateAuditReport(counts, consensus, multiSource, topRelations);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate audit query failed.", ex);
        }
    }

    public async Task<SubstrateVisualizationGraph> VisualizationGraphAsync(int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var edges = await ReadTopRelationsAsync(conn, Math.Clamp(limit, 1, 500), ct);
            var nodes = edges
                .SelectMany(edge => new[]
                {
                    (id: edge.SubjectIdHex, label: edge.Subject),
                    (id: edge.ObjectIdHex, label: edge.Object)
                })
                .GroupBy(n => n.id, StringComparer.OrdinalIgnoreCase)
                .Select(g => (id: g.Key, label: g.First().label))
                .ToArray();

            var nodeIds = new byte[nodes.Length][];
            for (int i = 0; i < nodes.Length; i++)
                nodeIds[i] = Convert.FromHexString(nodes[i].id);

            // ONE round-trip: first physicality (lowest type) per node, keyed by array ordinal.
            var geometry = new (double X, double Y, double Z, double M, double Radius, int Constituents)?[nodes.Length];
            if (includeGeometry && nodes.Length > 0)
            {
                foreach (var row in await NpgsqlSubstrateReads.EntityPrimaryFormsBatchAsync(conn, nodeIds, ct))
                {
                    int idx = (int)row.Ordinal - 1;
                    if ((uint)idx >= (uint)geometry.Length) continue;
                    geometry[idx] = (row.X, row.Y, row.Z, row.M, row.Radius, row.Constituents);
                }
            }

            // ONE round-trip: evidence count per node over the same array.
            var evidence = new long?[nodes.Length];
            if (includeEvidence && nodes.Length > 0)
            {
                foreach (var row in await NpgsqlSubstrateReads.EvidenceCountsBatchAsync(conn, nodeIds, ct))
                {
                    int idx = (int)row.Ordinal - 1;
                    if ((uint)idx >= (uint)evidence.Length) continue;
                    evidence[idx] = row.Count;
                }
            }

            var output = new List<VisualizationNode>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++)
            {
                var physicality = geometry[i];
                output.Add(new VisualizationNode(
                    IdHex: nodes[i].id,
                    Label: nodes[i].label,
                    X: physicality?.X,
                    Y: physicality?.Y,
                    Z: physicality?.Z,
                    M: physicality?.M,
                    Radius: physicality?.Radius,
                    Constituents: physicality?.Constituents,
                    EvidenceRows: evidence[i]));
            }

            return new SubstrateVisualizationGraph(output, edges);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate visualization query failed.", ex);
        }
    }

    public async Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var steps = await NpgsqlSubstrateReads.ExplainTraceStepsAsync(
                conn, prompt, Math.Clamp(depth, 1, 64), Math.Clamp(beam, 1, 64), ct);
            var rows = steps.Select(s => new ExplainTraceStep(
                Depth: s.Depth,
                PathHex: s.PathHex,
                TypePathHex: s.TypePathHex,
                EntityIdHex: s.EntityIdHex,
                EntityLabel: s.EntityLabel,
                EffectiveMu: s.EffMu,
                PathMu: s.PathMu,
                Witnesses: s.Witnesses,
                Evidence: Array.Empty<EvidenceSample>())).ToList();

            if (!includeEvidence || rows.Count == 0)
                return rows;

            // ONE round-trip: batch evidence for every step's entity via LATERAL attestations_out,
            // then bucket client-side. Distinct ids collapse the (frequently repeated) entity_ids.
            var distinctHex = rows.Select(r => r.EntityIdHex).Distinct(StringComparer.Ordinal).ToArray();
            var ids = new byte[distinctHex.Length][];
            for (int i = 0; i < distinctHex.Length; i++)
                ids[i] = Convert.FromHexString(distinctHex[i]);

            var buckets = new Dictionary<string, List<EvidenceSample>>(StringComparer.Ordinal);
            foreach (var a in await NpgsqlSubstrateReads.AttestationsOutBatchAsync(conn, ids, perId: 5, ct))
            {
                var hex = distinctHex[(int)a.Ordinal - 1];
                if (!buckets.TryGetValue(hex, out var list))
                {
                    list = new List<EvidenceSample>();
                    buckets[hex] = list;
                }
                list.Add(new EvidenceSample(
                    TypeIdHex: a.TypeIdHex,
                    ObjectIdHex: a.ObjectIdHex,
                    SourceIdHex: a.SourceIdHex,
                    ContextIdHex: a.ContextIdHex,
                    Outcome: a.Outcome,
                    ObservationCount: a.ObservationCount));
            }

            var enriched = new List<ExplainTraceStep>(rows.Count);
            foreach (var row in rows)
            {
                IReadOnlyList<EvidenceSample> evidence =
                    buckets.TryGetValue(row.EntityIdHex, out var list) ? list : Array.Empty<EvidenceSample>();
                enriched.Add(row with { Evidence = evidence });
            }

            return enriched;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate explainability trace query failed.", ex);
        }
    }

    /// <summary>
    /// Exact consensus.stats() is a full count(*) over attestations plus a full aggregate
    /// over consensus — measured minutes at 135M/124M rows live. Attempt it within a
    /// bounded budget, then fall back to consensus.stats_approx() (planner
    /// estimates; avg/max witnesses come back NULL — that nullness IS the approximation
    /// signal in the contract).
    /// </summary>
    private static async Task<ConsensusHealth?> ReadConsensusHealthAsync(
        NpgsqlConnection conn, int exactBudgetSeconds, CancellationToken ct)
    {
        static ConsensusHealth? Map(NpgsqlSubstrateReads.ConsensusStatsRow? s) =>
            s is { } v
                ? new ConsensusHealth(
                    EvidenceRows: v.EvidenceRows,
                    ConsensusRows: v.ConsensusRows,
                    DedupRatio: v.DedupRatio,
                    AvgWitnesses: v.AvgWitnesses,
                    MaxWitnesses: v.MaxWitnesses)
                : null;

        try
        {
            var exact = await NpgsqlSubstrateReads.ConsensusStatsExactAsync(
                conn, ct, timeoutSeconds: exactBudgetSeconds);
            if (exact is not null) return Map(exact);
        }
        catch (Exception ex) when (IsStatementTimeout(ex) && !ct.IsCancellationRequested)
        {
            // exact variant blew its budget — fall through to the approx variant
        }

        try
        {
            return Map(await NpgsqlSubstrateReads.ConsensusStatsApproxAsync(
                conn, ct, timeoutSeconds: DefaultCommandTimeoutSeconds));
        }
        catch (Exception ex) when (IsStatementTimeout(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// multi_source_entity_count() is a GROUP BY over ALL attestations with a
    /// count(DISTINCT source_id) — 169M rows and growing, with no bound and no index that
    /// helps. Durable fix is a calculated-layer stat maintained post-ingest (doc 02, Issue 52).
    ///
    /// OFF BY DEFAULT ON THE SERVING PATH. A budget is not a bound: the query still burns
    /// its FULL budget of cache-cold random I/O before being cancelled, and then returns
    /// null anyway. MEASURED 2026-08-03: observed running 6m31s in DataFileRead against a
    /// live ingest on a disk that was already the constraint, then discarded. Paying that
    /// to compute nothing is strictly worse than not asking.
    ///
    /// Null already means "not computed" and every response contract tolerates it, so
    /// declining costs the caller a field it was going to lose on timeout regardless.
    /// Set LAPLACE_AUDIT_MULTISOURCE=1 to opt in when the substrate is idle.
    /// </summary>
    private static bool MultiSourceCountEnabled =>
        Environment.GetEnvironmentVariable("LAPLACE_AUDIT_MULTISOURCE") == "1";

    private static async Task<long?> TryReadMultiSourceCountAsync(
        NpgsqlConnection conn, int budgetSeconds, CancellationToken ct)
    {
        if (!MultiSourceCountEnabled) return null;
        try
        {
            return await NpgsqlSubstrateReads.MultiSourceEntityCountAsync(
                conn, ct, timeoutSeconds: budgetSeconds);
        }
        catch (Exception ex) when (IsStatementTimeout(ex) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Npgsql surfaces a tripped CommandTimeout as NpgsqlException wrapping a
    /// TimeoutException, or as PostgresException 57014 (query_canceled) when the server
    /// processed the cancel first. Connection-level failures are neither and must keep
    /// propagating as substrate_unavailable.</summary>
    private static bool IsStatementTimeout(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState == PostgresErrorCodes.QueryCanceled,
        NpgsqlException npg => npg.InnerException is TimeoutException,
        TimeoutException => true,
        _ => false
    };

    /// <summary>
    /// consensus.top_relations(@limit, NULL) computes edge_rank() per row over the FULL
    /// consensus table (124M+ rows) before its LIMIT — measured >9 minutes live, which
    /// killed /v1/explore/catalog, /v1/audit/report and /v1/visualizations/substrate.
    /// <see cref="NpgsqlSubstrateReads.TopRelationsAsync"/> supersedes it: consensus_eff_mu_btree
    /// serves the global top-M by raw eff_mu instantly; edge_rank (salience band x eff_mu, the
    /// single readout law) then reorders only that candidate pool. Measured 0.5s live.
    /// </summary>
    private static async Task<IReadOnlyList<VisualizationEdge>> ReadTopRelationsAsync(NpgsqlConnection conn, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.TopRelationsAsync(conn, limit, ct);
        return [.. rows.Select(t => new VisualizationEdge(
            SubjectIdHex: t.SubjectIdHex,
            Subject: t.Subject,
            TypeIdHex: t.TypeIdHex,
            Type: t.Type,
            ObjectIdHex: t.ObjectIdHex,
            Object: t.Object,
            EffectiveMu: t.EffMu,
            Witnesses: t.Witnesses))];
    }




    public async Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct)
    {
        // Provenance receipts: deduped (type, object) claims with named sources — not
        // consensus.consensus_out(that duplicates chat/salient-facts) or raw attestations_out
        // (one row per source/context cartesian product).
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId = null;
            string? entityLabel = null;
            var items = new List<Laplace.Api.Contracts.LabeledEvidenceItem>(limit);

            // GH #575: FEN → composed position hex before resolve_ref.
            target = ChessPositionRef.RewriteFenToHex(target) ?? target;
            foreach (var r in await NpgsqlSubstrateReads.EvidenceForTargetAsync(conn, target.Trim(), limit, ct))
            {
                if (entityId is null && r.EntityId is not null)
                {
                    entityId = r.EntityId;
                    entityLabel = r.EntityLabel;
                }
                if (r.TypeIdHex is null)
                    continue; // anchor row with no evidence
                items.Add(new Laplace.Api.Contracts.LabeledEvidenceItem(
                    TypeId: r.TypeIdHex,
                    TypeLabel: r.TypeLabel!,
                    ObjectId: r.ObjectIdHex!,
                    ObjectLabel: r.ObjectLabel!,
                    SourceId: "",
                    SourceLabel: r.SourceLabels ?? "",
                    ContextId: null,
                    Outcome: 2,
                    ObservationCount: r.WitnessCount ?? 0L,
                    EffMu: r.EffMu ?? 0m));
            }

            if (entityId is null)
                return null;

            return new EntityEvidence(Convert.ToHexStringLower(entityId), entityLabel!, items);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"evidence query failed [{pg.SqlState}] {pg.MessageText}", pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate evidence query failed.", ex);
        }
    }





    public async Task<ReadinessResponse> ReadinessAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            long entities = 0, consensus = 0;
            foreach (var row in await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, ct))
            {
                // Metric keys from ops.substrate_counts — relation roles, not Brand.table.
                if (row.Metric.Equals("entities(ESTIMATE)", StringComparison.Ordinal))
                    entities = Math.Max(entities, row.Value);
                else if (row.Metric.Equals("consensus(ESTIMATE)", StringComparison.Ordinal))
                    consensus = Math.Max(consensus, row.Value);
            }

            if (entities == 0 || consensus == 0)
            {
                var (entitiesExist, consensusExist) = await NpgsqlSubstrateReads.EntitiesAndConsensusExistAsync(conn, ct);
                if (entities == 0 && entitiesExist) entities = 1;
                if (consensus == 0 && consensusExist) consensus = 1;
            }

            bool perfcacheReady;
            string? detail = null;
            try
            {
                await NpgsqlSubstrateReads.PerfCacheProbeAsync(conn, ct);
                perfcacheReady = true;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState)
            {
                perfcacheReady = false;
                detail = pg.MessageText;
            }

            var ready = entities > 0 && consensus > 0 && perfcacheReady;
            if (ready)
                return new ReadinessResponse(true, true, entities, consensus, true);

            detail ??= entities == 0 ? "substrate has no entities (unseeded)"
                : consensus == 0 ? "substrate has no consensus relations (unseeded)"
                : "T0 perfcache not loaded";
            return new ReadinessResponse(false, true, entities, consensus, perfcacheReady, detail);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new ReadinessResponse(false, false, 0, 0, false, $"substrate unreachable: {ex.Message}");
        }
    }



    public async Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct)
    {
        // ONE round-trip: resolve CTE feeds both the physical form (kind=0 anchor row) and the
        // meaning neighbors (kind=1 rows, gated by @include). ORDER BY kind, ord preserves the
        // form-then-meaning read order and consensus_out_readable's internal ranking.
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId = null;
            EmbeddingForm? form = null;
            var meaning = new List<MeaningNeighbor>();

            // GH #575: FEN → composed position hex before resolve_ref.
            input = ChessPositionRef.RewriteFenToHex(input) ?? input;
            var rows = await NpgsqlSubstrateReads.EmbeddingLookupAsync(
                conn, input.Trim(), Math.Clamp(meaningLimit, 1, 100), includeMeaning, ct);
            foreach (var row in rows)
            {
                if (row.Kind == 0)
                {
                    entityId = row.EntityId;
                    if (row.X is not null)
                        form = new EmbeddingForm(
                            row.X.Value, row.Y!.Value, row.Z!.Value,
                            row.M!.Value, row.Radius!.Value, row.Constituents!.Value);
                }
                else
                {
                    meaning.Add(new MeaningNeighbor(
                        Relation: row.Relation ?? "?",
                        ObjectLabel: row.ObjectLabel ?? "?",
                        EffMu: row.EffMu ?? 0m,
                        Witnesses: row.Witnesses ?? 0L));
                }
            }

            if (entityId is null)
                return new EmbeddingResult(null, null, Array.Empty<MeaningNeighbor>());

            return new EmbeddingResult(Convert.ToHexStringLower(entityId), form, meaning);
        }
        catch (PostgresException pg)
        {
            throw new SubstrateQueryException(
                $"embedding query failed [{pg.SqlState}] {pg.MessageText}", pg);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate embedding query failed.", ex);
        }
    }

    public async Task<InstalledOpInvoker.OpResult> InvokeOpAsync(
        string name, IReadOnlyDictionary<string, JsonNode?>? args, int maxRows,
        int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var boundedTimeout = Math.Clamp(
                timeoutSeconds, 1, InstalledOpInvoker.MaxCommandTimeoutSeconds);
            var dataSource = boundedTimeout > InstalledOpInvoker.DefaultCommandTimeoutSeconds
                ? _dataSourceReadOnlyLong
                : _dataSourceReadOnly;
            return await InstalledOpInvoker.InvokeAsync(
                dataSource, name, args, maxRows, boundedTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or PostgresException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable for op.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSourceReadOnlyLong.DisposeAsync();
        await _dataSourceReadOnly.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    /// <summary>
    /// The serving budget. Kept as an alias so the existing call sites
    /// (SubstrateClient.Explore, Middleware's 503 envelope text) keep reading one
    /// value — the policy itself now lives with the datasource that applies it.
    /// </summary>
    internal const int DefaultCommandTimeoutSeconds = LaplaceDataSource.ServingCommandTimeoutSeconds;

    /// <summary>
    /// The one exception-translation rule every read in this client applies, now shared
    /// with <see cref="Laplace.SubstrateCRUD.Npgsql.NpgsqlSubstrateReads"/> callers via
    /// its <c>onError</c> delegate: a rejected query (<see cref="PostgresException"/>)
    /// is a client mistake worth naming (bad SQL state, a where-clause); anything else
    /// NpgsqlRead offers to translate (plain <see cref="NpgsqlException"/>,
    /// <see cref="TimeoutException"/>) means the server itself was unreachable.
    /// </summary>
    private static Exception TranslateSubstrateError(Exception failure, string label) =>
        failure is PostgresException pg
            ? new SubstrateQueryException(
                $"{label} query failed [{pg.SqlState}] {pg.MessageText}"
                + (pg.Where is null ? "" : $" @ {pg.Where}"), pg)
            : new SubstrateUnavailableException("Substrate is unreachable.", failure);
}

internal sealed class SubstrateUnavailableException : Exception
{
    public SubstrateUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}






internal sealed class SubstrateQueryException : Exception
{
    public SubstrateQueryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
