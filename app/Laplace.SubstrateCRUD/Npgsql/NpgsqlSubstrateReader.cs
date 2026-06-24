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

    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (candidates.Count == 0) return Array.Empty<byte>();

        var byteaArray = new byte[candidates.Count][];
        for (int i = 0; i < candidates.Count; i++) byteaArray[i] = candidates[i].ToBytes();

        await using var cmd = _ds.CreateCommand(
            "SELECT laplace.entities_exist_bitmap($1)");
        var p = cmd.Parameters.AddWithValue(byteaArray);
        p.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result switch
        {
            byte[] bytes => bytes,
            null         => Array.Empty<byte>(),
            _            => throw new InvalidOperationException(
                $"entities_exist_bitmap returned unexpected type: {result.GetType()}")
        };
    }

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
}
