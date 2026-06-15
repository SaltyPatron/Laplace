using System.Diagnostics;
using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The append-only bulk commit path (the coherent path), isolated from the transactional-upsert
/// path in <see cref="NpgsqlSubstrateWriter"/>.
///
/// The chunked transactional-upsert path holds row locks on hot rows (deadlock) and re-pays
/// COPY+INSERT per chunk (slow), and bounds RAM that does not need bounding on a 96 GB box. This
/// path instead COPIES rows into per-source UNLOGGED staging with NO transaction, NO ON CONFLICT
/// against the live tables, NO preflight / proven-cache — so it is lock-free (parallel across cores)
/// and the live tables are touched exactly once, by ONE set-based merge at <see cref="FinalizeAsync"/>.
/// Measured on this PG: append ~1.3M row/s, merge ~135k row/s and ~flat as the target grows (B-tree
/// inserts are O(log n)).
/// </summary>
internal sealed class SubstrateStagingMerge
{
    private readonly NpgsqlDataSource _ds;

    public SubstrateStagingMerge(NpgsqlDataSource dataSource)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    private static string StagingTag(Hash128 sourceId) =>
        Convert.ToHexString(sourceId.ToBytes()).ToLowerInvariant();

    private static (string E, string P, string A) StagingNames(string tag) =>
        ($"laplace._lap_stg_{tag}_e", $"laplace._lap_stg_{tag}_p", $"laplace._lap_stg_{tag}_a");

    /// <summary>
    /// COPY a batch's rows into this source's UNLOGGED staging (append-only, lock-free).
    /// Rows are NOT visible in the live tables until <see cref="FinalizeAsync"/>.
    /// Safe to call concurrently from many tasks for the same source (COPY appends; the
    /// merge dedups). Re-appending the same intent double-counts attestations, so the
    /// runner appends each intent exactly once (no retry on the append leg).
    /// </summary>
    public async Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sw = Stopwatch.StartNew();
        int eAtt = 0, pAtt = 0, aAtt = 0, roundTrips = 0;

        using var stage = IntentStage.New(1024);
        var prebuilt = new List<IntentStage>();
        Span<double> coord = stackalloc double[4];
        foreach (var c in changes)
        {
            foreach (var e in c.Entities) { stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy); eAtt++; }
            foreach (var p in c.Physicalities)
            {
                coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                stage.AddPhysicality(
                    p.Id, p.EntityId, p.SourceId, (short)p.Type, coord, p.HilbertIndex,
                    p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty : p.TrajectoryXyzm.AsSpan(),
                    p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
                pAtt++;
            }
            foreach (var a in c.Attestations)
            {
                stage.AddAttestation(
                    a.Id, a.SubjectId, a.TypeId, a.ObjectId, a.SourceId, a.ContextId,
                    (short)a.Outcome, a.LastObservedAtUnixUs, a.ObservationCount);
                aAtt++;
            }
            if (!c.IntentStages.IsDefaultOrEmpty)
                foreach (var pre in c.IntentStages)
                    if (!pre.IsInvalid) prebuilt.Add(pre);
        }

        if (eAtt == 0 && pAtt == 0 && aAtt == 0 && prebuilt.Count == 0)
            return new ApplyResult(0, 0, 0, 0, 0, 0, 0, sw.Elapsed, true);

        string tag = StagingTag(sourceId);
        var (eT, pT, aT) = StagingNames(tag);
        await EnsureStagingAsync(tag, ct);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        roundTrips++;

        if (stage.EntityCount > 0)      { await CopyAppendAsync(conn, stage, IntentStageTable.Entities, eT, ct); roundTrips++; }
        if (stage.PhysicalityCount > 0) { await CopyAppendAsync(conn, stage, IntentStageTable.Physicalities, pT, ct); roundTrips++; }
        if (stage.AttestationCount > 0) { await CopyAppendAsync(conn, stage, IntentStageTable.Attestations, aT, ct); roundTrips++; }
        foreach (var pre in prebuilt)
        {
            try
            {
                if (pre.EntityCount > 0)      { await CopyAppendAsync(conn, pre, IntentStageTable.Entities, eT, ct); roundTrips++; }
                if (pre.PhysicalityCount > 0) { await CopyAppendAsync(conn, pre, IntentStageTable.Physicalities, pT, ct); roundTrips++; }
                if (pre.AttestationCount > 0) { await CopyAppendAsync(conn, pre, IntentStageTable.Attestations, aT, ct); roundTrips++; }
            }
            finally { pre.Dispose(); }
        }

