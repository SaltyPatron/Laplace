using System.Diagnostics;
using global::Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The one substrate write surface. Implements
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
///         table, filtered to NOVEL rows only (existence check + in-batch
///         dedup; idempotency is the content-addressed id).</item>
///   <item>Prove referential integrity SET-BASED: every entity id referenced
///         by a staged row resolves (staged this batch, or proven present via
///         one more <c>entities_exist_bitmap</c> round-trip). Any miss throws
///         <see cref="SubstrateReferentialIntegrityException"/> BEFORE
///         anything is written.</item>
///   <item>Stream each buffer over Npgsql's raw binary COPY inside one
///         transaction running <c>SET LOCAL session_replication_role =
///         replica</c>: the per-row RI triggers are skipped because the same
///         invariant was just proven set-based (measured warm on the live
///         substrate: per-row FK machinery ≈ 30s per 500k attestations —
///         2.5M trigger firings + KEY SHARE locks — vs ~ms for the set
///         proof). FK constraints REMAIN in the schema for every non-bulk
///         writer; <c>scripts/verify-fk.sql</c> is the independent audit.</item>
/// </list>
///
/// <para>
/// Best case (intent fully duplicate at the entity level): 1 round-trip
/// (the existence SRF; no COPYs issued because all 3 buffers are empty) —
/// and ZERO round-trips when every presented id is in the run-scoped
/// proven-id cache. Novel intent: 6 round-trips (2 SRF + SET LOCAL + 3 COPYs).
/// </para>
///
/// <para><b>Run-scoped proven-id cache.</b> Ids are content-addressed, so an
/// id once proven THIS RUN — found existing, or staged and committed — never
/// needs presenting again: corpus decomposers re-present the same wordform
/// trees and attestation identities for every occurrence (measured on run
/// 27001038623: UD presented 985M rows for 55M novel, 17.8:1; OMW 10.1:1),
/// and without the cache every re-occurrence re-ships its ids for an
/// existence re-proof. The cache filters presented ids BEFORE the wire.
/// Correctness: purely advisory — a miss only costs the old behavior; adds
/// happen for DB-found ids immediately (true regardless of this txn) and for
/// staged ids only AFTER COMMIT (a rolled-back batch never poisons it).
/// Consensus testimony is unaffected: the accumulating writer consumes
/// scores/games BEFORE rows reach this class. Caveat (pre-existing law, see
/// the GUC comment below): never run per-source evictions under a live
/// ingest — eviction invalidates "proven present". Env:
/// <c>LAPLACE_PROVEN_CACHE=0</c> disables; <c>LAPLACE_PROVEN_CACHE_MAX</c>
/// caps entries per table (default 256M ≈ 14 GB worst-case; past the cap the
/// cache stops growing and extra ids just take the uncached path).</para>
/// </summary>
public sealed class NpgsqlSubstrateWriter : ISubstrateWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly NpgsqlSubstrateReader _reader;
    private readonly ILogger<NpgsqlSubstrateWriter> _log;
    private readonly ProvenIdCache _provenEntities;
    private readonly ProvenIdCache _provenPhys;
    private readonly ProvenIdCache _provenAtt;

    /// <summary>Concurrent advisory set of ids proven present (DB-found or
    /// committed by this run). Thread-safe for ParallelWorkers &gt; 1.</summary>
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
        ILogger<NpgsqlSubstrateWriter>? logger = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlSubstrateReader(dataSource);
        _log = logger ?? NullLogger<NpgsqlSubstrateWriter>.Instance;
        bool cacheOn = Environment.GetEnvironmentVariable("LAPLACE_PROVEN_CACHE") != "0";
        int cacheMax = int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_PROVEN_CACHE_MAX"), out var m) && m > 0
            ? m : 256_000_000;
        _provenEntities = new ProvenIdCache(cacheOn, cacheMax);
        _provenPhys     = new ProvenIdCache(cacheOn, cacheMax);
        _provenAtt      = new ProvenIdCache(cacheOn, cacheMax);
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
        //    the batch (deduped), PRE-FILTERED by the run-scoped proven-id
        //    cache: ids proven this run never re-ship for an existence proof.
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
        _provenEntities.AddRange(existingEntities);   // DB-found: true regardless of this txn

        // 2. Physicality identity dedup — ONE query for the cache-filtered phys
        //    ids. COPY can't ON CONFLICT, so we filter to novel ids before
        //    staging: content shared across the batch (same grapheme/word/
        //    tensor) becomes a no-op instead of a physicalities_pkey clash.
        var physToCheck = CollectUnprovenIds(changes, static c => c.Physicalities, static p => p.Id, _provenPhys);
        var existingPhys = await LoadExistingIdsAsync(conn, "physicalities", physToCheck, ct);
        if (physToCheck.Count > 0) roundTrips++;
        _provenPhys.AddRange(existingPhys);

        // 3. Attestation identity dedup — ONE query for the cache-filtered
        //    attestation ids. Attestation ids are content-addressed BLAKE3 of
        //    (subject,kind,object,source,context); the same observation
        //    re-emitted is the same id and must not collide. Glicko-2 matchup
        //    updates on re-observation are a separate, later concern.
        var attToCheck = CollectUnprovenIds(changes, static c => c.Attestations, static a => a.Id, _provenAtt);
        var existingAtt = await LoadExistingIdsAsync(conn, "attestations", attToCheck, ct);
        if (attToCheck.Count > 0) roundTrips++;
        _provenAtt.AddRange(existingAtt);

        // 4. Stage ALL novel rows across the batch into ONE COPY stream per
        //    table (FK order: entities, then physicalities, then attestations).
        using var stage = IntentStage.New(Math.Max(uniqueEntityIds.Count, physAttempted));

        var seenEntity = new HashSet<Hash128>(uniqueEntityIds.Count);
        var seenPhys   = new HashSet<Hash128>(existingPhys);
        var seenAtt    = new HashSet<Hash128>(existingAtt);
        var stagedPhysIds = new List<Hash128>();
        var stagedAttIds  = new List<Hash128>();
        Span<double> coord = stackalloc double[4];

        // Referenced-entity collection for the SET-BASED referential proof
        // below: every entity id a STAGED row points at. Ids presented as batch
        // entities (seenEntityArg) are excluded — they are either already in the
        // DB (existingEntities) or staged in this same transaction, so they
        // resolve by commit either way. Ids already PROVEN this run are
        // excluded too — their existence proof already happened.
        var referenced = new HashSet<Hash128>();
        void Reference(Hash128 id)
        {
            if (!seenEntityArg.Contains(id) && !_provenEntities.Contains(id)) referenced.Add(id);
        }

        foreach (var c in changes)
            foreach (var e in c.Entities)
            {
                if (existingEntities.Contains(e.Id)) continue;     // already in DB
                if (_provenEntities.Contains(e.Id)) continue;      // proven earlier this run
                if (!seenEntity.Add(e.Id)) continue;               // already staged this batch
                stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
                Reference(e.TypeId);
                if (e.FirstObservedBy is Hash128 fob) Reference(fob);
            }
        foreach (var c in changes)
            foreach (var p in c.Physicalities)
            {
                if (_provenPhys.Contains(p.Id)) continue;   // proven earlier this run
                if (!seenPhys.Add(p.Id)) continue;          // in DB or already staged this batch
                coord[0] = p.CoordX; coord[1] = p.CoordY; coord[2] = p.CoordZ; coord[3] = p.CoordM;
                stage.AddPhysicality(
                    p.Id, p.EntityId, p.SourceId, (short)p.Kind,
                    coord, p.HilbertIndex,
                    p.TrajectoryXyzm is null ? ReadOnlySpan<double>.Empty
                                              : p.TrajectoryXyzm.AsSpan(),
                    p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
                stagedPhysIds.Add(p.Id);
                Reference(p.EntityId);
                Reference(p.SourceId);
            }
        foreach (var c in changes)
            foreach (var a in c.Attestations)
            {
                if (_provenAtt.Contains(a.Id)) continue;    // proven earlier this run
                if (!seenAtt.Add(a.Id)) continue;           // in DB or already staged this batch
                stage.AddAttestation(
                    a.Id, a.SubjectId, a.KindId, a.ObjectId, a.SourceId, a.ContextId,
                    (short)a.Outcome,
                    a.LastObservedAtUnixUs, a.ObservationCount);
                stagedAttIds.Add(a.Id);
                Reference(a.SubjectId);
                Reference(a.KindId);
                Reference(a.SourceId);
                if (a.ObjectId  is Hash128 aObj) Reference(aObj);
                if (a.ContextId is Hash128 aCtx) Reference(aCtx);
            }

        // Referential proof — the SET-BASED replacement for the per-row FK
        // triggers on the bulk path. ONE bitmap round-trip proves every
        // referenced id resolves; any miss aborts BEFORE the first COPY byte
        // (fail-closed: nothing written, vs FK which fails after the work).
        // Measured on the live 140M-row substrate, fully warm: PG's per-row RI
        // machinery (one trigger firing + FOR KEY SHARE lock per row per FK;
        // 5 FKs ⇒ 2.5M firings per 500k attestations) costs ~30s; this proof
        // is one indexed probe per DISTINCT referenced id (~ms). Same
        // invariant, proven once per batch. The FK constraints REMAIN in the
        // schema enforcing every non-bulk write; scripts/verify-fk.sql remains
        // the independent audit.
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
            _provenEntities.AddRange(present);   // referential proof = existence proof
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
            //, which is what unlocks ParallelWorkers > 1. The INSERT's
            // rows-affected is the TRUE inserted count (≤ staged, under conflict).
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // Referential integrity was proven set-based above, so the
                // per-row RI triggers are pure redundant cost on this bulk
                // transaction — skip them HERE ONLY. SET LOCAL is txn-scoped
                // (reverts at commit/rollback); the FK constraints stay live
                // for every other writer. PK/UNIQUE/CHECK are index/executor
                // enforced, not triggers — still active during the COPY.
                // Requires SET privilege on the GUC (laplace_admin is
                // superuser; a denial throws 42501 = fail-loud, no silent
                // downgrade). Single-writer assumption (the ingest default):
                // a concurrent per-source eviction between proof and commit
                // is the one race PG's KEY SHARE locks covered — do not run
                // evictions under a live ingest.
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
                await tx.CommitAsync(ct);

                // COMMITTED: every staged id is now durably present — feed the
                // run-scoped proven cache so no later batch re-presents them.
                // (On rollback/exception these adds never happen.)
                _provenEntities.AddRange(seenEntity);
                _provenPhys.AddRange(stagedPhysIds);
                _provenAtt.AddRange(stagedAttIds);
            }
            catch
            {
                // Best-effort rollback. If the connection was already torn down mid-
                // transaction (e.g. 57P01 admin_shutdown when the cluster is restarted
                // under the ingest), RollbackAsync itself throws ObjectDisposedException.
                // That MUST NOT mask the original exception — otherwise the retry
                // classifier (TransientErrorRetryPolicy) never sees the real transient
                // SQLSTATE and the whole run aborts on a recoverable blip.
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { /* connection already dead; the original exception is authoritative */ }
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
    /// One <c>laplace.entities_exist_bitmap</c> round-trip (chunked at 100k ids
    /// per call, same bound as <see cref="LoadExistingIdsAsync"/>) returning the
    /// subset of <paramref name="ids"/> present in <c>laplace.entities</c>.
    /// Serves both the novel-entity filter (step 1) and the set-based
    /// referential proof.
    /// </summary>
    private static async Task<HashSet<Hash128>> EntitiesExistAsync(
        NpgsqlConnection conn, IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var existing = new HashSet<Hash128>();
        const int ChunkSize = 100_000;
        await using var cmd = conn.CreateCommand();
        // Bulk-path commands are UNBOUNDED (CommandTimeout 0): under a heavy
        // checkpoint an indexed probe over a 10⁸-row table can exceed Npgsql's
        // 30 s default, whose client-side cancel surfaces as "canceling
        // statement due to user request" + a dead connection mid-batch.
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

    /// <summary>Deduped ids of the given row kind across the batch, minus the
    /// ones the run-scoped cache already proved — the only ids worth shipping
    /// for an existence check.</summary>
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

    /// <summary>
    /// One <c>SELECT id FROM laplace.&lt;table&gt; WHERE id = ANY(@ids)</c> over
    /// the (deduped, cache-filtered) ids — the COPY-can't-ON-CONFLICT identity
    /// filter, hoisted from per-intent to per-batch.
    /// </summary>
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

        // Chunk the existence check. A single `= ANY(@ids)` over an unbounded id array
        // overflows Npgsql's int32 parameter-size computation once a circuit intent
        // content-addresses into ~100M+ edges (one QK head over a large vocab). Dedup is
        // by id regardless; this only bounds how many ids ride one round-trip.
        const int ChunkSize = 100_000;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;   // bulk path — never client-cancel mid-batch
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

    // PostgreSQL COPY BINARY framing (constant): "PGCOPY\n\377\r\n\0" + int32 flags(0)
    // + int32 header-extension-length(0); trailer = int16 -1. These 19+2 bytes are the
    // ONLY managed bytes in the load path now — the tuples stream from the engine buffer.
    private static readonly byte[] CopyBinaryHeader =
        { 0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00,
          0, 0, 0, 0,  0, 0, 0, 0 };
    private static readonly byte[] CopyBinaryTrailer = { 0xFF, 0xFF };
    private const long CopyChunkBytes = 1L << 22;   // 4 MB socket-write window over the engine buffer

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

        // The writer's contract is `ON CONFLICT DO NOTHING` (R5) — idempotent AND
        // race-tolerant under concurrent writers. The client-side existence check +
        // in-batch dedup above already shrink the staged set to (believed-)novel rows,
        // so the set-based promote below sees few-to-zero conflicts in the common case;
        // it exists to make convergence a property of the WRITE, not an assumption
        // about who else is writing. COPY streams the engine-emitted tuples into an
        // ON COMMIT DROP temp table (header(19) + tuples through a fixed O(1) window +
        // trailer(2); no managed array ever holds the intent), then ONE set-based
        // `INSERT … SELECT … ON CONFLICT DO NOTHING` promotes into the real table.
        string stageName = $"_laplace_stage_{tableName}";
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandTimeout = 0;   // bulk path — never client-cancel mid-batch
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
            // UNBOUNDED: a 500k-row promote under a heavy checkpoint can exceed
            // the 30 s default; Npgsql's cancel kills the batch ("canceling
            // statement due to user request") and the retry hits the same wall.
            promote.CommandTimeout = 0;
            promote.CommandText =
                $"INSERT INTO laplace.{tableName} ({cols}) " +
                $"SELECT {cols} FROM {stageName} ON CONFLICT DO NOTHING";
            return await promote.ExecuteNonQueryAsync(ct);
        }
    }
}
