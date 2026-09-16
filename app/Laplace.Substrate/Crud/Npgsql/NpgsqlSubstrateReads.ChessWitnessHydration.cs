using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    /// <summary>
    /// One admitted materialization envelope. Charges encoded inputs and conservative
    /// managed/native row and replay reservations before their materialization.
    /// This is an input/work allowance, not a measurement of process RSS.
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
        byte[] Subject, byte[] Type, byte[]? Object, byte[]? Context);
    public readonly record struct ChessContentVertexRow(
        byte[] Parent, int Ordinal, byte[] Child, int RunLength);

    /// <summary>Selected recorded sources, subjects and, for headers, playing contexts.</summary>
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
            // Selected subject/type predicates and bounded object/context projections
            // constrain transport before Npgsql can allocate a malformed large field.
            // Null objects/non-null invalid line contexts are rejected by the caller.
            budget.Reserve(256);
            result.Add(new((byte[])reader[0], (byte[])reader[1],
                reader.IsDBNull(2) ? null : (byte[])reader[2],
                reader.IsDBNull(3) ? null : (byte[])reader[3]));
        }
        return result;
    }

    /// <summary>
    /// Admit stored geometry bytes/vertices before the native compressed-trajectory
    /// reader runs; then reserve actual expanded RLE work before any expanded read
    /// or chess replay. Source n_constituents metadata is not trusted as the count.
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
                // Geometry decoding may retain the serialized value and native XYZM
                // buffer together. Stored-vertex result rows need their own allowance.
                budget.Reserve(checked(bytes * 2));
                budget.Reserve(checked(points * 256));
                vertices = checked(vertices + points);
            }
        }

        var result = new List<ChessContentVertexRow>();
        // Reuse the native set-sized carrier owner for exactly the preflighted
        // physicalities. It validates canonical entity/Content identity bindings;
        // unrelated alternate physicalities cannot replace an admitted carrier.
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

    /// <summary>Render only a preflighted finite canonical text graph.</summary>
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
