using System.Diagnostics;
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

        var existingEntities = entToCheck.Count > 0
            ? await EntitiesExistAsync(conn, entToCheck, ct)
            : new HashSet<Hash128>();
        if (entToCheck.Count > 0) roundTrips++;
        _provenEntities.AddRange(existingEntities);

        var physToCheck = CollectUnprovenIds(changes, static c => c.Physicalities, static p => p.Id, _provenPhys);
        var existingPhys = await LoadExistingIdsAsync(conn, "physicalities", physToCheck, ct);
        if (physToCheck.Count > 0) roundTrips++;
        _provenPhys.AddRange(existingPhys);

        var existingAtt = new HashSet<Hash128>();
        if (!_bulkFreshSource)
        {
            var attToCheck = CollectUnprovenIds(changes, static c => c.Attestations, static a => a.Id, _provenAtt);
            existingAtt = await LoadExistingIdsAsync(conn, "attestations", attToCheck, ct);
            if (attToCheck.Count > 0) roundTrips++;
            _provenAtt.AddRange(existingAtt);
        }

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
