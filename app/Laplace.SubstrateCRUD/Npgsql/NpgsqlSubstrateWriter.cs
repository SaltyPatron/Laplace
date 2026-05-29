using System.Diagnostics;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The one substrate write surface per ADR 0050. Implements
/// <see cref="ISubstrateWriter.ApplyAsync"/> via engine-materialized
/// PG COPY BINARY byte streams; C# is the I/O transport only.
///
/// <para>
/// Hot path per intent:
/// </para>
/// <list type="number">
///   <item>Call <c>laplace.entities_exist_bitmap(entity_ids)</c> SRF to
///         identify which entity rows are novel (Story D.3 #250).</item>
///   <item>P/Invoke <see cref="MerkleDedup.FilterNovel"/> to compact the
///         entity list to novel-only.</item>
///   <item>Materialize PG COPY BINARY byte streams via
///         <see cref="IntentStage"/> (Story A.5 #243), one buffer per
///         table. Entities are pre-filtered to novel-only;
///         physicalities + attestations always emit and rely on
///         <c>ON CONFLICT DO NOTHING</c> for idempotency (RULES R5).</item>
///   <item>Stream each buffer over Npgsql's raw binary COPY.</item>
/// </list>
///
/// <para>
/// Best case (intent fully duplicate at the entity level): 1 round-trip
/// (the existence SRF; no COPYs issued because all 3 buffers are empty).
/// Novel intent: 4 round-trips (1 SRF + 3 COPYs).
/// </para>
/// </summary>
public sealed class NpgsqlSubstrateWriter : ISubstrateWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly NpgsqlSubstrateReader _reader;
    private readonly ILogger<NpgsqlSubstrateWriter> _log;

    public NpgsqlSubstrateWriter(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlSubstrateWriter>? logger = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlSubstrateReader(dataSource);
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
    }

    /// <inheritdoc/>
    public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ApplyManyAsync(new[] { change }, ct);
    }

    /// <inheritdoc/>
    public async Task<ApplyResult> ApplyManyAsync(
        IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sw = Stopwatch.StartNew();
        int roundTrips = 0;

        int entitiesAttempted = 0, physAttempted = 0, attAttempted = 0;
        for (int i = 0; i < changes.Count; i++)
        {
            entitiesAttempted += changes[i].Entities.Length;
            physAttempted     += changes[i].Physicalities.Length;
            attAttempted      += changes[i].Attestations.Length;
        }
        if (changes.Count == 0)
            return new ApplyResult(0, 0, 0, 0, 0, 0, 0, sw.Elapsed, false);

        // One connection for the whole batch — existence reads + all COPYs.
        // Replaces the old per-intent pattern of up to three OpenConnection
        // calls and 6 round-trips PER intent.
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 1. Entity existence — ONE engine-backed SRF for every entity id in
        //    the batch (deduped). Bitmap bit set => already present.
        var uniqueEntityIds = new List<Hash128>(entitiesAttempted);
        var seenEntityArg = new HashSet<Hash128>();
        foreach (var c in changes)
            foreach (var e in c.Entities)
                if (seenEntityArg.Add(e.Id)) uniqueEntityIds.Add(e.Id);

        var existingEntities = new HashSet<Hash128>();
        if (uniqueEntityIds.Count > 0)
        {
            var arg = new byte[uniqueEntityIds.Count][];
            for (int i = 0; i < uniqueEntityIds.Count; i++) arg[i] = uniqueEntityIds[i].ToBytes();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT laplace.entities_exist_bitmap(@ids)";
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = arg });
            var res = await cmd.ExecuteScalarAsync(ct);
            var bitmap = res as byte[] ?? Array.Empty<byte>();
            for (int i = 0; i < uniqueEntityIds.Count; i++)
            {
                byte b = (byte)(i >> 3 < bitmap.Length ? bitmap[i >> 3] : 0);
                if (((b >> (i & 7)) & 1) != 0) existingEntities.Add(uniqueEntityIds[i]);
            }
            roundTrips++;
        }

        // 2. Physicality identity dedup — ONE query for all phys ids. COPY
        //    can't ON CONFLICT, so we filter to novel ids before staging:
        //    content shared across the batch (same grapheme/word/tensor) becomes
        //    a no-op instead of a physicalities_pkey clash.
        var existingPhys = await LoadExistingIdsAsync(
            conn, "physicalities", changes, static c => c.Physicalities, static p => p.Id, ct);
        if (physAttempted > 0) roundTrips++;

        // 3. Attestation identity dedup — ONE query for all attestation ids.
        //    Attestation ids are content-addressed BLAKE3 of
        //    (subject,kind,object,source,context); the same observation
        //    re-emitted is the same id and must not collide. Glicko-2 matchup
        //    updates on re-observation are a separate, later concern.
        var existingAtt = await LoadExistingIdsAsync(
            conn, "attestations", changes, static c => c.Attestations, static a => a.Id, ct);
        if (attAttempted > 0) roundTrips++;

        // 4. Stage ALL novel rows across the batch into ONE COPY stream per
        //    table (FK order: entities, then physicalities, then attestations).
        using var stage = IntentStage.New(Math.Max(uniqueEntityIds.Count, physAttempted));

        var seenEntity = new HashSet<Hash128>(uniqueEntityIds.Count);
        var seenPhys   = new HashSet<Hash128>(existingPhys);
        var seenAtt    = new HashSet<Hash128>(existingAtt);
        Span<double> coord = stackalloc double[4];

        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (existingEntities.Contains(e.Id)) continue;   // already in DB
                if (!seenEntity.Add(e.Id)) continue;             // already staged this batch
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
            }
        foreach (var c in changes)
            foreach (var p in c.Physicalities)
            {
                if (!seenPhys.Add(p.Id)) continue;   // in DB or already staged this batch
                coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                stage.AddPhysicality(
                    p.Id, p.EntityId, p.SourceId, (short)p.Kind,
                    coord, p.HilbertIndex,
                    p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                              : p.TrajectoryXyzm.AsSpan(),
                    p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
            }
        foreach (var c in changes)
            foreach (var a in c.Attestations)
            {
                if (!seenAtt.Add(a.Id)) continue;   // in DB or already staged this batch
                stage.AddAttestation(
                    a.Id, a.SubjectId, a.KindId, a.ObjectId, a.SourceId, a.ContextId,
                    a.RatingFp1e9, a.RdFp1e9, a.VolatilityFp1e9,
                    a.LastObservedAtUnixUs, a.ObservationCount);
            }

        int entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        bool anyRows = stage.EntityCount > 0 || stage.PhysicalityCount > 0 || stage.AttestationCount > 0;

        if (anyRows)
        {
            // Conflict-safe bulk apply: COPY each table's rows into a TEMP
            // staging table, then INSERT … ON CONFLICT DO NOTHING into the real
            // table. COPY alone can't ON CONFLICT, so direct COPY of a novel-only
            // filter is only race-safe single-threaded; the staging + ON CONFLICT
            // path is correct under concurrent writers of overlapping ids
            // (RULES R5), which is what unlocks ParallelWorkers > 1. The INSERT's
            // rows-affected is the TRUE inserted count (≤ staged, under conflict).
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                if (stage.EntityCount > 0)
                {
                    entitiesInserted = await StageAndInsertAsync(
                        conn, IntentStageTable.Entities, "entities",
                        stage.EmitCopyBinary(IntentStageTable.Entities), ct);
                    roundTrips += 3;
                }
                if (stage.PhysicalityCount > 0)
                {
                    physicalitiesInserted = await StageAndInsertAsync(
                        conn, IntentStageTable.Physicalities, "physicalities",
                        stage.EmitCopyBinary(IntentStageTable.Physicalities), ct);
                    roundTrips += 3;
                }
                if (stage.AttestationCount > 0)
                {
                    attestationsInserted = await StageAndInsertAsync(
                        conn, IntentStageTable.Attestations, "attestations",
                        stage.EmitCopyBinary(IntentStageTable.Attestations), ct);
                    roundTrips += 3;
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        sw.Stop();
        return new ApplyResult(
            EntitiesAttempted: entitiesAttempted,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesAttempted: physAttempted,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsAttempted: attAttempted,
            AttestationsInserted: attestationsInserted,
            RoundTrips: roundTrips,
            WallClock: sw.Elapsed,
            // Trunk-shortcircuit: nothing was written — either the intent(s)
            // were empty, or every presented row deduped away (already present
            // / duplicated within the batch). Matches the legacy single-intent
            // contract: a no-op apply reports the shortcircuit.
            TrunkShortcircuitHit: !anyRows);
    }

    /// <summary>
    /// One <c>SELECT id FROM laplace.&lt;table&gt; WHERE id = ANY(@ids)</c> over
    /// every (deduped) id of the given row kind across the whole batch — the
    /// COPY-can't-ON-CONFLICT identity filter, hoisted from per-intent to
    /// per-batch.
    /// </summary>
    private static async Task<HashSet<Hash128>> LoadExistingIdsAsync<TRow>(
        NpgsqlConnection conn,
        string table,
        IReadOnlyList<SubstrateChange> changes,
        Func<SubstrateChange, System.Collections.Immutable.ImmutableArray<TRow>> select,
        Func<TRow, Hash128> idOf,
        CancellationToken ct)
    {
        var seen = new HashSet<Hash128>();
        var idBytes = new List<byte[]>();
        foreach (var c in changes)
            foreach (var row in select(c))
            {
                var id = idOf(row);
                if (seen.Add(id)) idBytes.Add(id.ToBytes());
            }

        var existing = new HashSet<Hash128>();
        if (idBytes.Count == 0) return existing;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM laplace.{table} WHERE id = ANY(@ids)";
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = idBytes.ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var bts = (byte[])r[0];
            existing.Add(new Hash128(BitConverter.ToUInt64(bts, 0), BitConverter.ToUInt64(bts, 8)));
        }
        return existing;
    }

    /// <summary>
    /// Conflict-safe bulk insert for one table: (1) create an <c>ON COMMIT
    /// DROP</c> TEMP table holding exactly the COPY columns, (2) stream the
    /// engine-emitted COPY BINARY bytes into it via Npgsql's raw COPY stream
    /// (zero per-row managed allocation — C arena → unmanaged write → PG
    /// socket), (3) <c>INSERT … SELECT … ON CONFLICT DO NOTHING</c> into the
    /// real table. Returns the INSERT's rows-affected — the TRUE inserted count
    /// (lower than staged when ids already exist or recur). Must run inside an
    /// open transaction (for the temp table's <c>ON COMMIT DROP</c> scope).
    /// </summary>
    private static async Task<int> StageAndInsertAsync(
        NpgsqlConnection conn, IntentStageTable table, string tableName, byte[] data, CancellationToken ct)
    {
        string cols = IntentStage.CopyColumnList(table);
        string tmp  = $"_lpl_stg_{tableName}";

        // 1. TEMP staging table: exactly the COPY columns + their types, no
        //    constraints/defaults (so COPY of the column subset never trips a
        //    NOT NULL on a server-defaulted column).
        await using (var create = conn.CreateCommand())
        {
            create.CommandText =
                $"CREATE TEMP TABLE {tmp} ON COMMIT DROP AS "
              + $"SELECT {cols} FROM laplace.{tableName} WITH NO DATA";
            await create.ExecuteNonQueryAsync(ct);
        }

        // 2. Raw binary COPY into the staging table.
        await using (var stream = await conn.BeginRawBinaryCopyAsync(
            $"COPY {tmp} ({cols}) FROM STDIN (FORMAT BINARY)", ct))
        {
            await stream.WriteAsync(data.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }

        // 3. Conflict-safe promote into the real table; rows-affected = inserted.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) "
              + $"SELECT {cols} FROM {tmp} ON CONFLICT DO NOTHING";
            return await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
