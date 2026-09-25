using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class NpgsqlSubstrateReader : ISubstrateReader
{
    private const byte EntityPresenceLane = 0;

    private readonly NpgsqlDataSource _ds;
    private readonly PresenceProbeBatcher<byte> _entityProbes;
    private readonly PresenceProbeBatcher<short> _tierProbes;
    private readonly IngestSizing.ApplyIoPlan _cachePlan;
    private readonly Func<Hash128, IReadOnlyList<Hash128>?, IReadOnlyList<Hash128>?,
        CancellationToken, Task> _evictSource;

    public NpgsqlDataSource DataSource => _ds;

    public NpgsqlSubstrateReader(NpgsqlDataSource dataSource)
        : this(dataSource, null, null)
    {
    }

    // Keep transport replaceable for deterministic cache-lifetime controls.
    // Production always uses the same PostgreSQL implementations below.
    internal NpgsqlSubstrateReader(NpgsqlDataSource dataSource,
        Func<IReadOnlyList<Hash128>, CancellationToken, Task<byte[]>>? entityProbe,
        Func<Hash128, IReadOnlyList<Hash128>?, IReadOnlyList<Hash128>?,
            CancellationToken, Task>? evictSource,
        Func<IReadOnlyList<Hash128>, short, CancellationToken, Task<byte[]>>? tierProbe = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _cachePlan = IngestSizing.ResolveApplyIo(IngestTopology.Current.ApplyPartitions);
        var probe = entityProbe ?? EntitiesExistBitmapDirectAsync;
        _entityProbes = new PresenceProbeBatcher<byte>(
            (ids, _, ct) => probe(ids, ct),
            _cachePlan.ProbeChunkIds);
        _tierProbes = new PresenceProbeBatcher<short>(
            tierProbe ?? TierBatchExistenceProbeDirectAsync, _cachePlan.ProbeChunkIds);
        _evictSource = evictSource ?? EvictSourceDirectAsync;
    }

    public async Task<IReadOnlySet<Hash128>> PresentAttestationIdsAsync(
        Hash128 typeId, IReadOnlyList<Hash128> ids, CancellationToken ct = default)
    {
        var present = new HashSet<Hash128>();
        if (ids.Count == 0) return present;
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        int probeChunk = Math.Max(1, _cachePlan.ProbeChunkIds);
        var type = typeId.ToBytes();
        for (int offset = 0; offset < ids.Count; offset += probeChunk)
        {
            int count = Math.Min(probeChunk, ids.Count - offset);
            var raw = new byte[count][];
            for (int i = 0; i < count; i++) raw[i] = ids[offset + i].ToBytes();
            var found = await NpgsqlAttestationReads.PresentIdsAsync(conn, type, raw, ct)
                .ConfigureAwait(false);
            foreach (var id in found) present.Add(Hash128.FromBytes(id));
        }
        return present;
    }

    public async Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM laplace.ingest_layer_completion WHERE layer = $1)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, layerOrder);
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    public async Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM laplace.ingest_layer_completion "
            + "WHERE witness_id = $1 AND layer = $2)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, layerOrder);
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    public async Task<bool> HasFileCompletedAsync(
        Hash128 fileId, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default)
    {
        var done = await HasFilesCompletedAsync([fileId], decomposerSourceId, layerOrder, ct)
            .ConfigureAwait(false);
        return done.Contains(fileId);
    }

    /// <summary>
    /// One round trip for N source witnesses against the layer completion key
    /// (witness_id, layer). An unreadable completion surface means "not known complete",
    /// never "complete": resuming re-observes; the opposite would silently skip work.
    /// </summary>
    public async Task<IReadOnlySet<Hash128>> HasSourcesCompletedAsync(
        IReadOnlyList<Hash128> sourceIds, int layerOrder, CancellationToken ct = default)
    {
        var done = new HashSet<Hash128>();
        if (sourceIds.Count == 0) return done;

        await using var cmd = _ds.CreateCommand(
            "SELECT c.witness_id FROM laplace.ingest_layer_completion c "
            + "WHERE c.witness_id = ANY($1) AND c.layer = $2");
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = ToByteArrays(sourceIds),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, layerOrder);
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                done.Add(Hash128.FromBytes((byte[])r[0]));
            return done;
        }
        catch (PostgresException)
        {
            return new HashSet<Hash128>();
        }
    }

    /// <summary>
    /// One round trip for N file roots completed by one decomposer witness. This is the
    /// per-file resume probe: FrameNet's 14,900 files once spent 37.7 ms each on scalar
    /// probes (562 s of a 561 s run), so membership is always asked as one set.
    /// </summary>
    public async Task<IReadOnlySet<Hash128>> HasFilesCompletedAsync(
        IReadOnlyList<Hash128> fileIds, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default)
    {
        var done = new HashSet<Hash128>();
        if (fileIds.Count == 0) return done;

        await using var cmd = _ds.CreateCommand(
            "SELECT DISTINCT c.unit_id FROM laplace.ingest_unit_completion c "
            + "WHERE c.witness_id = $1 AND c.unit_id = ANY($2) AND c.layer = $3 "
            + "AND c.digest IS NULL");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, decomposerSourceId.ToBytes());
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = ToByteArrays(fileIds),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, layerOrder);
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                done.Add(Hash128.FromBytes((byte[])r[0]));
        }
        catch (PostgresException)
        {
            // Unreadable completion state means not known complete. Re-observation is
            // safe; silently skipping a file is not.
            done.Clear();
        }
        return done;
    }

    public async Task<IReadOnlySet<IngestUnitCompletionKey>> CompletedUnitsAsync(
        IReadOnlyList<IngestUnitCompletionKey> keys, CancellationToken ct = default)
    {
        var done = new HashSet<IngestUnitCompletionKey>();
        if (keys.Count == 0) return done;
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        int chunk = Math.Max(1, _cachePlan.ProbeChunkIds);
        for (int offset = 0; offset < keys.Count; offset += chunk)
        {
            int count = Math.Min(chunk, keys.Count - offset);
            var witnesses = new byte[count][];
            var units = new byte[count][];
            var layers = new int[count];
            var digests = new byte[]?[count];
            for (int i = 0; i < count; i++)
            {
                var key = keys[offset + i];
                witnesses[i] = key.WitnessId.ToBytes();
                units[i] = key.UnitId.ToBytes();
                layers[i] = key.Layer;
                digests[i] = key.Digest?.ToBytes();
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT k.ord FROM unnest($1::bytea[], $2::bytea[], $3::int[], $4::bytea[]) "
                + "WITH ORDINALITY AS k(witness_id, unit_id, layer, digest, ord) "
                + "WHERE EXISTS (SELECT 1 FROM laplace.ingest_unit_completion c "
                + "WHERE c.witness_id = k.witness_id AND c.unit_id = k.unit_id "
                + "AND c.layer = k.layer AND c.digest IS NOT DISTINCT FROM k.digest)";
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = witnesses, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = units, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = layers, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            cmd.Parameters.Add(new NpgsqlParameter
            { Value = digests, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                done.Add(keys[offset + (int)(r.GetInt64(0) - 1)]);
        }
        return done;
    }

    private static byte[][] ToByteArrays(IReadOnlyList<Hash128> ids)
    {
        var raw = new byte[ids.Count][];
        for (int i = 0; i < ids.Count; i++) raw[i] = ids[i].ToBytes();
        return raw;
    }

    public async Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ops.entity_count($1)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, typeId.ToBytes());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
    }

    public async Task<PhysicalityCoverage> PhysicalityCoverageAsync(
        Hash128 sourceId,
        CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT count(*)::bigint,"
            + "       count(*) FILTER (WHERE EXISTS ("
            + "         SELECT 1 FROM laplace.physicalities p WHERE p.entity_id = e.id"
            + "       ))::bigint"
            + " FROM laplace.entities e"
            + " WHERE EXISTS (SELECT 1 FROM laplace.attestations a"
            + "   WHERE a.source_id = $1 AND a.subject_id = e.id)");
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());

        await using var result = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await result.ReadAsync(ct).ConfigureAwait(false))
            return new PhysicalityCoverage(0, 0);
        return new PhysicalityCoverage(result.GetInt64(0), result.GetInt64(1));
    }

    public async Task<PhysicalityCoverage> PhysicalityCoverageAsync(
        Hash128 sourceId,
        IReadOnlyList<Hash128> typeIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(typeIds);
        if (typeIds.Count == 0) return new PhysicalityCoverage(0, 0);

        var rawTypes = new byte[typeIds.Count][];
        for (int i = 0; i < typeIds.Count; i++) rawTypes[i] = typeIds[i].ToBytes();

        await using var cmd = _ds.CreateCommand(
            "WITH touched AS MATERIALIZED ("
            + " SELECT a.subject_id AS entity_id FROM laplace.attestations a WHERE a.source_id = $1"
            + " UNION SELECT a.object_id FROM laplace.attestations a"
            + "       WHERE a.source_id = $1 AND a.object_id IS NOT NULL"
            + " UNION SELECT a.context_id FROM laplace.attestations a"
            + "       WHERE a.source_id = $1 AND a.context_id IS NOT NULL"
            + "), governed AS MATERIALIZED ("
            + " SELECT DISTINCT e.id AS entity_id"
            + " FROM laplace.entities e"
            + " LEFT JOIN touched t ON t.entity_id = e.id"
            + " WHERE e.type_id = ANY($2)"
            + "   AND t.entity_id IS NOT NULL"
            + ")"
            + " SELECT count(*)::bigint,"
            + "        count(*) FILTER (WHERE EXISTS ("
            + "          SELECT 1 FROM laplace.physicalities p WHERE p.entity_id = g.entity_id"
            + "        ))::bigint"
            + " FROM governed g");
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = rawTypes,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });

        await using var result = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await result.ReadAsync(ct).ConfigureAwait(false))
            return new PhysicalityCoverage(0, 0);
        return new PhysicalityCoverage(result.GetInt64(0), result.GetInt64(1));
    }







    /// <summary>
    /// Ids confirmed present in the DB (or in this run's guaranteed-
    /// committed set) via a real presence-query result -- NOT "ids seen at
    /// least once". Only ever populate via <see cref="MarkProven"/> with an
    /// already-filtered "confirmed present" subset of a probe round; never
    /// with a probe round's whole, unfiltered candidate list. That
    /// unconditional-population bug (TierTreeDescent.cs previously calling
    /// MarkProven on an entire batch, including ids the same batch's
    /// bitmap had just proven absent) permanently poisoned this
    /// process-lifetime cache and caused every later occurrence of that
    /// content anywhere in the ingest run to be silently treated as already
    /// present -- see the dorian.txt repro in
    /// .scratchpad/02_Identified_Issues.txt.
    ///
    /// Capacity comes from the shared cache byte envelope. Once full, misses fall
    /// through to the DB; the useful hot prefix is not periodically erased.
    /// </summary>
    private sealed class ProvenPresence
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte> Ids = new();
        public int Count;
    }

    private ProvenPresence _proven = new();

    private void AddProven(ProvenPresence proven, Hash128 id)
    {
        if (Volatile.Read(ref proven.Count) >= _cachePlan.ReaderProvenCacheIds) return;
        if (!proven.Ids.TryAdd(id, 1)) return;
        int after = Interlocked.Increment(ref proven.Count);
        if (after <= _cachePlan.ReaderProvenCacheIds) return;
        if (proven.Ids.TryRemove(id, out _)) Interlocked.Decrement(ref proven.Count);
    }

    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        int n = candidates.Count;
        ct.ThrowIfCancellationRequested();
        var bm = new byte[BitmapBits.ByteLength(n)];
        if (n == 0) return bm;
        // A probe owns the cache generation it began with. Source eviction can
        // detach it while native/database work is in flight; late results must
        // never repopulate the replacement generation with deleted markers.
        var proven = Volatile.Read(ref _proven);
        var unresolved = new List<Hash128>();
        var positions = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (proven.Ids.ContainsKey(candidates[i])) BitmapBits.Set(bm, i);
            else { unresolved.Add(candidates[i]); positions.Add(i); }
        }
        if (unresolved.Count == 0) return bm;
        var floor = CodepointPerfcache.KnownIdsBitmap(unresolved);
        var dbUnknownIdx = new List<int>();
        for (int i = 0; i < positions.Count; i++)
        {
            if (BitmapBits.IsSet(floor, i)) BitmapBits.Set(bm, positions[i]);
            else dbUnknownIdx.Add(positions[i]);
        }
        if (dbUnknownIdx.Count == 0) return bm;

        var dbUnknown = new Hash128[dbUnknownIdx.Count];
        for (int u = 0; u < dbUnknownIdx.Count; u++) dbUnknown[u] = candidates[dbUnknownIdx[u]];

        // Every compose worker shares this reader. Root gates therefore join one
        // array-in native probe instead of opening one connection per working set.
        // The batcher deduplicates identities and restores each caller's positional
        // bitmap; the native function still owns the actual committed-row decision.
        var dbBm = await _entityProbes.ProbeAsync(dbUnknown, EntityPresenceLane, ct)
            .ConfigureAwait(false);

        for (int u = 0; u < dbUnknownIdx.Count; u++)
        {
            if (BitmapBits.IsSet(dbBm, u))
            {
                int i = dbUnknownIdx[u];
                BitmapBits.Set(bm, i);
                AddProven(proven, candidates[i]);
            }
        }
        return bm;
    }

    private async Task<byte[]> EntitiesExistBitmapDirectAsync(
        IReadOnlyList<Hash128> ids, CancellationToken ct)
    {
        var byteaArray = new byte[ids.Count][];
        for (int i = 0; i < ids.Count; i++) byteaArray[i] = ids[i].ToBytes();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.entities_exist_bitmap($1)";
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        await cmd.PrepareAsync(ct);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as byte[]
            ?? new byte[BitmapBits.ByteLength(ids.Count)];
    }

    public PresenceCacheScope CapturePresenceScope() => new(Volatile.Read(ref _proven));

    public void MarkProven(IReadOnlyList<Hash128> ids, PresenceCacheScope scope)
    {
        if (ids is null || scope.State is not ProvenPresence proven
            || !ReferenceEquals(proven, Volatile.Read(ref _proven))) return;
        // If eviction races this loop, the captured object becomes detached;
        // neither these hints nor a late probe can enter its replacement.
        for (int i = 0; i < ids.Count; i++) AddProven(proven, ids[i]);
    }

    public bool IsProvenPresent(Hash128 id) => Volatile.Read(ref _proven).Ids.ContainsKey(id);





    // Same resource plan as _proven: canonical→root is deterministic, so a cache
    // miss only costs a recompute.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, Hash128> _rootCache = new();
    private int _rootCacheApprox;
    public bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) => _rootCache.TryGetValue(canonicalKey, out rootId);
    public void CacheRoot(Hash128 canonicalKey, Hash128 rootId)
    {
        if (Volatile.Read(ref _rootCacheApprox) >= _cachePlan.ReaderRootCacheIds) return;
        if (!_rootCache.TryAdd(canonicalKey, rootId)) return;
        int after = Interlocked.Increment(ref _rootCacheApprox);
        if (after <= _cachePlan.ReaderRootCacheIds) return;
        if (_rootCache.TryRemove(canonicalKey, out _)) Interlocked.Decrement(ref _rootCacheApprox);
    }

    public async Task<byte[]> ContentDescentBitmapAsync(
    IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        if (parents is null) throw new ArgumentNullException(nameof(parents));
        if (ids.Count != parents.Count)
            throw new ArgumentException("ids and parents must be the same length");
        if (ids.Count == 0) return Array.Empty<byte>();

        ct.ThrowIfCancellationRequested();
        var proven = Volatile.Read(ref _proven);
        var floor = CodepointPerfcache.KnownIdsBitmap(ids);
        bool allProven = true;
        for (int i = 0; i < ids.Count; i++)
        {
            if (BitmapBits.IsSet(floor, i)) continue;
            if (proven.Ids.ContainsKey(ids[i])) BitmapBits.Set(floor, i);
            else allProven = false;
        }
        if (allProven) return floor;

        var byteaArray = new byte[ids.Count][];
        for (int i = 0; i < ids.Count; i++) byteaArray[i] = ids[i].ToBytes();
        var parentArray = new int[parents.Count];
        for (int i = 0; i < parents.Count; i++) parentArray[i] = parents[i];

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.content_descent_bitmap($1, $2)";
        var p1 = cmd.Parameters.AddWithValue(byteaArray);
        p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p2 = cmd.Parameters.AddWithValue(parentArray);
        p2.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
        await cmd.PrepareAsync(ct);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch
        {
            byte[] bytes => bytes,
            null => Array.Empty<byte>(),
            _ => throw new InvalidOperationException(
                $"content_descent_bitmap returned unexpected type: {result.GetType()}")
        };
    }

    /// <summary>
    /// Resolve the immutable floor before transport. Only unresolved identities
    /// enter the shared tier batch; native PostgreSQL probes confirm stored rows.
    /// The positional map preserves duplicate inputs and their exact bit positions.
    /// </summary>
    public async Task<byte[]> TierBatchExistenceProbeAsync(
        IReadOnlyList<Hash128> ids, short tier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ct.ThrowIfCancellationRequested();
        var bitmap = CodepointPerfcache.KnownIdsBitmap(ids);
        var unresolved = new List<Hash128>();
        var positions = new List<int>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (BitmapBits.IsSet(bitmap, i)) continue;
            unresolved.Add(ids[i]);
            positions.Add(i);
        }
        if (unresolved.Count == 0) return bitmap;
        var stored = await _tierProbes.ProbeAsync(unresolved, tier, ct).ConfigureAwait(false);
        for (int i = 0; i < positions.Count; i++)
            if (BitmapBits.IsSet(stored, i)) BitmapBits.Set(bitmap, positions[i]);
        return bitmap;
    }

    private async Task<byte[]> TierBatchExistenceProbeDirectAsync(
        IReadOnlyList<Hash128> ids, short tier, CancellationToken ct)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        int n = ids.Count;
        if (n == 0) return Array.Empty<byte>();

        var byteaArray = new byte[n][];
        for (int i = 0; i < n; i++) byteaArray[i] = ids[i].ToBytes();
        var tiers = new short[n];
        Array.Fill(tiers, tier);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.tier_batch_existence_probe($1, $2)";
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var pt = cmd.Parameters.AddWithValue(tiers);
        pt.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint;
        await cmd.PrepareAsync(ct);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch
        {
            byte[] bytes => bytes,
            null => new byte[(n + 7) / 8],
            _ => throw new InvalidOperationException(
                $"tier_batch_existence_probe returned unexpected type: {result.GetType()}")
        };
    }

    public async Task<IReadOnlyList<CircuitRelation>> ClassifyCircuitAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, CancellationToken ct = default)
    {
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));
        if (pairs.Count == 0) return Array.Empty<CircuitRelation>();

        var packed = new byte[pairs.Count][];
        for (int i = 0; i < pairs.Count; i++)
        {
            var buf = new byte[32];
            pairs[i].Subject.WriteBytes(buf.AsSpan(0, 16));
            pairs[i].Object.WriteBytes(buf.AsSpan(16, 16));
            packed[i] = buf;
        }

        await using var cmd = _ds.CreateCommand(
            "SELECT subject_id, object_id, type_id, eff_mu, witnesses FROM consensus.classify_circuit($1)");
        var p = cmd.Parameters.AddWithValue(packed);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;

        var rows = new List<CircuitRelation>(pairs.Count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var subj = Hash128.FromBytes((byte[])rdr[0]);
            var obj = Hash128.FromBytes((byte[])rdr[1]);
            var type = Hash128.FromBytes((byte[])rdr[2]);
            double emu = rdr.IsDBNull(3) ? 0.0 : (double)rdr.GetDecimal(3);
            long w = rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4);
            rows.Add(new CircuitRelation(subj, obj, type, emu, w));
        }
        return rows;
    }

    public async Task<CircuitPairProposalPage> ReadCircuitPairProposalsAsync(
        IReadOnlyList<Hash128> vocabulary, Hash128 targetTypeId, bool targetSymmetric,
        Hash128? afterSubject, Hash128? afterObject, int pageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (vocabulary.Count == 0)
            return new CircuitPairProposalPage(Array.Empty<CircuitPairProposal>(), null, null);

        var ids = new byte[vocabulary.Count][];
        for (int i = 0; i < vocabulary.Count; i++) ids[i] = vocabulary[i].ToBytes();
        await using var cmd = _ds.CreateCommand(
            "SELECT subject_id, object_id, basis_type_ids "
            + "FROM consensus.circuit_pair_proposals($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = targetTypeId.ToBytes() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = targetSymmetric });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)afterSubject?.ToBytes() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = (object?)afterObject?.ToBytes() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = pageSize });

        var rows = new List<CircuitPairProposal>(pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[][] basisBytes = reader.GetFieldValue<byte[][]>(2);
            var basis = new Hash128[basisBytes.Length];
            for (int i = 0; i < basis.Length; i++) basis[i] = Hash128.FromBytes(basisBytes[i]);
            rows.Add(new CircuitPairProposal(
                Hash128.FromBytes(reader.GetFieldValue<byte[]>(0)),
                Hash128.FromBytes(reader.GetFieldValue<byte[]>(1)),
                basis));
        }
        return rows.Count == pageSize
            ? new CircuitPairProposalPage(rows, rows[^1].Subject, rows[^1].Object)
            : new CircuitPairProposalPage(rows, null, null);
    }

    public async Task<IReadOnlyList<double>> GetEdgeStrengthsAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, Hash128 typeId, CancellationToken ct = default)
    {
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));
        if (pairs.Count == 0) return Array.Empty<double>();

        var subj = new byte[pairs.Count][];
        var obj = new byte[pairs.Count][];
        for (int i = 0; i < pairs.Count; i++)
        {
            subj[i] = pairs[i].Subject.ToBytes();
            obj[i] = pairs[i].Object.ToBytes();
        }



        // Installed pair-scoring surface, not a hand-rolled join over the consensus
        // table. The unattested->0 COALESCE this caller depends on is part of that
        // function's contract and documented there as a deliberate tri-state collapse
        // for scoring; presence questions use consensus_cell instead.
        await using var cmd = _ds.CreateCommand(
            "SELECT score FROM consensus.pair_scores($1, $3, $2) ORDER BY ord");
        var p1 = cmd.Parameters.AddWithValue(subj); p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p2 = cmd.Parameters.AddWithValue(obj); p2.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p3 = cmd.Parameters.AddWithValue(typeId.ToBytes()); p3.NpgsqlDbType = NpgsqlDbType.Bytea;

        var outv = new List<double>(pairs.Count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) outv.Add(rdr.GetDouble(0));
        return outv;
    }

    /// <summary>
    /// Retract a source's testimony and refold every cell it touched
    /// (<c>ops.evict_source</c>, GH #508). The procedure COMMITs per batch and
    /// RAISE LOGs progress server-side, so hours are legitimate on a large lane —
    /// hence no command timeout. <paramref name="relationIds"/> and
    /// <paramref name="markerTypeIds"/> are null to mean "every relation the source
    /// has rows under" and "no marker cleanup" respectively.
    /// </summary>
    public async Task EvictSourceAsync(
        Hash128 sourceId, IReadOnlyList<Hash128>? relationIds,
        IReadOnlyList<Hash128>? markerTypeIds, CancellationToken ct = default)
    {
        try
        {
            await _evictSource(sourceId, relationIds, markerTypeIds, ct).ConfigureAwait(false);
        }
        finally
        {
            // The procedure commits in batches, so even a failed/cancelled call
            // can have deleted marker entities. No pre-eviction presence claim
            // may gate the next derivation. Swapping the generation also keeps
            // delayed probes from restoring stale positives after invalidation.
            Interlocked.Exchange(ref _proven, new ProvenPresence());
        }
    }

    private async Task EvictSourceDirectAsync(
        Hash128 sourceId, IReadOnlyList<Hash128>? relationIds,
        IReadOnlyList<Hash128>? markerTypeIds, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand("CALL ops.evict_source($1, $2, $3)");
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            relationIds is null
                ? DBNull.Value
                : (object)relationIds.Select(r => r.ToBytes()).ToArray());
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            markerTypeIds is null
                ? DBNull.Value
                : (object)markerTypeIds.Select(m => m.ToBytes()).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);

        // Remove unphysical shells from the canonical entity table directly.
        await using var cleanup = _ds.CreateCommand(
            "WITH invalid AS MATERIALIZED ("
            + " SELECT e.id"
            + " FROM laplace.entities e"
            + " WHERE EXISTS (SELECT 1 FROM laplace.attestations a"
            + "               WHERE a.source_id = $1 AND a.subject_id = e.id)"
            + "   AND NOT EXISTS (SELECT 1 FROM laplace.physicalities p"
            + "                   WHERE p.entity_id = e.id)"
            + "), orphan_ids AS MATERIALIZED ("
            + " SELECT i.id FROM invalid i"
            + " WHERE NOT EXISTS (SELECT 1 FROM laplace.physicalities p"
            + "                   WHERE p.entity_id = i.id)"
            + "   AND NOT EXISTS (SELECT 1 FROM laplace.attestations a"
            + "                   WHERE a.subject_id = i.id"
            + "                      OR a.object_id = i.id"
            + "                      OR a.context_id = i.id)"
            + ")"
            + " DELETE FROM laplace.entities e USING orphan_ids o"
            + " WHERE e.id = o.id");
        cleanup.CommandTimeout = 0;
        cleanup.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        await cleanup.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Surviving evidence rows under a source — the eviction receipt. Zero is the
    /// expected answer after an unrestricted evict; a restricted one leaves the
    /// relations it did not name in place.
    /// </summary>
    public async Task<long> CountEvidenceBySourceAsync(Hash128 sourceId, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ops.evidence_count(p_source => $1)");
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }
}
