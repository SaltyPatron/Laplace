using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class NpgsqlSubstrateWriter : ISubstrateWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly NpgsqlSubstrateReader _reader;
    private readonly ILogger<NpgsqlSubstrateWriter> _log;
    private readonly ProvenIdCache _provenEntities;
    private readonly ProvenIdCache _provenPhys;
    private readonly ProvenIdCache _provenAtt;
    private readonly bool _bulkFreshSource;
    private readonly SubstrateStagingMerge _staging;

    public NpgsqlSubstrateWriter(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlSubstrateWriter>? logger = null,
        bool bulkFreshSource = false)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlSubstrateReader(dataSource);
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
        _bulkFreshSource = bulkFreshSource;
        _staging = new SubstrateStagingMerge(dataSource);
        bool cacheOn = Environment.GetEnvironmentVariable("LAPLACE_PROVEN_CACHE") != "0";
        // Bounded client-side "already in the DB" cache (3 of these: ent/phys/att). 32M was ~5GB of
        // ConcurrentDictionary across the three on a fresh insert-heavy source; 4M keeps it ~600MB and
        // still absorbs the hot repeats — cold misses fall through to the DB ON CONFLICT floor.
        int cacheMax = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_PROVEN_CACHE_MAX"), out var m) && m > 0
            ? m : 4_000_000;
        _provenEntities = new ProvenIdCache(cacheOn, cacheMax);
        _provenPhys     = new ProvenIdCache(cacheOn, cacheMax);
        _provenAtt      = new ProvenIdCache(cacheOn, cacheMax);
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

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var uniqueEntityIds = new List<Hash128>(entitiesAttempted);
        var seenEntityArg = new HashSet<Hash128>();
        foreach (var c in changes)
            foreach (var e in c.Entities)
                if (seenEntityArg.Add(e.Id)) uniqueEntityIds.Add(e.Id);

        // Entities/physicalities are NOT preflighted. Probing every id up front
        // (pg_laplace_intent_preflight) did not scale to per-batch re-emission volume and crashed the
        // backend ("connection forcibly closed"); and the ProvenId caches it fed grew UNBOUNDED across
        // the run (the same boil-the-ocean as the just-deleted content bank). They dedup per-batch
        // (the staged HashSets below) + at the DB by content address (INSERT ... ON CONFLICT (id) DO
        // NOTHING). Attestations still preflight (unless this is a fresh bulk source) because their
        // insert is ON CONFLICT DO NOTHING, so an already-present attestation must route to the locked
        // observation_count UPDATE below to sum re-observations.
        var physToCheck = new List<Hash128>();
        var attToCheck = _bulkFreshSource
            ? new List<Hash128>()
            : IntentPreflight.CollectUnprovenIds(changes, static c => c.Attestations, static a => a.Id, _provenAtt);

        // P2 — "which ones don't you have?" Rather than staging every entity and leaning on the
        // per-row ON CONFLICT floor, ask the DB once via the scalable, chunked entities_exist_bitmap
        // (NOT the old per-id intent_preflight that crashed the backend) which of this batch's ids it
        // already holds, then stage only the novel ones. A fresh bulk source skips the probe (the DB
        // holds nothing for it yet, so every id is novel and asking is wasted round trips); a re-ingest
        // or incremental run gets the idempotency win — present ids are never re-staged or re-COPYed.
        var existingEntities = _bulkFreshSource
            ? new HashSet<Hash128>()
            : await IntentPreflight.EntitiesExistAsync(conn, uniqueEntityIds, ct);
        if (!_bulkFreshSource && uniqueEntityIds.Count > 0) roundTrips++;

        var (_, existingPhys, existingAtt) = await IntentPreflight.RunAsync(
            conn, System.Array.Empty<Hash128>(), physToCheck, attToCheck, ct);
        if (physToCheck.Count > 0 || attToCheck.Count > 0) roundTrips++;
        _provenEntities.AddRange(existingEntities);
        _provenPhys.AddRange(existingPhys);
        _provenAtt.AddRange(existingAtt);

        var prebuiltStages = new List<IntentStage>();
        foreach (var c in changes)
        {
            if (c.IntentStages.IsDefaultOrEmpty) continue;
            foreach (var pre in c.IntentStages)
            {
                if (!pre.IsInvalid) prebuiltStages.Add(pre);
            }
        }

        using var stage = IntentStage.New(Math.Max(Math.Max(uniqueEntityIds.Count, physAttempted), attAttempted));

        var seenEntity = new HashSet<Hash128>(uniqueEntityIds.Count);
        var seenPhys   = new HashSet<Hash128>(existingPhys);
        var seenAtt    = new HashSet<Hash128>(existingAtt);
        var stagedPhysIds = new List<Hash128>();
        var stagedAttIds  = new List<Hash128>();
        Span<double> coord = stackalloc double[4];

        // Referential integrity is no longer pre-checked per batch. Identity is content-addressed
        // and the tier DAG is acyclic, so a "forward" reference to a not-yet-deposited entity is not
        // an error: the referent lands with the identical id (this batch, a sibling worker, or
        // already present), and FK/triggers are disabled (session_replication_role=replica) for the
        // bulk insert. End-of-run soundness is proven by reconstruction (db-roundtrip) — the same
        // trunk⟹leaves containment the dedup descent relies on. Deleting this EXISTS check is what
        // frees decomposers from the global two-pass / StrictSerial it used to force.
        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (existingEntities.Contains(e.Id)) continue;
                if (_provenEntities.Contains(e.Id)) continue;
                if (!seenEntity.Add(e.Id)) continue;
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
            }
        CopyBlobValidator.Checkpoint(stage, "after-entity-loop");
        foreach (var c in changes)
            foreach (var p in c.Physicalities)
            {
                if (_provenPhys.Contains(p.Id)) continue;
                if (!seenPhys.Add(p.Id)) continue;
                coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                stage.AddPhysicality(
                    p.Id, p.EntityId, p.SourceId, (short)p.Type,
                    coord, p.HilbertIndex,
                    p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                              : p.TrajectoryXyzm.AsSpan(),
                    p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
                stagedPhysIds.Add(p.Id);
            }
        CopyBlobValidator.Checkpoint(stage, "after-physicality-loop");
        var attGamesDelta = new Dictionary<Hash128, (long Games, long MaxTsUs)>();
        foreach (var c in changes)
            foreach (var a in c.Attestations)
            {
                if (_provenAtt.Contains(a.Id) || !seenAtt.Add(a.Id))
                {
                    var d = attGamesDelta.TryGetValue(a.Id, out var cur) ? cur : (0L, 0L);
                    attGamesDelta[a.Id] = (checked(d.Item1 + a.ObservationCount),
                                           Math.Max(d.Item2, a.LastObservedAtUnixUs));
                    continue;
                }
                stage.AddAttestation(
                    a.Id, a.SubjectId, a.TypeId, a.ObjectId, a.SourceId, a.ContextId,
                    (short)a.Outcome,
                    a.LastObservedAtUnixUs, a.ObservationCount);
                stagedAttIds.Add(a.Id);
            }
        CopyBlobValidator.Checkpoint(stage, "after-attestation-loop");

        int entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        bool anyPrebuilt = prebuiltStages.Count > 0;
        bool anyRows = anyPrebuilt
                       || stage.EntityCount > 0 || stage.PhysicalityCount > 0 || stage.AttestationCount > 0
                       || attGamesDelta.Count > 0;

        if (anyRows)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var guc = conn.CreateCommand())
                {
                    guc.CommandText = "SET LOCAL session_replication_role = replica";
                    await guc.ExecuteNonQueryAsync(ct);
                }
                roundTrips++;

                if (prebuiltStages.Count > 0)
                {
                    try
                    {
                        entitiesInserted += await StageAndInsertManyAsync(
                            conn, prebuiltStages, IntentStageTable.Entities, "entities", ct);
                        physicalitiesInserted += await StageAndInsertManyAsync(
                            conn, prebuiltStages, IntentStageTable.Physicalities, "physicalities", ct);
                        roundTrips += 2;
                    }
                    finally
                    {
                        foreach (var pre in prebuiltStages)
                            pre.Dispose();
                    }
                }

                if (stage.EntityCount > 0)
                {
                    entitiesInserted += await StageAndInsertAsync(
                        conn, stage, IntentStageTable.Entities, "entities", ct);
                    roundTrips += 3;
                }

                if (stage.PhysicalityCount > 0)
                {
                    physicalitiesInserted = await StageAndInsertAsync(
                        conn, stage, IntentStageTable.Physicalities, "physicalities", ct);
                    roundTrips += 3;
                }
                if (stage.AttestationCount > 0)
                {
                    attestationsInserted = await StageAndInsertAsync(
                        conn, stage, IntentStageTable.Attestations, "attestations", ct);
                    roundTrips += 3;
                }
                if (attGamesDelta.Count > 0)
                {
                    var ids   = new byte[attGamesDelta.Count][];
                    var games = new long[attGamesDelta.Count];
                    var tsUs  = new long[attGamesDelta.Count];
                    int di = 0;
                    foreach (var kv in attGamesDelta)
                    {
                        ids[di]   = kv.Key.ToBytes();
                        games[di] = kv.Value.Games;
                        tsUs[di]  = kv.Value.MaxTsUs;
                        di++;
                    }
                    await using var upd = conn.CreateCommand();
                    upd.CommandTimeout = 0;
                    upd.CommandText =
                        "WITH d AS MATERIALIZED (" +
                        "  SELECT unnest(@ids) AS id, unnest(@games) AS games, unnest(@ts) AS ts_us" +
                        "), locked AS MATERIALIZED (" +
                        "  SELECT a.id FROM laplace.attestations a " +
                        "  WHERE a.id IN (SELECT id FROM d) ORDER BY a.id FOR UPDATE" +
                        ") " +
                        "UPDATE laplace.attestations a SET " +
                        "  observation_count = a.observation_count + d.games, " +
                        "  last_observed_at  = GREATEST(a.last_observed_at, to_timestamp(d.ts_us / 1e6)) " +
                        "FROM d " +
                        "WHERE a.id = d.id AND a.id IN (SELECT id FROM locked)";
                    upd.Parameters.AddWithValue("ids",   ids);
                    upd.Parameters.AddWithValue("games", games);
                    upd.Parameters.AddWithValue("ts",    tsUs);
                    await upd.ExecuteNonQueryAsync(ct);
                    roundTrips++;
                }
                await tx.CommitAsync(ct);

                _provenEntities.AddRange(seenEntity);
                _provenPhys.AddRange(stagedPhysIds);
                _provenAtt.AddRange(stagedAttIds);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { }
                throw;
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
            TrunkShortcircuitHit: !anyRows);
    }

    private static readonly long RtBudgetPer10K =
        long.TryParse(Environment.GetEnvironmentVariable("LAPLACE_RT_BUDGET_PER_10K"), out var b) && b >= 0
            ? b : 64;
    private static readonly bool RtBudgetEnforce =
        Environment.GetEnvironmentVariable("LAPLACE_RT_BUDGET_ENFORCE") == "1";

    private static async Task<int> StageAndInsertAsync(
        NpgsqlConnection conn, IntentStage stage, IntentStageTable table, string tableName, CancellationToken ct)
        => await StageAndInsertManyAsync(conn, new[] { stage }, table, tableName, ct);

    private static async Task<int> StageAndInsertManyAsync(
        NpgsqlConnection conn,
        IReadOnlyList<IntentStage> stages,
        IntentStageTable table,
        string tableName,
        CancellationToken ct)
    {
        int totalRowCount = 0;
        foreach (var s in stages)
        {
            totalRowCount += table switch
            {
                IntentStageTable.Entities      => s.EntityCount,
                IntentStageTable.Physicalities => s.PhysicalityCount,
                _                              => s.AttestationCount,
            };
        }
        if (totalRowCount == 0) return 0;

        string cols = IntentStage.CopyColumnList(table);
        int expectedFields = table switch
        {
            IntentStageTable.Entities      => 4,
            IntentStageTable.Physicalities => 11,
            _                              => 9,
        };

        string stageName = $"_laplace_stage_{tableName}";
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandTimeout = 0;
            ddl.CommandText =
                $"CREATE TEMP TABLE IF NOT EXISTS {stageName} " +
                $"(LIKE laplace.{tableName} INCLUDING DEFAULTS) ON COMMIT DROP; " +
                $"TRUNCATE {stageName};";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // One COPY per (table, call) — not one per stage. The native tuple buffers are
        // header/trailer-free, so every stage's blob streams into a single binary COPY
        // (Header once → all blobs → Trailer once). A ConceptNet committed batch carries
        // ~one content stage per 65536-row grammar batch (commitRows=4M ⇒ ~60 stages); the
        // per-stage COPY loop turned that into ~120 round-trips/batch. This collapses it to one.
        var blobs = new List<(IntPtr Ptr, long Len)>(stages.Count);
        foreach (var stage in stages)
        {
            int rowCount = table switch
            {
                IntentStageTable.Entities      => stage.EntityCount,
                IntentStageTable.Physicalities => stage.PhysicalityCount,
                _                              => stage.AttestationCount,
            };
            if (rowCount == 0) continue;

            (IntPtr ptr, long len) = stage.TupleBuffer(table);
            if (CopyBlobValidator.Enabled)
                CopyBlobValidator.Validate(ptr, len, expectedFields, tableName, rowCount);

            blobs.Add((ptr, len));
        }

        if (blobs.Count > 0)
        {
            await using var stream = await conn.BeginRawBinaryCopyAsync(
                $"COPY {stageName} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
            await PgBinaryCopy.WriteNativeBlobsAsync(stream, blobs, ct);
        }

        await using (var promote = conn.CreateCommand())
        {
            promote.CommandTimeout = 0;
            // Set-based dedup: a single NOT EXISTS anti-join filters the already-present majority
            // in one operation (content-addressed id is the PK), so we never pay per-row speculative
            // insertion for rows the substrate already holds. The trailing ON CONFLICT DO NOTHING is
            // NOT the dedup mechanism — it is only the thin concurrency tie-breaker for the rare case
            // two parallel commit workers stage the same novel id in overlapping transactions (the
            // anti-join can't see the other's uncommitted insert). ORDER BY id keeps insert locality.
            promote.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) " +
                $"SELECT {cols} FROM {stageName} s " +
                $"WHERE NOT EXISTS (SELECT 1 FROM laplace.{tableName} t WHERE t.id = s.id) " +
                $"ORDER BY id ON CONFLICT DO NOTHING";
            return await promote.ExecuteNonQueryAsync(ct);
        }
    }

    
    
    
    
    

    
    public Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => _staging.AppendAsync(changes, sourceId, ct);

    
    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => _staging.FinalizeAsync(sourceId, ct);
}
