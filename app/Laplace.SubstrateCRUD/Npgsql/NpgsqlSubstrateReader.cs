using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Npgsql-backed <see cref="ISubstrateReader"/> implementation. Read-only
/// view used by IngestRunner (per ADR 0052) for layer-ordering checks +
/// by IDecomposer.InitializeAsync for bootstrap verification.
/// </summary>
public sealed class NpgsqlSubstrateReader : ISubstrateReader
{
    private readonly NpgsqlDataSource _ds;

    public NpgsqlSubstrateReader(NpgsqlDataSource dataSource)
        => _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc/>
    /// <remarks>
    /// MVP impl uses an "any entities of the substrate-canonical Source
    /// type exist with a recorded layer-order meta-attestation" probe.
    /// The full ADR 0037 layer-completion semantics (per-source HAS_LAYER
    /// meta-attestation set at end of decomposer run) lands with the
    /// concrete decomposer story (#183 Unicode first); for now a thin
    /// probe satisfies the interface so IngestRunner can compile.
    /// </remarks>
    public async Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM laplace.attestations a "
          + "JOIN laplace.entities e ON e.id = a.subject_id "
          + "WHERE a.kind_id = public.laplace_hash128_blake3($1::bytea))");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea,
            System.Text.Encoding.UTF8.GetBytes($"substrate/kind/HasLayerCompleted/{layerOrder}/v1"));
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is bool b && b;
        }
        catch (PostgresException)
        {
            // Function or kind not yet present (pre-bootstrap or test DB) — treat
            // as "not completed" rather than throwing. Concrete bootstrap lands
            // in #183.
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE type_id = $1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, typeId.ToBytes());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0L;
    }

    /// <inheritdoc/>
    public async Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (candidates.Count == 0) return Array.Empty<byte>();

        // Marshal candidates to a bytea[] for the SRF call.
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
