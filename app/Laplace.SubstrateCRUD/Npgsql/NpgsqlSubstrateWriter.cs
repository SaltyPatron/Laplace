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
    public async Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var sw = Stopwatch.StartNew();
        int roundTrips = 0;

        // 1. Existence check on entities (engine-backed SRF).
        var entityIds = new Hash128[change.Entities.Length];
        for (int i = 0; i < change.Entities.Length; i++)
            entityIds[i] = change.Entities[i].Id;
        byte[] existingBitmap = change.Entities.Length == 0
            ? Array.Empty<byte>()
            : await _reader.EntitiesExistBitmapAsync(entityIds, ct);
        if (change.Entities.Length > 0) roundTrips++;

        // 2. Engine compaction: novel = bit clear.
        var novelEntities = new List<EntityRow>(change.Entities.Length);
        for (int i = 0; i < change.Entities.Length; i++)
        {
            byte b = (byte)(i >> 3 < existingBitmap.Length ? existingBitmap[i >> 3] : 0);
            bool present = ((b >> (i & 7)) & 1) != 0;
            if (!present) novelEntities.Add(change.Entities[i]);
        }

        // Trunk-shortcircuit: if every entity is already present AND the intent
        // has no physicality / attestation rows (or those tables won't insert
        // because their FKs point at not-yet-present entities), we can return
        // immediately. Conservative shortcircuit: only when all entities are
        // present AND there are no physicalities/attestations to insert.
        // (Physicalities/attestations may still need insert even if their
        // referenced entities already existed — e.g. new source attesting to
        // a known entity. So we always emit those rows when present.)
        bool trunkShortcircuit = novelEntities.Count == 0
                                && change.Physicalities.Length == 0
                                && change.Attestations.Length == 0;
        if (trunkShortcircuit)
        {
            sw.Stop();
            return new ApplyResult(
                EntitiesAttempted: change.Entities.Length,
                EntitiesInserted: 0,
                PhysicalitiesAttempted: 0,
                PhysicalitiesInserted: 0,
                AttestationsAttempted: 0,
                AttestationsInserted: 0,
                RoundTrips: roundTrips,
                WallClock: sw.Elapsed,
                TrunkShortcircuitHit: true);
        }

        // 2b. Physicality dedup — the same "do you have these ids?" walk as
        // entities, applied to physicalities. COPY can't ON CONFLICT, so we
        // filter to novel ids before COPY: re-ingest and content shared across
        // documents (the same grapheme/word) become no-ops instead of a
        // physicalities_pkey clash.
        var existingPhys = new HashSet<Hash128>();
        if (change.Physicalities.Length > 0)
        {
            var pidBytes = new byte[change.Physicalities.Length][];
            for (int i = 0; i < change.Physicalities.Length; i++)
                pidBytes[i] = change.Physicalities[i].Id.ToBytes();
            await using var ec = await _ds.OpenConnectionAsync(ct);
            await using var eq = ec.CreateCommand();
            eq.CommandText = "SELECT id FROM laplace.physicalities WHERE id = ANY(@ids)";
            eq.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = pidBytes });
            await using var er = await eq.ExecuteReaderAsync(ct);
            while (await er.ReadAsync(ct))
            {
                var bts = (byte[])er[0];
                existingPhys.Add(new Hash128(BitConverter.ToUInt64(bts, 0), BitConverter.ToUInt64(bts, 8)));
            }
            roundTrips++;
        }

        // 2c. Attestation dedup by id (same shape). NOTE: this is the IDENTITY
        // filter — attestation ids are content-addressed BLAKE3 of
        // (subject,kind,object,source,context) so the same observation re-emitted
        // is the same id and must not collide. Glicko-2 matchup updates on
        // RE-OBSERVATION are a separate, later concern (DO UPDATE with double-
        // count guards) — distinct from this "already wrote this exact row" check.
        var existingAtt = new HashSet<Hash128>();
        if (change.Attestations.Length > 0)
        {
            var aidBytes = new byte[change.Attestations.Length][];
            for (int i = 0; i < change.Attestations.Length; i++)
                aidBytes[i] = change.Attestations[i].Id.ToBytes();
            await using var ec = await _ds.OpenConnectionAsync(ct);
            await using var eq = ec.CreateCommand();
            eq.CommandText = "SELECT id FROM laplace.attestations WHERE id = ANY(@ids)";
            eq.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = aidBytes });
            await using var er = await eq.ExecuteReaderAsync(ct);
            while (await er.ReadAsync(ct))
            {
                var bts = (byte[])er[0];
                existingAtt.Add(new Hash128(BitConverter.ToUInt64(bts, 0), BitConverter.ToUInt64(bts, 8)));
            }
            roundTrips++;
        }

        // 3. Materialize COPY BINARY buffers via engine IntentStage.
        using var stage = IntentStage.New(
            Math.Max(novelEntities.Count, change.Physicalities.Length));

        foreach (var e in novelEntities)
        {
            stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
        }
        Span<double> coord = stackalloc double[4];
        foreach (var p in change.Physicalities)
        {
            if (existingPhys.Contains(p.Id)) continue;   // novel only
            coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
            stage.AddPhysicality(
                p.Id, p.EntityId, p.SourceId, (short)p.Kind,
                coord, p.HilbertIndex,
                p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                          : p.TrajectoryXyzm.AsSpan(),
                p.NConstituents,
                p.AlignmentResidual, p.SourceDim,
                p.ObservedAtUnixUs);
        }
        foreach (var a in change.Attestations)
        {
            if (existingAtt.Contains(a.Id)) continue;   // novel only (identity dedup)
            stage.AddAttestation(
                a.Id, a.SubjectId, a.KindId, a.ObjectId, a.SourceId, a.ContextId,
                a.RatingFp1e9, a.RdFp1e9, a.VolatilityFp1e9,
                a.LastObservedAtUnixUs, a.ObservationCount);
        }

        int entitiesInserted = 0;
        int physicalitiesInserted = 0;
        int attestationsInserted = 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            if (stage.EntityCount > 0)
            {
                byte[] buf = stage.EmitCopyBinary(IntentStageTable.Entities);
                entitiesInserted = await StreamCopyAsync(
                    conn, $"COPY laplace.entities ({IntentStage.CopyColumnList(IntentStageTable.Entities)}) FROM STDIN (FORMAT BINARY)",
                    buf, stage.EntityCount, ct);
                roundTrips++;
            }
            if (stage.PhysicalityCount > 0)
            {
                byte[] buf = stage.EmitCopyBinary(IntentStageTable.Physicalities);
                physicalitiesInserted = await StreamCopyAsync(
                    conn, $"COPY laplace.physicalities ({IntentStage.CopyColumnList(IntentStageTable.Physicalities)}) FROM STDIN (FORMAT BINARY)",
                    buf, stage.PhysicalityCount, ct);
                roundTrips++;
            }
            if (stage.AttestationCount > 0)
            {
                byte[] buf = stage.EmitCopyBinary(IntentStageTable.Attestations);
                attestationsInserted = await StreamCopyAsync(
                    conn, $"COPY laplace.attestations ({IntentStage.CopyColumnList(IntentStageTable.Attestations)}) FROM STDIN (FORMAT BINARY)",
                    buf, stage.AttestationCount, ct);
                roundTrips++;
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        sw.Stop();
        return new ApplyResult(
            EntitiesAttempted: change.Entities.Length,
            EntitiesInserted: entitiesInserted,
            PhysicalitiesAttempted: change.Physicalities.Length,
            PhysicalitiesInserted: physicalitiesInserted,
            AttestationsAttempted: change.Attestations.Length,
            AttestationsInserted: attestationsInserted,
            RoundTrips: roundTrips,
            WallClock: sw.Elapsed,
            TrunkShortcircuitHit: false);
    }

    /// <summary>
    /// Stream engine-emitted COPY BINARY bytes directly into the PG wire
    /// via Npgsql's raw COPY stream — zero per-row managed allocation;
    /// the bytes go from C arena → unmanaged write → PG socket.
    /// Returns the row count the engine staged (the actual inserted count
    /// may be lower under ON CONFLICT, but that path is via INSERT — COPY
    /// doesn't support ON CONFLICT, so we use a staging-table strategy
    /// only when re-inserts are likely; for now COPY direct with the
    /// caller-controlled novel-only filter handles dedup).
    /// </summary>
    private static async Task<int> StreamCopyAsync(
        NpgsqlConnection conn, string copyStmt, byte[] data, int rowCount, CancellationToken ct)
    {
        await using var stream = await conn.BeginRawBinaryCopyAsync(copyStmt, ct);
        await stream.WriteAsync(data.AsMemory(), ct);
        await stream.FlushAsync(ct);
        return rowCount;
    }
}
