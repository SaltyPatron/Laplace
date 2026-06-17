using System.Diagnostics;
using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;













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
