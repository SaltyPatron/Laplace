using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The sink is one COPY-once + call-once orchestrator over the native engine.
///
/// The client (this writer's caller) already holds every content id, geometry and
/// tier — laplace_core did the heavy compose during Transform. So ApplyManyAsync
/// does NOTHING heavy: it streams the batch in ONCE (a binary COPY of the native,
/// already-computed entity / physicality / attestation tuples into three UNLOGGED
/// staging tables) and then calls the single SPI function laplace_apply_batch once.
/// That one call does the entire light set-based merge inside the native extension:
/// set-based novelty (no per-row ON CONFLICT — partition-by-hash makes the keys
/// disjoint so collisions cannot occur) and the attestation observation-count fold.
///
/// There is deliberately NO client dedup cache, NO per-record existence check, NO
/// entities_exist_bitmap / intent_preflight preflight, and NO DISTINCT-ON /
/// anti-join in this writer: novelty is decided server-side in the one call. The
/// only dedup is the perfcache (client ids) + that in-call set-based merge.
/// </summary>
public sealed class NpgsqlSubstrateWriter : ISubstrateWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly NpgsqlSubstrateReader _reader;
    private readonly ILogger<NpgsqlSubstrateWriter> _log;
    private readonly int _applyPartitions;

    public NpgsqlSubstrateWriter(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlSubstrateWriter>? logger = null,
        bool bulkFreshSource = false)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlSubstrateReader(dataSource);
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
        _ = bulkFreshSource; // novelty is now decided server-side; no client preflight to toggle.
        _applyPartitions = ResolveApplyPartitions();
    }

    // PER-CONTENT-ID PARALLEL APPLY. A batch's staged rows are split natively by id.lo % N
    // into N disjoint partitions (intent_stage_partition). Each partition COPYs to its OWN
    // staging tables and runs laplace_apply_batch on its OWN connection in parallel. Because a
    // given content id lands in exactly one partition, the apply anti-joins never race on the
    // same id across partitions — no 23505, no ON CONFLICT, no serial lane. N defaults to the
    // P-core count (the apply is light/IO-ish so it scales to the commit fan-out, not compute).
    private static int ResolveApplyPartitions()
    {
        string? raw = Environment.GetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS");
        if (int.TryParse(raw, out int n) && n >= 1)
            return Math.Min(n, 64);
        return Math.Clamp(CpuTopology.PerformanceCoreCount, 1, 16);
    }

    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyManyAsync(new[] { change }, ct);
    }

    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sw = Stopwatch.StartNew();
        int roundTrips = 0;

        int entitiesAttempted = 0, physAttempted = 0, attAttempted = 0;
        for (int i = 0; i < changes.Count; i++)
        {
            if (!changes[i].TestimonyWalks.IsDefaultOrEmpty)
                throw new InvalidOperationException(
                    "testimony walks reached the evidence writer: walks are the consensus-only "
                    + "journal (the accumulating writer journals and strips them); evidence-"
                    + "persisting deposits emit AttestationRows at the decomposer");
            entitiesAttempted += changes[i].Entities.Length;
            physAttempted     += changes[i].Physicalities.Length;
            attAttempted      += changes[i].Attestations.Length;
        }
        if (changes.Count == 0)
            return new ApplyResult(0, 0, 0, 0, 0, 0, 0, sw.Elapsed, false);

        // Build ONE native stage holding this batch's already-computed tuples. The
        // decomposer may also have prebuilt grammar-compose stages (c.IntentStages);
        // those stream into the same COPY alongside the managed rows below.
        var prebuiltStages = new List<IntentStage>();
        foreach (var c in changes)
        {
            if (c.IntentStages.IsDefaultOrEmpty) continue;
            foreach (var pre in c.IntentStages)
                if (!pre.IsInvalid) prebuiltStages.Add(pre);
        }

        using var stage = IntentStage.New(
            Math.Max(Math.Max(entitiesAttempted, physAttempted), attAttempted));

        Span<double> coord = stackalloc double[4];

        // Within-batch de-dup only (a batch can legitimately re-emit the same id many
        // times). Across batches and across workers, the server-side set-based merge in
        // laplace_apply_batch is the sole novelty decision — there is no client cache.
        var seenEntity = new HashSet<Hash128>();
        var seenPhys   = new HashSet<Hash128>();
        var seenAtt    = new HashSet<Hash128>();

        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (!seenEntity.Add(e.Id)) continue;
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
            }
        foreach (var c in changes)
            foreach (var p in c.Physicalities)
            {
                if (!seenPhys.Add(p.Id)) continue;
                coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                stage.AddPhysicality(
                    p.Id, p.EntityId, (short)p.Type,
                    coord, p.HilbertIndex,
                    p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                              : p.TrajectoryXyzm.AsSpan(),
                    p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
            }
        foreach (var c in changes)
            foreach (var a in c.Attestations)
            {
                if (!seenAtt.Add(a.Id)) continue;
                stage.AddAttestation(
                    a.Id, a.SubjectId, a.TypeId, a.ObjectId, a.SourceId, a.ContextId,
                    (short)a.Outcome, a.LastObservedAtUnixUs, a.ObservationCount);
            }

        var sourceStages = new List<IntentStage>(prebuiltStages.Count + 1);
        sourceStages.AddRange(prebuiltStages);
        sourceStages.Add(stage);

        long entCount  = sourceStages.Sum(s => (long)s.EntityCount);
        long physCount = sourceStages.Sum(s => (long)s.PhysicalityCount);
        long attCount  = sourceStages.Sum(s => (long)s.AttestationCount);

        int entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        long attestationsFolded = 0;
        bool anyRows = entCount > 0 || physCount > 0 || attCount > 0;

        if (anyRows)
        {
            // PER-CONTENT-ID PARTITION (native, by id.lo % N). Each source stage partitions
            // independently; because the router is deterministic on the id, the union of the
            // k-th sub-stages across all source stages is still a disjoint partition k.
            int parts = Math.Max(1, _applyPartitions);

            // Partition every source stage; gather sub-stage[k] lists per partition.
            var perPartition = new List<IntentStage>[parts];
            for (int k = 0; k < parts; k++) perPartition[k] = new List<IntentStage>();
            var partitionHandles = new List<IntentStage>();
            try
            {
                foreach (var src in sourceStages)
                {
                    if (src.EntityCount == 0 && src.PhysicalityCount == 0 && src.AttestationCount == 0)
                        continue;
                    var split = src.Partition(parts);
                    for (int k = 0; k < parts; k++)
                    {
                        perPartition[k].Add(split[k]);
                        // Partition(1) returns the source itself; that handle is freed by its
                        // owner (the `stage` using / prebuiltStages dispose), so don't re-track it.
                        if (!ReferenceEquals(split[k], src))
                            partitionHandles.Add(split[k]);
                    }
                }

                var tasks = new Task<(int e, int p, int a, long f, int rt)>[parts];
                for (int k = 0; k < parts; k++)
                {
                    int idx = k;
                    tasks[k] = Task.Run(() => ApplyPartitionAsync(perPartition[idx], idx, ct), ct);
                }
                var results = await Task.WhenAll(tasks);

                foreach (var r in results)
                {
                    entitiesInserted      = (int)Math.Min(int.MaxValue, (long)entitiesInserted + r.e);
                    physicalitiesInserted = (int)Math.Min(int.MaxValue, (long)physicalitiesInserted + r.p);
                    attestationsInserted  = (int)Math.Min(int.MaxValue, (long)attestationsInserted + r.a);
                    attestationsFolded   += r.f;
                    roundTrips           += r.rt;
                }
            }
            finally
            {
                foreach (var h in partitionHandles) h.Dispose();
                foreach (var pre in prebuiltStages) pre.Dispose();
            }
        }
        else
        {
            foreach (var pre in prebuiltStages)
                pre.Dispose();
        }

        sw.Stop();

        long rowsAttempted = (long)entitiesAttempted + physAttempted + attAttempted;
        if (rowsAttempted >= 1000 && RtBudgetPer10K > 0)
        {
            long budget = 20 + RtBudgetPer10K * (rowsAttempted / 10_000 + 1);
            if (roundTrips > budget)
            {
                string msg = $"round-trip budget exceeded: {roundTrips} RT for {rowsAttempted:N0} rows "
                           + $"(budget {budget}; LAPLACE_RT_BUDGET_PER_10K={RtBudgetPer10K})";
                if (RtBudgetEnforce)
                    throw new InvalidOperationException(msg);
                _log.LogWarning("{Message}", msg);
            }
        }

        return new ApplyResult(
            EntitiesAttempted: entitiesAttempted,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesAttempted: physAttempted,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsAttempted: attAttempted,
            AttestationsInserted: attestationsInserted,
            RoundTrips: roundTrips,
            WallClock: sw.Elapsed,
            // Short-circuit = the merge found everything already present: nothing was
            // inserted and nothing was folded. Novelty is now decided server-side, so this
            // is read from the apply result rather than a client preflight. An empty batch
            // (no staged rows at all) also counts as a no-op short-circuit.
            TrunkShortcircuitHit:
                !anyRows ||
                (entitiesInserted == 0 && physicalitiesInserted == 0
                 && attestationsInserted == 0 && attestationsFolded == 0));
    }

    private static readonly long RtBudgetPer10K =
        long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_RT_BUDGET_PER_10K"), out var b) && b >= 0
            ? b : 64;
    private static readonly bool RtBudgetEnforce =
        Environment.GetEnvironmentVariable("LAPLACE_RT_BUDGET_ENFORCE") == "1";

    /// <summary>
    /// One partition's commit: its own connection + transaction, COPY its disjoint sub-stages
    /// into per-partition temp staging, then ONE laplace_apply_batch call. Runs concurrently with
    /// the other partitions; their key spaces never overlap, so the set-based anti-join in the SPI
    /// merge cannot collide cross-partition (no ON CONFLICT, no retry).
    /// </summary>
    private async Task<(int e, int p, int a, long f, int rt)> ApplyPartitionAsync(
        IReadOnlyList<IntentStage> stages, int partitionIndex, CancellationToken ct)
    {
        long entCount  = stages.Sum(s => (long)s.EntityCount);
        long physCount = stages.Sum(s => (long)s.PhysicalityCount);
        long attCount  = stages.Sum(s => (long)s.AttestationCount);
        if (entCount == 0 && physCount == 0 && attCount == 0)
            return (0, 0, 0, 0, 0);

        int rt = 0;
        int eIns = 0, pIns = 0, aIns = 0;
        long aFold = 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var guc = conn.CreateCommand())
            {
                guc.CommandText = "SET LOCAL session_replication_role = replica";
                await guc.ExecuteNonQueryAsync(ct);
            }
            rt++;

            // Unique prefix per (connection, partition): the temp tables are session-local, and
            // a distinct partition index keeps even same-thread reuse disjoint.
            string prefix = $"_lab_{Environment.CurrentManagedThreadId:x}_{partitionIndex:x}_";

            if (entCount > 0)
                await CopyStageAsync(conn, stages, IntentStageTable.Entities, "entities", prefix, ct);
            if (physCount > 0)
                await CopyStageAsync(conn, stages, IntentStageTable.Physicalities, "physicalities", prefix, ct);
            if (attCount > 0)
                await CopyStageAsync(conn, stages, IntentStageTable.Attestations, "attestations", prefix, ct);
            rt += (entCount > 0 ? 1 : 0) + (physCount > 0 ? 1 : 0) + (attCount > 0 ? 1 : 0);

            await using (var apply = conn.CreateCommand())
            {
                apply.CommandTimeout = 0;
                apply.CommandText =
                    "SELECT entities_inserted, physicalities_inserted, attestations_inserted, "
                    + "attestations_folded "
                    + "FROM laplace.laplace_apply_batch(@prefix)";
                apply.Parameters.AddWithValue("prefix", prefix);
                await using var rd = await apply.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    eIns  = (int)Math.Min(int.MaxValue, rd.GetInt64(0));
                    pIns  = (int)Math.Min(int.MaxValue, rd.GetInt64(1));
                    aIns  = (int)Math.Min(int.MaxValue, rd.GetInt64(2));
                    aFold = rd.GetInt64(3);
                }
            }
            rt++;

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }
        return (eIns, pIns, aIns, aFold, rt);
    }

    /// <summary>
    /// COPY all stages' native tuple blobs for one table into a per-call UNLOGGED
    /// staging table shaped exactly LIKE laplace.&lt;table&gt;. No promote here — the
    /// SPI call does the set-based merge. The native tuple buffers are
    /// header/trailer-free so every stage streams into one binary COPY.
    /// </summary>
    private static async Task CopyStageAsync(
        NpgsqlConnection conn,
        IReadOnlyList<IntentStage> stages,
        IntentStageTable table,
        string tableName,
        string prefix,
        CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        int expectedFields = table switch
        {
            IntentStageTable.Entities      => 4,
            IntentStageTable.Physicalities => 11,
            _                              => 9,
        };

        string stageName = $"{prefix}{tableName}";
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandTimeout = 0;
            // TEMP (not UNLOGGED): ON COMMIT DROP is only valid on temp tables, and a
            // session-local temp table keeps concurrent workers' staging disjoint without a
            // shared catalog name. The SPI function reads it by bare name via pg_temp.
            ddl.CommandText =
                $"CREATE TEMP TABLE IF NOT EXISTS \"{stageName}\" " +
                $"(LIKE laplace.{tableName} INCLUDING DEFAULTS) ON COMMIT DROP; " +
                $"TRUNCATE \"{stageName}\";";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        var blobs = new List<(IntPtr Ptr, long Len)>(stages.Count);
        foreach (var s in stages)
        {
            int rowCount = table switch
            {
                IntentStageTable.Entities      => s.EntityCount,
                IntentStageTable.Physicalities => s.PhysicalityCount,
                _                              => s.AttestationCount,
            };
            if (rowCount == 0) continue;

            (IntPtr ptr, long len) = s.TupleBuffer(table);
            if (CopyBlobValidator.Enabled)
                CopyBlobValidator.Validate(ptr, len, expectedFields, tableName, rowCount);
            blobs.Add((ptr, len));
        }

        if (blobs.Count > 0)
        {
            await using var stream = await conn.BeginRawBinaryCopyAsync(
                $"COPY \"{stageName}\" ({cols}) FROM STDIN (FORMAT BINARY)", ct);
            await PgBinaryCopy.WriteNativeBlobsAsync(stream, blobs, ct);
        }
    }

    // AppendAsync / FinalizeSourceAsync are intentionally NOT overridden: the
    // ISubstrateWriter defaults route AppendAsync straight to ApplyManyAsync (the
    // one COPY-once + laplace_apply_batch call) and make FinalizeSourceAsync a no-op.
    // The old deferred-staging merge (SubstrateStagingMerge) is gone — novelty and the
    // observation-count fold are now decided server-side in the single SPI call, so a
    // batch goes live on its own commit and there is nothing left to finalize.
}
