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

        // Physicalities are NOT preflighted up front. Probing every id via the per-id
        // pg_laplace_intent_preflight did not scale to per-batch re-emission volume and crashed the
        // backend ("connection forcibly closed"); and the ProvenId caches it fed grew UNBOUNDED across
        // the run (the same boil-the-ocean as the just-deleted content bank). Physicalities instead
        // dedup per-batch (the staged HashSets below) + the client ProvenId cache + the set-based
        // NOT EXISTS anti-join at promote time (StageAndInsertManyAsync) — no per-row ON CONFLICT.
        // Entities ARE preflighted set-based via the scalable chunked entities_exist_bitmap (below),
        // so already-present entities are never re-staged. Attestations still preflight (unless this is
        // a fresh bulk source) because an already-present attestation must route to the locked
        // observation_count UPDATE below to sum re-observations rather than being dropped at insert.
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
                    // Bulk-fresh loads don't carry re-observation semantics: every attestation is
                    // a first-time write to an empty (or near-empty) DB. Routing already-seen IDs
                    // to attGamesDelta issues a FOR UPDATE lock query against all 4 commit workers
                    // simultaneously → each waits for the others indefinitely (no SKIP LOCKED).
                    // Silently drop the duplicate; the first staging attempt covers the row.
                    if (!_bulkFreshSource)
                    {
                        var d = attGamesDelta.TryGetValue(a.Id, out var cur) ? cur : (0L, 0L);
                        attGamesDelta[a.Id] = (checked(d.Item1 + a.ObservationCount),
                                               Math.Max(d.Item2, a.LastObservedAtUnixUs));
                    }
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
                    // No try/finally here: prebuiltStages must remain alive across retries.
                    // A 23505 transient race disposes these on the first attempt, making the
                    // retry crash with ObjectDisposedException. Dispose only after CommitAsync.
                    entitiesInserted += await StageAndInsertManyAsync(
                        conn, prebuiltStages, IntentStageTable.Entities, "entities", ct);
                    physicalitiesInserted += await StageAndInsertManyAsync(
                        conn, prebuiltStages, IntentStageTable.Physicalities, "physicalities", ct);
                    roundTrips += 2;
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
                        "  WHERE a.id IN (SELECT id FROM d) ORDER BY a.id FOR UPDATE SKIP LOCKED" +
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

                foreach (var pre in prebuiltStages)
                    pre.Dispose();

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
            // Set-based dedup is the ONLY dedup mechanism: a NOT EXISTS anti-join filters the
            // already-present majority in one operation, so the DB only ever receives genuinely-novel
            // rows. There is deliberately NO trailing `ON CONFLICT DO NOTHING`: that was a per-row
            // speculative-insert crutch that masked, rather than eliminated, duplicate work — and for
            // attestations it silently DROPPED a concurrent batch's re-observation count (a present id
            // hit DO NOTHING instead of routing to the locked observation_count UPDATE).
            //
            // CRITICAL: the anti-join + DISTINCT must honor EVERY unique constraint on the target, not
            // just the `id` PK. The schema (extension/laplace_substrate/sql) declares:
            //   * entities       — PK (id) only                       (02_entities.sql.in:2)
            //   * physicalities  — PK (id) + UNIQUE (entity_id, source_id, type)
            //                                                          (03_physicalities.sql.in:2,16)
            //   * attestations   — PK (id) only                       (04_attestations.sql.in:2)
            // The id is content-addressed from (entity_id, source_id, type, coord, trajectory, …), so a
            // physicality with a DIFFERENT id can still collide on the SECONDARY natural key
            // (entity_id, source_id, type) — a deterministic duplicate (e.g. the same entity given two
            // coords by one source) the bare ON CONFLICT used to absorb. The retry net does NOT help
            // there (it is not a race), so we must filter it set-based: dedup the stage on the natural
            // key (deterministic ORDER BY → keep the lowest-id winner, drop the rest) AND anti-join the
            // natural key against the live table. This reproduces the old ON CONFLICT DO NOTHING
            // semantics (one content physicality per entity+source+type) while staying purely set-based.
            //
            // The remaining cross-worker race — two parallel commit workers staging the same novel key
            // in overlapping transactions, where neither anti-join sees the other's uncommitted insert —
            // still surfaces as a unique_violation (SQLSTATE 23505) on whichever constraint, which
            // IngestRunner's ConcurrencyRetry classifies as transient and retries; on retry the
            // now-committed row is visible to the anti-join so it is correctly skipped/summed with zero
            // duplicates and zero lost counts. Single-worker runs (the default) never race, so the
            // anti-join alone is exact. DISTINCT ON + ORDER BY also keep insert locality.
            //
            // Per-table natural key: entities/attestations key on id; physicalities additionally on
            // (entity_id, source_id, type). This mirrors SubstrateStagingMerge.FinalizeAsync's merge
            // (which kept a bare ON CONFLICT DO NOTHING and so was never exposed to this hazard).
            // Entities are pure content-addresses: same id = same bytes, always idempotent.
            // ON CONFLICT DO NOTHING is safe here and eliminates the 23505 cross-worker race
            // that exhausts the transient-retry budget when many commit workers share the entity
            // key space (OMW, ConceptNet, etc.). The concern that removed ON CONFLICT globally
            // was attestation observation_count semantics — not applicable to entities.
            (string distinctOn, string orderBy, string naturalKeyFilter, string conflictClause) = table switch
            {
                IntentStageTable.Physicalities => (
                    "entity_id, source_id, type",
                    "entity_id, source_id, type, id",
                    $" AND NOT EXISTS (SELECT 1 FROM laplace.{tableName} n " +
                    "WHERE n.entity_id = s.entity_id AND n.source_id = s.source_id AND n.type = s.type)",
                    string.Empty),
                IntentStageTable.Entities => ("id", "id", string.Empty, " ON CONFLICT (id) DO NOTHING"),
                _ => ("id", "id", string.Empty, string.Empty),
            };
            promote.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) " +
                $"SELECT DISTINCT ON ({distinctOn}) {cols} FROM {stageName} s " +
                $"WHERE NOT EXISTS (SELECT 1 FROM laplace.{tableName} t WHERE t.id = s.id)" +
                naturalKeyFilter + " " +
                $"ORDER BY {orderBy}" +
                conflictClause;
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
