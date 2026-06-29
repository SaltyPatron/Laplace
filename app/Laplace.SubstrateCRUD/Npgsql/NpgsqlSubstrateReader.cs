using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class NpgsqlSubstrateReader : ISubstrateReader
{
    private readonly NpgsqlDataSource _ds;

    public NpgsqlSubstrateReader(NpgsqlDataSource dataSource)
        => _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT laplace.evidence_count(p_type => laplace.canonical_id($1)) > 0");
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
            "SELECT laplace.evidence_count(p_type => laplace.canonical_id($1), p_source => $2) > 0");
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

    public async Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE type_id = $1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, typeId.ToBytes());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
    }

    // Session "proven present" set. Content is content-addressed and IMMUTABLE, so a proven id is
    // present forever — never stale, never rebuilt. This is the seen-set/perfcache: a re-emitted trunk
    // (re-quoted verse, repeated word, pwn15/16/17 re-ref) is a hit here and never re-probes the DB nor
    // re-stages. Thread-safe because the reader is shared across parallel compose workers; correctness
    // does not depend on it (merge skipped counts instrument unexpected conflicts), so a racy
    // double-add is harmless.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte> _proven = new();

    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        int n = candidates.Count;
        var bm = new byte[(n + 7) / 8];
        if (n == 0) return bm;

        // Seen-set first: proven ids are present by construction — no DB probe. Only the UNKNOWNS hit
        // the DB. This is what drives the re-emission tax to zero round-trips.
        var unknownIdx = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (_proven.ContainsKey(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            else unknownIdx.Add(i);
        }
        if (unknownIdx.Count == 0) return bm;   // everything already proven — zero DB work

        var byteaArray = new byte[unknownIdx.Count][];
        for (int u = 0; u < unknownIdx.Count; u++) byteaArray[u] = candidates[unknownIdx[u]].ToBytes();

        await using var cmd = _ds.CreateCommand("SELECT laplace.entities_exist_bitmap($1)");
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var result = await cmd.ExecuteScalarAsync(ct);
        var dbBm = result as byte[] ?? Array.Empty<byte>();
        long dbBits = (long)dbBm.Length * 8;

        // Map the unknowns' DB results back to the candidate bitmap; cache the present ones (immutable).
        for (int u = 0; u < unknownIdx.Count; u++)
        {
            if (u < dbBits && (dbBm[u >> 3] & (1 << (u & 7))) != 0)
            {
                int i = unknownIdx[u];
                bm[i >> 3] |= (byte)(1 << (i & 7));
                _proven.TryAdd(candidates[i], 1);
            }
        }
        return bm;
    }

    public void MarkProven(IReadOnlyList<Hash128> ids)
    {
        if (ids is null) return;
        for (int i = 0; i < ids.Count; i++) _proven.TryAdd(ids[i], 1);
    }

    // Canonical-hash → natural-unit-root cache. compose IS the dedup: a canonical already composed this
    // session has a known, immutable content-address, so the expensive BuildContentTree (BLAKE3 +
    // geometry per node) is skipped on re-occurrence — the occurrence just attests via the cached root.
    // Session-scoped, thread-safe across the parallel compose workers.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, Hash128> _rootCache = new();
    public bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) => _rootCache.TryGetValue(canonicalKey, out rootId);
    public void CacheRoot(Hash128 canonicalKey, Hash128 rootId) => _rootCache.TryAdd(canonicalKey, rootId);

    /// <summary>
    /// Top-down O(tier) containment probe — the tree-aware sibling of
    /// <see cref="EntitiesExistBitmapAsync"/>, returning the identical present-bitmap contract (bit k
    /// set ⟺ candidate k present, LSB-first). <paramref name="ids"/> are the tier&gt;=2 node ids;
    /// <paramref name="parents"/>[k] is the 0-based index of node k's parent (&lt; 0 = root). The
    /// native descent only checks nodes under a novel parent — a present trunk short-circuits its
    /// whole subtree — so re-emitted content costs O(tier-depth) DB checks, not O(nodes). The caller's
    /// existing bit-walk and the native content emit consume this unchanged.
    /// </summary>
    public async Task<byte[]> ContentDescentBitmapAsync(
        IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        if (parents is null) throw new ArgumentNullException(nameof(parents));
        if (ids.Count != parents.Count)
            throw new ArgumentException("ids and parents must be the same length");
        if (ids.Count == 0) return Array.Empty<byte>();

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
            null         => Array.Empty<byte>(),
            _            => throw new InvalidOperationException(
                $"content_descent_bitmap returned unexpected type: {result.GetType()}")
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
            "SELECT subject_id, object_id, type_id, eff_mu, witnesses FROM laplace.classify_circuit($1)");
        var p = cmd.Parameters.AddWithValue(packed);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;

        var rows = new List<CircuitRelation>(pairs.Count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var subj = Hash128.FromBytes((byte[])rdr[0]);
            var obj  = Hash128.FromBytes((byte[])rdr[1]);
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
        var obj  = new byte[pairs.Count][];
        for (int i = 0; i < pairs.Count; i++)
        {
            subj[i] = pairs[i].Subject.ToBytes();
            obj[i]  = pairs[i].Object.ToBytes();
        }

        // Left-join each ordered pair to its consensus row via consensus_id(subject,type,object);
        // eff_mu_display is the human-scale (rating-2·rd)/1e9. COALESCE→0 for absent/unfolded edges.
        await using var cmd = _ds.CreateCommand(@"
            SELECT COALESCE(laplace.eff_mu_display(c.rating, c.rd), 0)::float8
            FROM unnest($1::bytea[]) WITH ORDINALITY AS s(sid, ord)
            JOIN unnest($2::bytea[]) WITH ORDINALITY AS o(oid, ord) USING (ord)
            LEFT JOIN laplace.consensus c ON c.id = laplace.consensus_id(s.sid, $3, o.oid)
            ORDER BY s.ord");
        var p1 = cmd.Parameters.AddWithValue(subj); p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p2 = cmd.Parameters.AddWithValue(obj);  p2.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p3 = cmd.Parameters.AddWithValue(typeId.ToBytes()); p3.NpgsqlDbType = NpgsqlDbType.Bytea;

        var outv = new List<double>(pairs.Count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) outv.Add(rdr.GetDouble(0));
        return outv;
    }
}
