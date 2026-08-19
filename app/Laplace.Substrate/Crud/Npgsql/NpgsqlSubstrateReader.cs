using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class NpgsqlSubstrateReader : ISubstrateReader
{
    private readonly NpgsqlDataSource _ds;
    private readonly TierProbeBatcher _tierProbes;
    private readonly IngestSizing.ApplyIoPlan _cachePlan;

    public NpgsqlDataSource DataSource => _ds;

    public NpgsqlSubstrateReader(NpgsqlDataSource dataSource)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _cachePlan = IngestSizing.ResolveApplyIo(IngestTopology.Current.ApplyPartitions);
        _tierProbes = new TierProbeBatcher(
            TierBatchExistenceProbeDirectAsync, _cachePlan.ProbeChunkIds);
    }

    public async Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ops.evidence_count(p_type => realize.canonical_id($1)) > 0");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1");
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is bool b && b;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    public async Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ops.evidence_count(p_type => realize.canonical_id($1), p_source => $2) > 0");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, sourceId.ToBytes());
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is bool b && b;
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
        await using var cmd = _ds.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM laplace.attestations a "
            + "WHERE a.type_id = realize.canonical_id($1) "
            + "AND a.source_id = $2 AND a.context_id = $3)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, fileId.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, decomposerSourceId.ToBytes());
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    /// <summary>
    /// One round trip for N file roots, replacing N scalar probes. See the interface
    /// doc for the measurement that motivated it (FrameNet: 14,900 files, 37.7 ms each,
    /// 562s of a 561s run).
    ///
    /// Two separate costs are removed, not one:
    ///   - N round trips become 1, over a bound bytea[] the planner can index-scan once;
    ///   - the marker type id is hashed ONCE here instead of `realize.canonical_id($1)`
    ///     re-deriving the same BLAKE3 on every call.
    /// EXISTS, not a count: the scalar form asks `evidence_count(...) > 0`, which counts
    /// matching rows to answer a membership question. A semi-join stops at the first hit
    /// per source.
    /// </summary>
    public async Task<IReadOnlySet<Hash128>> HasSourcesCompletedAsync(
        IReadOnlyList<Hash128> sourceIds, int layerOrder, CancellationToken ct = default)
    {
        var done = new HashSet<Hash128>();
        if (sourceIds.Count == 0) return done;

        var raw = new byte[sourceIds.Count][];
        for (int i = 0; i < sourceIds.Count; i++) raw[i] = sourceIds[i].ToBytes();

        await using var cmd = _ds.CreateCommand(
            "SELECT DISTINCT a.source_id FROM laplace.attestations a "
            + "WHERE a.type_id = realize.canonical_id($1) "
            + "  AND a.source_id = ANY($2)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1");
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = raw,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                done.Add(Hash128.FromBytes((byte[])r[0]));
            return done;
        }
        catch (PostgresException)
        {
            // Same posture as the scalar form: an unreadable marker surface means
            // "not known complete", never "complete". Resuming re-observes; the
            // opposite would silently skip un-ingested files.
            return new HashSet<Hash128>();
        }
    }

    public async Task<IReadOnlySet<Hash128>> HasFilesCompletedAsync(
        IReadOnlyList<Hash128> fileIds, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default)
    {
        var done = new HashSet<Hash128>();
        if (fileIds.Count == 0) return done;

        var raw = new byte[fileIds.Count][];
        for (int i = 0; i < fileIds.Count; i++) raw[i] = fileIds[i].ToBytes();

        await using var cmd = _ds.CreateCommand(
            "SELECT DISTINCT a.source_id FROM laplace.attestations a "
            + "WHERE a.type_id = realize.canonical_id($1) "
            + "AND a.source_id = ANY($2) AND a.context_id = $3");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1");
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = raw,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, decomposerSourceId.ToBytes());
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                done.Add(Hash128.FromBytes((byte[])r[0]));
        }
        catch (PostgresException)
        {
            // Unreadable marker state means not known complete. Re-observation is safe;
            // silently skipping a file is not.
        }
        return done;
    }

    public async Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT ops.entity_count($1)");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, typeId.ToBytes());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte> _proven = new();
    private int _provenApprox;

    private void AddProven(Hash128 id)
    {
        if (Volatile.Read(ref _provenApprox) >= _cachePlan.ReaderProvenCacheIds) return;
        if (!_proven.TryAdd(id, 1)) return;
        int after = Interlocked.Increment(ref _provenApprox);
        if (after <= _cachePlan.ReaderProvenCacheIds) return;
        if (_proven.TryRemove(id, out _)) Interlocked.Decrement(ref _provenApprox);
    }

    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        int n = candidates.Count;
        var bm = new byte[(n + 7) / 8];
        if (n == 0) return bm;



        var unknownIdx = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (_proven.ContainsKey(candidates[i])) BitmapBits.Set(bm, i);
            else unknownIdx.Add(i);
        }
        if (unknownIdx.Count == 0) return bm;


        var dbUnknownIdx = new List<int>(unknownIdx.Count);
        for (int u = 0; u < unknownIdx.Count; u++)
        {
            int i = unknownIdx[u];
            if (CodepointPerfcache.IsKnownCodepointId(candidates[i]))
            {
                BitmapBits.Set(bm, i);
                AddProven(candidates[i]);
            }
            else
                dbUnknownIdx.Add(i);
        }
        if (dbUnknownIdx.Count == 0) return bm;

        var byteaArray = new byte[dbUnknownIdx.Count][];
        for (int u = 0; u < dbUnknownIdx.Count; u++) byteaArray[u] = candidates[dbUnknownIdx[u]].ToBytes();

        await using var cmd = _ds.CreateCommand("SELECT laplace.entities_exist_bitmap($1)");
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var result = await cmd.ExecuteScalarAsync(ct);
        var dbBm = result as byte[] ?? Array.Empty<byte>();

        for (int u = 0; u < dbUnknownIdx.Count; u++)
        {
            if (BitmapBits.IsSet(dbBm, u))
            {
                int i = dbUnknownIdx[u];
                BitmapBits.Set(bm, i);
                AddProven(candidates[i]);
            }
        }
        return bm;
    }

    public void MarkProven(IReadOnlyList<Hash128> ids)
    {
        if (ids is null) return;
        for (int i = 0; i < ids.Count; i++) AddProven(ids[i]);
    }

    public bool IsProvenPresent(Hash128 id) => _proven.ContainsKey(id);





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

        bool allProven = true;
        for (int i = 0; i < ids.Count; i++)
        {
            if (!_proven.ContainsKey(ids[i])) { allProven = false; break; }
        }
        if (allProven)
        {
            var allBm = new byte[BitmapBits.ByteLength(ids.Count)];
            for (int i = 0; i < ids.Count; i++)
                BitmapBits.Set(allBm, i);
            return allBm;
        }

        var byteaArray = new byte[ids.Count][];
        for (int i = 0; i < ids.Count; i++) byteaArray[i] = ids[i].ToBytes();
        var parentArray = new int[parents.Count];
        for (int i = 0; i < parents.Count; i++) parentArray[i] = parents[i];

        await using var cmd = _ds.CreateCommand("SELECT laplace.content_descent_bitmap($1, $2)");
        var p1 = cmd.Parameters.AddWithValue(byteaArray);
        p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p2 = cmd.Parameters.AddWithValue(parentArray);
        p2.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer;
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
    /// One round of the tier-by-tier, trunk-to-leaf batch existence probe.
    /// Concurrent callers are coalesced by tier before this direct native
    /// tier_batch_existence_probe() call. There is no C# result cache here:
    /// the native side already does its own perfcache fast-path internally
    /// (batch_presence_core() in descent_probe.c), and every bit in the
    /// result is a real, positive confirmation for exactly the ids passed
    /// in. The round's tier rides along as the parallel key array so the
    /// probe prunes entities' LIST(tier) partitions to one index descent
    /// per id instead of one per leaf. The caller
    /// (TierTreeDescent.ProbeBatchEmitBitmapsAsync) is responsible for
    /// filtering which ids to check each round and for only calling
    /// MarkProven with the subset this round's bitmap actually confirmed
    /// present.
    /// </summary>
    public Task<byte[]> TierBatchExistenceProbeAsync(
        IReadOnlyList<Hash128> ids, short tier, CancellationToken ct = default) =>
        _tierProbes.ProbeAsync(ids, tier, ct);

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

        await using var cmd = _ds.CreateCommand("SELECT laplace.tier_batch_existence_probe($1, $2)");
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var pt = cmd.Parameters.AddWithValue(tiers);
        pt.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint;
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

    public async Task<IReadOnlyList<PartitionPressure>> PartitionPressureAsync(
        long minRows, CancellationToken ct = default)
    {
        // The scan lives in consensus_partition_pressure() — one implementation of the
        // fact, on the layer that owns partition layout. An install predating the
        // function degrades to "nothing to report" rather than sinking a finished run.
        await using var cmd = _ds.CreateCommand(
            "SELECT relation, rows, pct_of_default FROM ops.consensus_partition_pressure($1) "
            + "WHERE tbl = 'consensus' ORDER BY rows DESC");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, minRows);
        try
        {
            var outv = new List<PartitionPressure>();
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                outv.Add(new PartitionPressure(
                    rdr.GetString(0), rdr.GetInt64(1), (double)rdr.GetDecimal(2)));
            return outv;
        }
        catch (PostgresException)
        {
            return Array.Empty<PartitionPressure>();
        }
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
