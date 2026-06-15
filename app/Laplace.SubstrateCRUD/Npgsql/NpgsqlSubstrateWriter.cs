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
        int cacheMax = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_PROVEN_CACHE_MAX"), out var m) && m > 0
            ? m : 32_000_000;
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

        var entToCheck = new List<Hash128>(uniqueEntityIds.Count);
        foreach (var id in uniqueEntityIds)
            if (!_provenEntities.Contains(id)) entToCheck.Add(id);

        var physToCheck = IntentPreflight.CollectUnprovenIds(changes, static c => c.Physicalities, static p => p.Id, _provenPhys);
        var attToCheck = _bulkFreshSource
            ? new List<Hash128>()
            : IntentPreflight.CollectUnprovenIds(changes, static c => c.Attestations, static a => a.Id, _provenAtt);

        var (existingEntities, existingPhys, existingAtt) = await IntentPreflight.RunAsync(
            conn, entToCheck, physToCheck, attToCheck, ct);
        if (entToCheck.Count > 0 || physToCheck.Count > 0 || attToCheck.Count > 0) roundTrips++;
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

        // Referrer provenance: the proof's failure message names WHO referenced the
        // missing id (role + unit), turning "decomposer bug somewhere" into a file:line.
        var referenced = new Dictionary<Hash128, string>();
        void Reference(Hash128 id, string role, string unit)
        {
            if (seenEntityArg.Contains(id) || _provenEntities.Contains(id) || seenEntity.Contains(id)) return;
            if (!referenced.ContainsKey(id)) referenced.Add(id, $"{role} in {unit}");
        }

        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (existingEntities.Contains(e.Id)) continue;
                if (_provenEntities.Contains(e.Id)) continue;
                if (!seenEntity.Add(e.Id)) continue;
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
                Reference(e.TypeId, $"entity {Convert.ToHexString(e.Id.ToBytes())} type_id", c.Metadata.SourceContentUnitName);
                if (e.FirstObservedBy is Hash128 fob) Reference(fob, $"entity {Convert.ToHexString(e.Id.ToBytes())} first_observed_by", c.Metadata.SourceContentUnitName);
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
                Reference(p.EntityId, $"physicality {Convert.ToHexString(p.Id.ToBytes())} entity_id", c.Metadata.SourceContentUnitName);
                Reference(p.SourceId, $"physicality {Convert.ToHexString(p.Id.ToBytes())} source_id", c.Metadata.SourceContentUnitName);
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
                var attHex = Convert.ToHexString(a.Id.ToBytes());
                Reference(a.SubjectId, $"attestation {attHex} subject_id", c.Metadata.SourceContentUnitName);
                Reference(a.TypeId, $"attestation {attHex} type_id", c.Metadata.SourceContentUnitName);
                Reference(a.SourceId, $"attestation {attHex} source_id", c.Metadata.SourceContentUnitName);
                if (a.ObjectId  is Hash128 aObj) Reference(aObj, $"attestation {attHex} object_id", c.Metadata.SourceContentUnitName);
                if (a.ContextId is Hash128 aCtx) Reference(aCtx, $"attestation {attHex} context_id", c.Metadata.SourceContentUnitName);
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

                foreach (var pre in prebuiltStages)
                {
                    try
                    {
                        entitiesInserted += await StageAndInsertAsync(
                            conn, pre, IntentStageTable.Entities, "entities", ct);
                        physicalitiesInserted += await StageAndInsertAsync(
                            conn, pre, IntentStageTable.Physicalities, "physicalities", ct);
                        roundTrips += 2;
                    }
                    finally
                    {
                        pre.Dispose();
                    }
                }

                if (stage.EntityCount > 0)
                {
                    entitiesInserted += await StageAndInsertAsync(
                        conn, stage, IntentStageTable.Entities, "entities", ct);
                    roundTrips += 3;
                }

                if (referenced.Count > 0)
                {
                    var refList = new List<Hash128>(referenced.Keys);
                    var present = await IntentPreflight.EntitiesExistAsync(conn, refList, ct);
                    roundTrips++;
                    if (present.Count != refList.Count)
                    {
                        Hash128 firstMissing = default;
                        int missingCount = 0;
                        foreach (var id in refList)
                            if (!present.Contains(id))
                            {
                                if (missingCount == 0) firstMissing = id;
                                missingCount++;
                            }
                        throw new SubstrateReferentialIntegrityException(
                            missingCount, Convert.ToHexString(firstMissing.ToBytes()),
                            referenced.GetValueOrDefault(firstMissing));
                    }
                    _provenEntities.AddRange(present);
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

        // The round-trip budget law: RT must amortize over rows. The per-witness
        // prebuilt-stage pattern once cost 16,681 RT for 51k rows — this guard makes
        // that class of regression a loud failure instead of log archaeology.
        // LAPLACE_RT_BUDGET_PER_10K (default 64; 0 disables) is the allowed RT per
        // 10k attempted rows on top of a fixed per-intent allowance; set
        // LAPLACE_RT_BUDGET_ENFORCE=1 (tests) to throw instead of warn.
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
    {
        (IntPtr ptr, long len) = stage.TupleBuffer(table);
        string cols = IntentStage.CopyColumnList(table);
        int rowCount = table switch
        {
            IntentStageTable.Entities      => stage.EntityCount,
            IntentStageTable.Physicalities => stage.PhysicalityCount,
            _                              => stage.AttestationCount,
        };

        if (rowCount == 0) return 0;

        if (CopyBlobValidator.Enabled)
        {
            int expectedFields = table switch
            {
                IntentStageTable.Entities      => 4,
                IntentStageTable.Physicalities => 11,
                _                              => 9,
            };
            CopyBlobValidator.Validate(ptr, len, expectedFields, tableName, rowCount);
        }

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

        await using (var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY {stageName} ({cols}) FROM STDIN (FORMAT BINARY)", ct))
            await PgBinaryCopy.WriteNativeBlobAsync(stream, ptr, len, ct);

        await using (var promote = conn.CreateCommand())
        {
            promote.CommandTimeout = 0;
            promote.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) " +
                $"SELECT {cols} FROM {stageName} ORDER BY id ON CONFLICT DO NOTHING";
            return await promote.ExecuteNonQueryAsync(ct);
        }
    }

    // ======================================================================
    // Append-only bulk commit (the coherent path) — see SubstrateStagingMerge.
    // These public methods are thin delegators so ISubstrateWriter is unchanged;
    // the lock-free fresh-source strategy lives in SubstrateStagingMerge.
    // ======================================================================

    /// <inheritdoc cref="SubstrateStagingMerge.AppendAsync"/>
    public Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => _staging.AppendAsync(changes, sourceId, ct);

    /// <inheritdoc cref="SubstrateStagingMerge.FinalizeAsync"/>
    public Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => _staging.FinalizeAsync(sourceId, ct);
}
