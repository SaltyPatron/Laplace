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

    public SubstrateClient()
    {
        _dataSource = LaplaceDataSource.Create(SubstrateAccess.Serving);
        // Read-only is enforced by the server session. Each command sets its own
        // timeout; a datasource-wide statement_timeout would override it.
        _dataSourceReadOnly = LaplaceDataSource.Create(SubstrateAccess.Serving, dsb =>
        {
            dsb.ConnectionStringBuilder.CommandTimeout =
                InstalledOpInvoker.DefaultCommandTimeoutSeconds;
            dsb.ConnectionStringBuilder.Options =
                "-c default_transaction_read_only=on";
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
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            return await RunConversationAsync(conn, prompt, session, options, ct);
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
    /// Folds consensus over only <paramref name="scopeSources"/> into pg_temp.consensus,
    /// which shadows laplace.consensus for every unqualified read on this connection, then
    /// runs the same forward pass. The fact rows are unchanged; only the standing the pass
    /// reads is scoped. Npgsql's pool reset (DISCARD ALL) drops the temp table on return.
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
            return await RunConversationAsync(conn, prompt, session, options, ct);
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
    /// Sends only the newest user turn. Earlier turns are already witnessed under
    /// <paramref name="session"/> in the substrate.
    /// </summary>
    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        ConverseAsync(userTurns.Count > 0 ? userTurns[^1] : "", session, ct);

    private static async Task<IReadOnlyList<ConverseRow>> RunConversationAsync(
        NpgsqlConnection conn, string prompt, byte[]? session,
        ConverseOptions options, CancellationToken ct)
    {
        // Shape, bands, or elaborate select the inspecting chat read; otherwise the
        // forward turn runs. An empty result stays empty; nothing substitutes a reply.
        bool inspection = !string.IsNullOrWhiteSpace(options.Shape)
            || options.Bands is { Length: > 0 } || options.Elaborate;
        var reply = inspection
            ? await NpgsqlSubstrateReads.ChatAsync(
            conn, prompt, session, ct,
            shape: options.Shape,
            bands: options.Bands,
            elaborate: options.Elaborate,
            language: options.Language)
            : await NpgsqlSubstrateReads.ForwardTurnAsync(
                conn, prompt, session, options.MaxTokens ?? 128,
                options.Window ?? 5, options.Temperature ?? 0.6,
                options.TopK ?? 10, ct);
        if (!string.IsNullOrEmpty(reply))
            return [new ConverseRow(reply, null, null)];

        return [];
    }








    public IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt, int steps = 32, int maxOrder = 5,
        double temperature = 0.7, int topK = 10, CancellationToken ct = default) =>
        ForwardTurnStreamAsync(prompt, null,
            new ConverseOptions(MaxTokens: steps, Window: maxOrder, Temperature: temperature, TopK: topK), ct);

    public async IAsyncEnumerable<GenerateToken> ForwardTurnStreamAsync(
        string prompt, byte[]? session, ConverseOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in NpgsqlSubstrateReads.ForwardTurnStepsAsync(
            _dataSource, prompt, session, options.MaxTokens ?? 128,
            options.Window ?? 5, options.Temperature ?? 0.6, options.TopK ?? 10, ct))
            yield return new GenerateToken(row.Step, row.Entity, row.StrideUsed);
    }

    public async Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await NpgsqlSubstrateReads.CompletionsAsync(conn, prompt, Math.Max(0, limit), ct);
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

            var topRelations = await ReadTopRelationsAsync(conn, Math.Max(0, topRelationLimit), ct);
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
            var samples = await NpgsqlSubstrateReads.ConstellationSampleAsync(
                conn, Math.Max(0, limit), ct);
            if (samples.Count == 0)
                return new SubstrateVisualizationGraph([], []);

            var sampleHex = samples.Select(s => s.IdHex).ToList();
            var labels = await ReadDisplayLabelsAsync(conn, sampleHex, ct);
            var nodeIds = samples.Select(s => Convert.FromHexString(s.IdHex)).ToArray();

            var evidence = new long?[samples.Count];
            if (includeEvidence)
            {
                foreach (var row in await NpgsqlSubstrateReads.EvidenceCountsBatchAsync(conn, nodeIds, ct))
                {
                    var idx = (int)row.Ordinal - 1;
                    if ((uint)idx < (uint)evidence.Length) evidence[idx] = row.Count;
                }
            }

            var output = new List<VisualizationNode>(samples.Count);
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                output.Add(new VisualizationNode(
                    IdHex: sample.IdHex,
                    Label: DisplayLabel(labels, sample.IdHex, sample.IdHex),
                    X: includeGeometry ? sample.X : null,
                    Y: includeGeometry ? sample.Y : null,
                    Z: includeGeometry ? sample.Z : null,
                    M: includeGeometry ? sample.M : null,
                    Radius: includeGeometry ? sample.Radius : null,
                    Constituents: includeGeometry ? sample.Constituents : null,
                    EvidenceRows: evidence[i]));
            }

            // The nodes are a spatial sample of physicality coordinates. Edges between
            // sampled nodes are not implied by that sample, so none are returned.
            return new SubstrateVisualizationGraph(output, []);
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
                conn, prompt, Math.Max(0, depth), Math.Max(0, beam), ct);
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
    /// consensus.stats() scans every attestation and consensus row, so it runs under
    /// <paramref name="exactBudgetSeconds"/>; on timeout consensus.stats_approx() answers
    /// from planner estimates, with null avg/max witnesses marking the estimate. Null when
    /// both time out.
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
            // The exact read timed out; the planner estimate answers below.
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
    /// ops.multi_source_entity_count() groups every attestation by count(DISTINCT
    /// source_id) with no bounding index; a timeout spends the whole budget in I/O and
    /// still yields nothing. It runs only when LAPLACE_AUDIT_MULTISOURCE=1; otherwise the
    /// field is null, which the contract reads as "not computed".
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
    /// consensus.top_relations(@limit, NULL) reads the governed edge-rank expression
    /// index on every consensus leaf, so the top edges come from one bounded merge
    /// rather than a sort of the consensus table.
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
        // Provenance receipts: one row per deduped (type, object) claim with its source
        // labels and witness count, not one row per source/context attestation.
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            byte[]? entityId = null;
            string? entityLabel = null;
            var items = new List<Laplace.Api.Contracts.LabeledEvidenceItem>(limit);

            // A FEN resolves as the id of its composed position.
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
        var chessPerfcache = ObserveChessPerfcache();
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);

            long entities = 0, consensus = 0;
            foreach (var row in await NpgsqlSubstrateReads.SubstrateCountsAsync(conn, ct))
            {
                // Metric keys are ops.substrate_counts labels.
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

            bool postgresPerfcacheReady;
            string? detail = null;
            try
            {
                await NpgsqlSubstrateReads.PerfCacheProbeAsync(conn, ct);
                postgresPerfcacheReady = true;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.ObjectNotInPrerequisiteState)
            {
                postgresPerfcacheReady = false;
                detail = pg.MessageText;
            }

            // The PostgreSQL backend and this process load the T0 perfcache
            // independently; readiness requires both.
            bool processPerfcacheReady = CodepointPerfcache.IsLoaded;
            if (!processPerfcacheReady)
            {
                try
                {
                    CodepointPerfcache.LoadDefault();
                    processPerfcacheReady = true;
                }
                catch (Exception ex)
                {
                    processPerfcacheReady = false;
                    detail = string.IsNullOrEmpty(detail)
                        ? ex.Message
                        : detail + "; " + ex.Message;
                }
            }

            bool perfcacheReady = postgresPerfcacheReady && processPerfcacheReady;
            var ready = entities > 0 && consensus > 0 && perfcacheReady;
            if (ready)
                return new ReadinessResponse(true, true, entities, consensus, true, ChessPerfcache: chessPerfcache);

            detail ??= entities == 0 ? "substrate has no entities (unseeded)"
                : consensus == 0 ? "substrate has no consensus relations (unseeded)"
                : !postgresPerfcacheReady ? "T0 perfcache not loaded in PostgreSQL"
                : "T0 perfcache not loaded in this process";
            return new ReadinessResponse(false, true, entities, consensus, perfcacheReady, detail, chessPerfcache);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new ReadinessResponse(false, false, 0, 0, false, $"substrate unreachable: {ex.Message}", chessPerfcache);
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

            // A FEN resolves as the id of its composed position.
            input = ChessPositionRef.RewriteFenToHex(input) ?? input;
            var rows = await NpgsqlSubstrateReads.EmbeddingLookupAsync(
                conn, input.Trim(), Math.Max(0, meaningLimit), includeMeaning, ct);
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
            var requestedTimeout = InstalledOpInvoker.RequestedCommandTimeout(timeoutSeconds);
            // Ops on the write allow-list run on the writable datasource; every
            // other op runs under default_transaction_read_only.
            var dataSource = InstalledOpInvoker.IsWritable(name)
                ? _dataSource
                : _dataSourceReadOnly;
            return await InstalledOpInvoker.InvokeAsync(
                dataSource, name, args, maxRows, requestedTimeout, ct).ConfigureAwait(false);
        }
        // A PostgresException outside the availability classes is the operation's
        // own answer (a RAISE, a bad argument, a failed precondition). It returns as
        // an op error carrying the function's message, not as unavailability.
        catch (PostgresException pg) when (!IsAvailabilitySqlState(pg.SqlState))
        {
            return new InstalledOpInvoker.OpResult(
                [], null, $"op '{name}' failed [{pg.SqlState}] {pg.MessageText}");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or PostgresException)
        {
            throw new SubstrateUnavailableException("Substrate is unreachable for op.", ex);
        }
    }

    /// <summary>
    /// SQLSTATE classes that mean the cluster rather than the query, and so must stay
    /// 503: 08 connection_exception, 53 insufficient_resources, 57 operator_intervention,
    /// 58 system_error (53000 covers fd exhaustion, "could not open shared memory
    /// segment"). PostgresException derives from NpgsqlException, so this is tested first.
    /// </summary>
    private static bool IsAvailabilitySqlState(string? sqlState) =>
        sqlState is { Length: >= 2 } && sqlState[..2] is "08" or "53" or "57" or "58";

    public async ValueTask DisposeAsync()
    {
        await _dataSourceReadOnly.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    /// <summary>
    /// Serving command timeout, as applied by <see cref="LaplaceDataSource"/>.
    /// </summary>
    internal const int DefaultCommandTimeoutSeconds = LaplaceDataSource.ServingCommandTimeoutSeconds;

    /// <summary>
    /// Exception translation passed as the <c>onError</c> delegate to
    /// <see cref="Laplace.SubstrateCRUD.Npgsql.NpgsqlSubstrateReads"/>: a
    /// <see cref="PostgresException"/> is the query's own rejection and is named with its
    /// SQL state; a plain <see cref="NpgsqlException"/> or <see cref="TimeoutException"/>
    /// means the server was unreachable.
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
    public SubstrateUnavailableException(string message)
        : base(message)
    {
    }

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
