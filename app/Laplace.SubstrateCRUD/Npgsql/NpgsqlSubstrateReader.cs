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
}
