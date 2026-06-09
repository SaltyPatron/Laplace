using System.Diagnostics;
using System.Text;
using global::Npgsql;
using NpgsqlTypes;
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

    private sealed class ProvenIdCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _set;
        private readonly int _cap;
        public ProvenIdCache(bool enabled, int cap)
        {
            _set = enabled ? new() : null;
            _cap = cap;
        }
        public bool Contains(Hash128 id) => _set is { } s && s.ContainsKey(id);
        public void Add(Hash128 id)
        {
            if (_set is { } s && s.Count < _cap) s.TryAdd(id, 0);
        }
        public void AddRange(IEnumerable<Hash128> ids)
        {
            if (_set is null) return;
            foreach (var id in ids) Add(id);
        }
    }

    public NpgsqlSubstrateWriter(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlSubstrateWriter>? logger = null,
        bool bulkFreshSource = false)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlSubstrateReader(dataSource);
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
        _bulkFreshSource = bulkFreshSource;
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

        var physToCheck = CollectUnprovenIds(changes, static c => c.Physicalities, static p => p.Id, _provenPhys);
        var attToCheck = _bulkFreshSource
            ? new List<Hash128>()
            : CollectUnprovenIds(changes, static c => c.Attestations, static a => a.Id, _provenAtt);

        var (existingEntities, existingPhys, existingAtt) = await IntentPreflightAsync(
            conn, entToCheck, physToCheck, attToCheck, ct);
        if (entToCheck.Count > 0 || physToCheck.Count > 0 || attToCheck.Count > 0) roundTrips++;
        _provenEntities.AddRange(existingEntities);
        _provenPhys.AddRange(existingPhys);
        _provenAtt.AddRange(existingAtt);

        using var stage = IntentStage.New(Math.Max(Math.Max(uniqueEntityIds.Count, physAttempted), attAttempted));

        var seenEntity = new HashSet<Hash128>(uniqueEntityIds.Count);
        var seenPhys   = new HashSet<Hash128>(existingPhys);
        var seenAtt    = new HashSet<Hash128>(existingAtt);
        var stagedPhysIds = new List<Hash128>();
        var stagedAttIds  = new List<Hash128>();
        Span<double> coord = stackalloc double[4];

        var referenced = new HashSet<Hash128>();
        void Reference(Hash128 id)
        {
            if (!seenEntityArg.Contains(id) && !_provenEntities.Contains(id)) referenced.Add(id);
        }

        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (existingEntities.Contains(e.Id)) continue;
                if (_provenEntities.Contains(e.Id)) continue;
                if (!seenEntity.Add(e.Id)) continue;
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
                Reference(e.TypeId);
                if (e.FirstObservedBy is Hash128 fob) Reference(fob);
            }
        CheckpointEntities(stage, "after-entity-loop");
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
                Reference(p.EntityId);
                Reference(p.SourceId);
            }
        CheckpointEntities(stage, "after-physicality-loop");
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
                Reference(a.SubjectId);
                Reference(a.TypeId);
                Reference(a.SourceId);
                if (a.ObjectId  is Hash128 aObj) Reference(aObj);
                if (a.ContextId is Hash128 aCtx) Reference(aCtx);
            }
        CheckpointEntities(stage, "after-attestation-loop");

        if (referenced.Count > 0)
        {
            var refList = new List<Hash128>(referenced);
            var present = await EntitiesExistAsync(conn, refList, ct);
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
                    missingCount, Convert.ToHexString(firstMissing.ToBytes()));
            }
            _provenEntities.AddRange(present);
        }

        int entitiesInserted = 0, physicalitiesInserted = 0, attestationsInserted = 0;
        bool anyRows = stage.EntityCount > 0 || stage.PhysicalityCount > 0 || stage.AttestationCount > 0
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

                if (stage.EntityCount > 0)
                {
                    entitiesInserted = await StageAndInsertAsync(
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
            TrunkShortcircuitHit: !anyRows);
    }

    private static async Task<(HashSet<Hash128> Entities, HashSet<Hash128> Phys, HashSet<Hash128> Att)>
        IntentPreflightAsync(
            NpgsqlConnection conn,
            IReadOnlyList<Hash128> entityIds,
            IReadOnlyList<Hash128> physIds,
            IReadOnlyList<Hash128> attIds,
            CancellationToken ct)
    {
        var entExisting = new HashSet<Hash128>();
        var physExisting = new HashSet<Hash128>();
        var attExisting = new HashSet<Hash128>();
        if (entityIds.Count == 0 && physIds.Count == 0 && attIds.Count == 0)
            return (entExisting, physExisting, attExisting);

        const int ChunkSize = 250_000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText =
            "SELECT (p).entity_exists, (p).phys_exists, (p).att_exists " +
            "FROM laplace.intent_preflight(@ent, @phys, @att) p";
        var entParam = cmd.Parameters.Add(
            new NpgsqlParameter("ent", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        var physParam = cmd.Parameters.Add(
            new NpgsqlParameter("phys", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        var attParam = cmd.Parameters.Add(
            new NpgsqlParameter("att", NpgsqlDbType.Array | NpgsqlDbType.Bytea));

        int max = Math.Max(entityIds.Count, Math.Max(physIds.Count, attIds.Count));
        for (int off = 0; off < max; off += ChunkSize)
        {
            int entLen = Math.Min(ChunkSize, Math.Max(0, entityIds.Count - off));
            int physLen = Math.Min(ChunkSize, Math.Max(0, physIds.Count - off));
            int attLen = Math.Min(ChunkSize, Math.Max(0, attIds.Count - off));

            entParam.Value = ToByteaArray(entityIds, off, entLen);
            physParam.Value = ToByteaArray(physIds, off, physLen);
            attParam.Value = ToByteaArray(attIds, off, attLen);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;
            DecodeBitmap(entExisting, entityIds, off, entLen, r.GetFieldValue<byte[]>(0));
            DecodeBitmap(physExisting, physIds, off, physLen, r.GetFieldValue<byte[]>(1));
            DecodeBitmap(attExisting, attIds, off, attLen, r.GetFieldValue<byte[]>(2));
        }
        return (entExisting, physExisting, attExisting);
    }

    private static byte[][] ToByteaArray(IReadOnlyList<Hash128> ids, int off, int len)
    {
        var arg = new byte[len][];
        for (int i = 0; i < len; i++) arg[i] = ids[off + i].ToBytes();
        return arg;
    }

    private static void DecodeBitmap(
        HashSet<Hash128> existing, IReadOnlyList<Hash128> ids, int off, int len, byte[] bitmap)
    {
        for (int i = 0; i < len; i++)
        {
            byte b = (byte)(i >> 3 < bitmap.Length ? bitmap[i >> 3] : 0);
            if (((b >> (i & 7)) & 1) != 0) existing.Add(ids[off + i]);
        }
    }

    private static async Task<HashSet<Hash128>> EntitiesExistAsync(
        NpgsqlConnection conn, IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var existing = new HashSet<Hash128>();
        const int ChunkSize = 250_000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT laplace.entities_exist_bitmap(@ids)";
        var idsParam = cmd.Parameters.Add(
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        for (int off = 0; off < ids.Count; off += ChunkSize)
        {
            int len = Math.Min(ChunkSize, ids.Count - off);
            var arg = new byte[len][];
            for (int i = 0; i < len; i++) arg[i] = ids[off + i].ToBytes();
            idsParam.Value = arg;
            var res = await cmd.ExecuteScalarAsync(ct);
            var bitmap = res as byte[] ?? Array.Empty<byte>();
            for (int i = 0; i < len; i++)
            {
                byte b = (byte)(i >> 3 < bitmap.Length ? bitmap[i >> 3] : 0);
                if (((b >> (i & 7)) & 1) != 0) existing.Add(ids[off + i]);
            }
        }
        return existing;
    }

    private static List<Hash128> CollectUnprovenIds<TRow>(
        IReadOnlyList<SubstrateChange> changes,
        Func<SubstrateChange, System.Collections.Immutable.ImmutableArray<TRow>> select,
        Func<TRow, Hash128> idOf,
        ProvenIdCache proven)
    {
        var seen = new HashSet<Hash128>();
        var ids = new List<Hash128>();
        foreach (var c in changes)
            foreach (var row in select(c))
            {
                var id = idOf(row);
                if (!proven.Contains(id) && seen.Add(id)) ids.Add(id);
            }
        return ids;
    }

    private static async Task<HashSet<Hash128>> LoadExistingIdsAsync(
        NpgsqlConnection conn,
        string table,
        IReadOnlyList<Hash128> ids,
        CancellationToken ct)
    {
        var existing = new HashSet<Hash128>();
        if (ids.Count == 0) return existing;
        var idBytes = new List<byte[]>(ids.Count);
        foreach (var id in ids) idBytes.Add(id.ToBytes());

        const int ChunkSize = 250_000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"SELECT id FROM laplace.{table} WHERE id = ANY(@ids)";
        var idsParam = cmd.Parameters.Add(
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea));
        for (int off = 0; off < idBytes.Count; off += ChunkSize)
        {
            int len = Math.Min(ChunkSize, idBytes.Count - off);
            var chunk = new byte[len][];
            idBytes.CopyTo(off, chunk, 0, len);
            idsParam.Value = chunk;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var bts = (byte[])r[0];
                existing.Add(new Hash128(BitConverter.ToUInt64(bts, 0), BitConverter.ToUInt64(bts, 8)));
            }
        }
        return existing;
    }

    private static readonly byte[] CopyBinaryHeader =
        { 0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00,
          0, 0, 0, 0,  0, 0, 0, 0 };
    private static readonly byte[] CopyBinaryTrailer = { 0xFF, 0xFF };
    private const long CopyChunkBytes = 1L << 23;

    private static readonly bool CopyValidate =
        Environment.GetEnvironmentVariable("LAPLACE_COPY_VALIDATE") == "1";

    // Diagnostic: re-validate the entities buffer at a named staging phase. Because the
    // entities buffer provably never reallocs and is only written by complete, checked
    // add_entity rows, any corruption observed here must come from an EXTERNAL heap write
    // (e.g. a buffer overrun by a later staging phase). Checkpointing after each phase
    // names the exact phase that introduces the corruption.
    private static void CheckpointEntities(IntentStage stage, string phase)
    {
        if (!CopyValidate) return;
        int n = stage.EntityCount;
        if (n == 0) return;
        (IntPtr ptr, long len) = stage.TupleBuffer(IntentStageTable.Entities);
        try
        {
            ValidateCopyBlob(ptr, len, 4, "entities", n);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ENTITIES buffer corruption FIRST observed at phase '{phase}' " +
                $"(entityCount={n}, entities.len={len}, expected={(long)n} rows). " +
                $"This localizes the heap corruptor to whatever ran BEFORE this checkpoint. {ex.Message}", ex);
        }
    }

    // Walks the native PG-binary-COPY blob row-by-row and verifies each row begins with
    // the expected int16 field count and that field length prefixes stay within bounds.
    // On the FIRST framing violation it throws with the exact byte offset, the row index,
    // the expected vs observed field count, and a hex window so we can see what bytes
    // corrupted the stream (recognizable ASCII / hash / double patterns reveal the source).
    private static void ValidateCopyBlob(IntPtr ptr, long len, int expectedFields, string tableName, int rowCount)
    {
        if (ptr == IntPtr.Zero || len <= 0) return;
        var blob = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(ptr, blob, 0, checked((int)len));

        // Entities are STRICTLY fixed-size: int16(4) + [int32(16)+16 id] + [int32(2)+2 tier]
        // + [int32(16)+16 type_id] + [int32(16)+16 fob | int32(-1) NULL fob].
        // => stride is 68 bytes (fob present) or 52 bytes (fob NULL). Because the stream
        // is fixed-size, the FIRST row whose start is not reachable by a valid stride from
        // the previous row start is the true point of corruption. We detect that precisely
        // and, on the row-by-row walk below, also confirm field counts/lengths.
        long off = 0;
        int row = 0;
        while (off < len)
        {
            long rowStart = off;
            if (off + 2 > len)
                FailCopyBlob(blob, rowStart, row, tableName, expectedFields, -1,
                    "truncated field-count (need 2 bytes)");
            int fields = (blob[off] << 8) | blob[off + 1];
            off += 2;
            if (fields != expectedFields)
                FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                    $"unexpected field count (got {fields})");
            for (int f = 0; f < fields; f++)
            {
                if (off + 4 > len)
                    FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                        $"truncated length prefix at field {f}");
                int flen = (blob[off] << 24) | (blob[off + 1] << 16) | (blob[off + 2] << 8) | blob[off + 3];
                off += 4;
                if (flen == -1) continue;          // NULL field
                if (flen < 0 || off + flen > len)
                    FailCopyBlob(blob, rowStart, row, tableName, expectedFields, fields,
                        $"field {f} length {flen} overruns blob (off={off}, len={len})");
                off += flen;
            }
            row++;
        }
        if (row != rowCount)
            throw new InvalidOperationException(
                $"COPY blob validation: {tableName} parsed {row} rows but stage reports {rowCount}.");
    }

    private static void FailCopyBlob(
        byte[] blob, long rowStart, int row, string tableName, int expected, int got, string why)
    {
        long winStart = Math.Max(0, rowStart - 160);
        long winEnd = Math.Min(blob.LongLength, rowStart + 160);
        var sb = new StringBuilder();
        for (long i = winStart; i < winEnd; i++)
        {
            if (i == rowStart) sb.Append("[>");
            sb.Append(blob[i].ToString("X2"));
            if (i == rowStart) sb.Append("<]");
            sb.Append(' ');
        }
        var ascii = new StringBuilder();
        for (long i = winStart; i < winEnd; i++)
        {
            byte c = blob[i];
            ascii.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
        }

        // Walk forward from the blob start to find the FIRST row that fails to land on a
        // valid fixed-stride boundary (entities only). This pinpoints the originating
        // corrupt row rather than the row where the desync surfaced.
        var strideReport = new StringBuilder();
        if (tableName == "entities")
        {
            strideReport.Append('\n');
            strideReport.Append(DescribeEntityStride(blob, rowStart));
        }

        throw new InvalidOperationException(
            $"COPY blob CORRUPT in '{tableName}': {why}; row #{row}, rowStart byte offset {rowStart}, " +
            $"expected {expected} fields. Hex window (rowStart marked [>..<]):\n{sb}\nASCII: {ascii}{strideReport}");
    }

    // Re-walks the entities blob assuming the strict fixed layout and reports the first row
    // whose framing deviates, plus a per-row stride trace around the failure offset.
    private static string DescribeEntityStride(byte[] blob, long failOffset)
    {
        var sb = new StringBuilder();
        long off = 0;
        int row = 0;
        long len = blob.LongLength;
        long lastGoodStart = 0;
        while (off + 2 <= len)
        {
            long rowStart = off;
            int fields = (blob[off] << 8) | blob[off + 1];
            if (fields != 4)
            {
                sb.Append($"first off-layout entity row #{row} at offset {rowStart} (field-count={fields}, expected 4); ");
                sb.Append($"previous good row started at {lastGoodStart} (stride {rowStart - lastGoodStart}). ");
                long ws = Math.Max(0, lastGoodStart - 8);
                long we = Math.Min(len, rowStart + 16);
                sb.Append("bytes around prev→bad: ");
                for (long i = ws; i < we; i++)
                {
                    if (i == lastGoodStart) sb.Append("{prev>");
                    if (i == rowStart) sb.Append("{bad>");
                    sb.Append(blob[i].ToString("X2"));
                    sb.Append(' ');
                }
                return sb.ToString();
            }
            // STRICT fixed-layout check: every field length must be exactly as specified.
            // We do NOT follow a wrong length (which would mask the true origin); instead we
            // verify each prefix and stop at the FIRST deviation. Layout:
            //   [int16=4][int32=16 + 16 id][int32=2 + 2 tier][int32=16 + 16 type_id]
            //   [int32=16 + 16 fob | int32=-1 NULL]
            int lId   = ReadLen(blob, rowStart + 2);
            int lTier = ReadLen(blob, rowStart + 2 + 4 + 16);
            int lType = ReadLen(blob, rowStart + 2 + 4 + 16 + 4 + 2);
            int lFob  = ReadLen(blob, rowStart + 2 + 4 + 16 + 4 + 2 + 4 + 16);
            bool ok = lId == 16 && lTier == 2 && lType == 16 && (lFob == 16 || lFob == -1);
            if (!ok)
            {
                sb.Append($"first BAD-LENGTH entity row #{row} at offset {rowStart}: ");
                sb.Append($"id_len={lId}(want 16) tier_len={lTier}(want 2) type_len={lType}(want 16) fob_len={lFob}(want 16 or -1). ");
                sb.Append($"prev good row at {lastGoodStart} (stride {rowStart - lastGoodStart}). ");
                long ws = Math.Max(0, rowStart - 8);
                long we = Math.Min(len, rowStart + 80);
                sb.Append("bytes: ");
                for (long i = ws; i < we; i++)
                {
                    if (i == rowStart) sb.Append("{row>");
                    sb.Append(blob[i].ToString("X2"));
                    sb.Append(' ');
                }
                return sb.ToString();
            }
            long stride = lFob == -1 ? 52 : 68;
            lastGoodStart = rowStart;
            off = rowStart + stride;
            row++;
        }
        sb.Append($"entities STRICT re-walk completed {row} rows up to offset {off} with NO layout break (failOffset={failOffset}); ");
        sb.Append($"this means the byte count is consistent but a field-count read 0 at failOffset — investigate buffer len vs row_count. ");
        return sb.ToString();
    }

    private static int ReadLen(byte[] blob, long at)
    {
        if (at < 0 || at + 4 > blob.LongLength) return -100;
        return (blob[at] << 24) | (blob[at + 1] << 16) | (blob[at + 2] << 8) | blob[at + 3];
    }

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

        if (CopyValidate)
        {
            int expectedFields = table switch
            {
                IntentStageTable.Entities      => 4,
                IntentStageTable.Physicalities => 11,
                _                              => 9,
            };
            ValidateCopyBlob(ptr, len, expectedFields, tableName, rowCount);
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
        {
            await stream.WriteAsync(CopyBinaryHeader, ct);
            if (len > 0)
            {
                var window = new byte[(int)Math.Min(CopyChunkBytes, len)];
                for (long off = 0; off < len; off += window.Length)
                {
                    int n = (int)Math.Min(window.Length, len - off);
                    System.Runtime.InteropServices.Marshal.Copy(ptr + (nint)off, window, 0, n);
                    await stream.WriteAsync(window.AsMemory(0, n), ct);
                }
            }
            await stream.WriteAsync(CopyBinaryTrailer, ct);
            await stream.FlushAsync(ct);
        }

        await using (var promote = conn.CreateCommand())
        {
            promote.CommandTimeout = 0;
            promote.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) " +
                $"SELECT {cols} FROM {stageName} ORDER BY id ON CONFLICT DO NOTHING";
            return await promote.ExecuteNonQueryAsync(ct);
        }
    }
}