        sw.Stop();
        return new ApplyResult(eAtt, eAtt, pAtt, pAtt, aAtt, aAtt, roundTrips, sw.Elapsed, false);
    }

    /// <summary>
    /// Merge this source's UNLOGGED staging into the live tables in ONE set operation per
    /// table — entities/physicalities dedup by id (DISTINCT ON … ON CONFLICT DO NOTHING),
    /// attestations fold by id (GROUP BY, SUM(observation_count)) — then drop the staging.
    /// No-op if the source never appended. Entities merge first so attestation/physicality
    /// FKs resolve against rows merged in the same call.
    /// </summary>
    public async Task<(int Entities, int Physicalities, int Attestations)> FinalizeAsync(
        Hash128 sourceId, CancellationToken ct = default)
    {
        string tag = StagingTag(sourceId);
        var (eT, pT, aT) = StagingNames(tag);
        string ecols = IntentStage.CopyColumnList(IntentStageTable.Entities);
        string pcols = IntentStage.CopyColumnList(IntentStageTable.Physicalities);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        await using (var chk = conn.CreateCommand())
        {
            chk.CommandText = $"SELECT to_regclass('{eT}') IS NOT NULL";
            if (await chk.ExecuteScalarAsync(ct) is not true) return (0, 0, 0);
        }

        int e, p, a;
        await using (var m = conn.CreateCommand())
        {
            m.CommandTimeout = 0;
            m.CommandText =
                $"INSERT INTO laplace.entities ({ecols}) "
              + $"SELECT DISTINCT ON (id) {ecols} FROM {eT} ORDER BY id ON CONFLICT DO NOTHING";
            e = await m.ExecuteNonQueryAsync(ct);
        }
        await using (var m = conn.CreateCommand())
        {
            m.CommandTimeout = 0;
            m.CommandText =
                $"INSERT INTO laplace.physicalities ({pcols}) "
              + $"SELECT DISTINCT ON (id) {pcols} FROM {pT} ORDER BY id ON CONFLICT DO NOTHING";
            p = await m.ExecuteNonQueryAsync(ct);
        }
        await using (var m = conn.CreateCommand())
        {
            m.CommandTimeout = 0;
            m.CommandText =
                "INSERT INTO laplace.attestations "
              + "(id, subject_id, type_id, object_id, source_id, context_id, outcome, last_observed_at, observation_count) "
              + "SELECT id, min(subject_id), min(type_id), min(object_id), min(source_id), min(context_id), "
              + "       min(outcome), max(last_observed_at), sum(observation_count) "
              + $"FROM {aT} GROUP BY id "
              + "ON CONFLICT (id) DO UPDATE SET "
              + "  observation_count = laplace.attestations.observation_count + excluded.observation_count, "
              + "  last_observed_at  = GREATEST(laplace.attestations.last_observed_at, excluded.last_observed_at)";
            a = await m.ExecuteNonQueryAsync(ct);
        }
        await using (var d = conn.CreateCommand())
        {
            d.CommandText = $"DROP TABLE IF EXISTS {eT}, {pT}, {aT}";
            await d.ExecuteNonQueryAsync(ct);
        }
        return (e, p, a);
    }

    // Concurrent "CREATE TABLE IF NOT EXISTS" from parallel appenders races on pg_type
    // (23505) — IF NOT EXISTS is not atomic against concurrent creation. Create each
    // source's staging exactly once: Lazy<Task> runs the DDL a single time and every
    // appender awaits that same completion (on its own short-lived connection; DDL
    // auto-commits, so the tables are visible to the COPY connections afterward).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> _stagingInit = new();

    private Task EnsureStagingAsync(string tag, CancellationToken ct) =>
        _stagingInit.GetOrAdd(tag, t => new Lazy<Task>(() => CreateStagingTablesAsync(t, ct))).Value;

    private async Task CreateStagingTablesAsync(string tag, CancellationToken ct)
    {
        var (eT, pT, aT) = StagingNames(tag);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            $"CREATE UNLOGGED TABLE IF NOT EXISTS {eT} (LIKE laplace.entities INCLUDING DEFAULTS);"
          + $"CREATE UNLOGGED TABLE IF NOT EXISTS {pT} (LIKE laplace.physicalities INCLUDING DEFAULTS);"
          + $"CREATE UNLOGGED TABLE IF NOT EXISTS {aT} (LIKE laplace.attestations INCLUDING DEFAULTS);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CopyAppendAsync(
        NpgsqlConnection conn, IntentStage stage, IntentStageTable table, string targetTable, CancellationToken ct)
    {
        int rowCount = table switch
        {
            IntentStageTable.Entities      => stage.EntityCount,
            IntentStageTable.Physicalities => stage.PhysicalityCount,
            _                              => stage.AttestationCount,
        };
        if (rowCount == 0) return 0;

        (IntPtr ptr, long len) = stage.TupleBuffer(table);
        if (CopyBlobValidator.Enabled)
        {
            int expectedFields = table switch
            {
                IntentStageTable.Entities      => 4,
                IntentStageTable.Physicalities => 11,
                _                              => 9,
            };
            CopyBlobValidator.Validate(ptr, len, expectedFields, targetTable, rowCount);
        }
        string cols = IntentStage.CopyColumnList(table);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY {targetTable} ({cols}) FROM STDIN (FORMAT BINARY)", ct);
        await PgBinaryCopy.WriteNativeBlobAsync(stream, ptr, len, ct);
        return rowCount;
    }
}
