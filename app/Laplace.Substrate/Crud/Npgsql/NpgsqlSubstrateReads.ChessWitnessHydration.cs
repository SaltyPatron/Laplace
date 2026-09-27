using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    /// <summary>
    /// Byte allowance for one hydration of recorded playings. Encoded inputs, row
    /// transport and replay work are reserved against it before they are materialized;
    /// exceeding it throws. It bounds work, it does not measure process memory.
    /// </summary>
    public sealed class ChessWitnessReadBudget(long maximumBytes)
    {
        public long Remaining { get; private set; } = maximumBytes > 0 ? maximumBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        public void Reserve(long bytes)
        {
            if (bytes < 0 || bytes > Remaining)
                throw new InvalidDataException("Chess playing hydration exceeds its admitted materialization envelope.");
            Remaining -= bytes;
        }
        internal int RowLimit => checked((int)Math.Min(int.MaxValue - 1, Remaining / 256));
    }

    public readonly record struct ChessWitnessInputRow(
        byte[] Subject, byte[] Type, byte[]? Object, byte[]? Context, byte[]? Qualifiers = null);
    public readonly record struct ChessContentVertexRow(
        byte[] Parent, int Ordinal, byte[] Child, int RunLength);

    /// <summary>Recorded testimony for the selected subjects, types and sources (and, when
    /// given, contexts), capped by the budget's row limit.</summary>
    public static async Task<IReadOnlyList<ChessWitnessInputRow>> ChessWitnessInputsAsync(
        NpgsqlDataSource ds, byte[][] subjects, byte[][] types, byte[][] sources,
        byte[][]? contexts, ChessWitnessReadBudget budget, CancellationToken ct)
    {
        int limit = budget.RowLimit;
        await using var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("chess.witness_inputs"),
            parameters =>
            {
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, subjects);
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, types);
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, sources);
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, contexts is null ? DBNull.Value : contexts);
                parameters.AddWithValue(limit + 1);
            });
        var result = new List<ChessWitnessInputRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (result.Count == limit)
                throw new InvalidDataException("Chess playing header fanout exceeds its admitted materialization envelope.");
            // The catalog statement bounds object/context projections server-side, so
            // each row fits its fixed reservation. The caller validates object and
            // context shapes.
            budget.Reserve(256);
            result.Add(new((byte[])reader[0], (byte[])reader[1],
                reader.IsDBNull(2) ? null : (byte[])reader[2],
                reader.IsDBNull(3) ? null : (byte[])reader[3],
                reader.IsDBNull(4) ? null : (byte[])reader[4]));
        }
        return result;
    }

    /// <summary>
    /// Reads the Content trajectory vertices of <paramref name="ids"/> in two passes: a
    /// preflight reserves the stored geometry bytes and vertex counts, then the carrier
    /// read reserves each vertex's expanded run before it is returned. The vertex count
    /// comes from the stored geometry, not from n_constituents.
    /// </summary>
    public static async Task<IReadOnlyList<ChessContentVertexRow>> ChessContentShapeAsync(
        NpgsqlDataSource ds, byte[][] ids, ChessWitnessReadBudget budget,
        int bytesPerExpandedConstituent, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerExpandedConstituent, 256);
        int limit = budget.RowLimit;
        var owners = new HashSet<string>(StringComparer.Ordinal);
        long vertices = 0;
        var selectedEntities = new List<byte[]>();
        var selectedPhysicalities = new List<byte[]>();
        await using (var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("chess.content_preflight"),
            parameters =>
            {
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
                parameters.AddWithValue(limit + 1);
            }))
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (owners.Count == limit)
                    throw new InvalidDataException("Chess playing physicality fanout exceeds its admitted materialization envelope.");
                budget.Reserve(256);
                if (!owners.Add(Convert.ToHexString((byte[])reader[0])))
                    throw new InvalidDataException("Chess playing input has competing Content physicalities.");
                selectedEntities.Add((byte[])reader[0]);
                selectedPhysicalities.Add((byte[])reader[1]);
                long bytes = reader.GetInt64(2), points = reader.GetInt64(3);
                // Decoding may hold the serialized geometry and the native XYZM buffer
                // at once; each stored vertex row is reserved separately.
                budget.Reserve(checked(bytes * 2));
                budget.Reserve(checked(points * 256));
                vertices = checked(vertices + points);
            }
        }

        var result = new List<ChessContentVertexRow>();
        // Carrier vertices for exactly the preflighted (entity, physicality) pairs; the
        // native reader checks each Content physicality against its entity id, so a
        // different physicality cannot stand in for the preflighted one.
        await using (var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("content.carrier_vertices_selected"),
            parameters =>
            {
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, selectedEntities.ToArray());
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, selectedPhysicalities.ToArray());
            }))
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (result.Count >= vertices)
                    throw new InvalidDataException("Chess playing Content changed after its geometry preflight.");
                int run = checked((int)reader.GetInt64(4));
                if (run <= 0) throw new InvalidDataException("Chess playing Content has an invalid constituent run.");
                budget.Reserve(checked((long)run * bytesPerExpandedConstituent));
                result.Add(new((byte[])reader[0], checked((int)reader.GetInt64(2)), (byte[])reader[3], run));
            }
        }
        if (result.Count != vertices)
            throw new InvalidDataException("Chess playing Content changed after its geometry preflight.");
        return result;
    }

    /// <summary>Realizes the given result ids as text, bounded by <paramref name="maximumDepth"/>.</summary>
    public static async Task<string[]?> RenderPreflightedChessResultsAsync(
        NpgsqlDataSource ds, byte[][] ids, int maximumDepth, CancellationToken ct)
    {
        await using var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("chess.render_preflighted_results"),
            parameters =>
            {
                parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ids);
                parameters.AddWithValue(maximumDepth);
            });
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string[];
    }
}
