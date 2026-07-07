using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed class NpgsqlSubstrateReader : ISubstrateReader
{
    private readonly NpgsqlDataSource _ds;

    public NpgsqlDataSource DataSource => _ds;

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
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte> _proven = new();

    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        int n = candidates.Count;
        var bm = new byte[(n + 7) / 8];
        if (n == 0) return bm;



        var unknownIdx = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (_proven.ContainsKey(candidates[i])) bm[i >> 3] |= (byte)(1 << (i & 7));
            else unknownIdx.Add(i);
        }
        if (unknownIdx.Count == 0) return bm;


        var dbUnknownIdx = new List<int>(unknownIdx.Count);
        for (int u = 0; u < unknownIdx.Count; u++)
        {
            int i = unknownIdx[u];
            if (CodepointPerfcache.IsKnownCodepointId(candidates[i]))
            {
                bm[i >> 3] |= (byte)(1 << (i & 7));
                _proven.TryAdd(candidates[i], 1);
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
        long dbBits = (long)dbBm.Length * 8;


        for (int u = 0; u < dbUnknownIdx.Count; u++)
        {
            if (u < dbBits && (dbBm[u >> 3] & (1 << (u & 7))) != 0)
            {
                int i = dbUnknownIdx[u];
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

    public bool IsProvenPresent(Hash128 id) => _proven.ContainsKey(id);





    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, Hash128> _rootCache = new();
    public bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) => _rootCache.TryGetValue(canonicalKey, out rootId);
    public void CacheRoot(Hash128 canonicalKey, Hash128 rootId) => _rootCache.TryAdd(canonicalKey, rootId);

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
            var allBm = new byte[(ids.Count + 7) / 8];
            for (int i = 0; i < ids.Count; i++)
                allBm[i >> 3] |= (byte)(1 << (i & 7));
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
    /// Calls the native tier_batch_existence_probe() SQL function directly
    /// -- no C#-side caching layer here, deliberately: the native side
    /// already does its own perfcache fast-path internally
    /// (batch_presence_core() in descent_probe.c), and every bit in the
    /// result is a real, positive confirmation for exactly the ids passed
    /// in. The caller (TierTreeDescent.ProbeBatchEmitBitmapsAsync) is
    /// responsible for filtering which ids to check each round and for
    /// only calling MarkProven with the subset this round's bitmap actually
    /// confirmed present.
    /// </summary>
    public async Task<byte[]> TierBatchExistenceProbeAsync(IReadOnlyList<Hash128> ids, CancellationToken ct = default)
    {
        if (ids is null) throw new ArgumentNullException(nameof(ids));
        int n = ids.Count;
        if (n == 0) return Array.Empty<byte>();

        var byteaArray = new byte[n][];
        for (int i = 0; i < n; i++) byteaArray[i] = ids[i].ToBytes();

        await using var cmd = _ds.CreateCommand("SELECT laplace.tier_batch_existence_probe($1)");
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
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
            "SELECT subject_id, object_id, type_id, eff_mu, witnesses FROM laplace.classify_circuit($1)");
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



        await using var cmd = _ds.CreateCommand(@"
            SELECT COALESCE(laplace.eff_mu_display(c.rating, c.rd), 0)::float8
            FROM unnest($1::bytea[]) WITH ORDINALITY AS s(sid, ord)
            JOIN unnest($2::bytea[]) WITH ORDINALITY AS o(oid, ord) USING (ord)
            LEFT JOIN laplace.consensus c ON c.id = laplace.consensus_id(s.sid, $3, o.oid)
            ORDER BY s.ord");
        var p1 = cmd.Parameters.AddWithValue(subj); p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p2 = cmd.Parameters.AddWithValue(obj); p2.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var p3 = cmd.Parameters.AddWithValue(typeId.ToBytes()); p3.NpgsqlDbType = NpgsqlDbType.Bytea;

        var outv = new List<double>(pairs.Count);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) outv.Add(rdr.GetDouble(0));
        return outv;
    }
}
