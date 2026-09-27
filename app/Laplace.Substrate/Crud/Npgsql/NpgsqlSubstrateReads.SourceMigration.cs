using System.Data;
using Laplace.Engine.Core;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public static async Task<IReadOnlyList<byte[]>> SourceObservationContextsAsync(
        NpgsqlDataSource ds, byte[] source, byte[] type, byte[] value,
        byte[][] selected, CancellationToken ct)
    {
        await using var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("source.observation_contexts"),
            parameters =>
            {
                parameters.AddWithValue(source);
                parameters.AddWithValue(type);
                parameters.AddWithValue(value);
                parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea, selected);
            });
        var result = new List<byte[]>(selected.Length);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add((byte[])reader[0]);
        return result;
    }

    public sealed record SourceObservationInventory(
        long SourceRows, long NullContextOutcomeRows, long ContextOutcomeRows,
        long OutcomeObservations, long OutcomeContexts, long MarkerRows, string Database, string ServerVersion);

    /// <summary>Read-only counts of a calculated source's testimony, outcome rows and markers,
    /// taken before its observation recipe is replaced.</summary>
    public static async Task<SourceObservationInventory> SourceObservationInventoryAsync(
        NpgsqlDataSource ds, byte[] source, byte[] outcomeType, byte[] outcomeObject,
        byte[] markerType, CancellationToken ct = default)
    {
        await using var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("source.observation_inventory"),
            parameters =>
            {
                parameters.AddWithValue(source);
                parameters.AddWithValue(outcomeType);
                parameters.AddWithValue(outcomeObject);
                parameters.AddWithValue(markerType);
            });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("Missing source inventory.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7));
    }

    /// <summary>
    /// Streams the source's complete typed rows, serialized as JSON by PostgreSQL, to
    /// <paramref name="destination"/> before eviction changes its testimony, standing,
    /// markers or receipts. One snapshot, sequential access, capped at
    /// <paramref name="maximumBytes"/>.
    /// </summary>
    public static async Task<long> RetainSourceMigrationAsync(
        NpgsqlDataSource ds, byte[] source, byte[] markerType, Stream destination,
        long maximumBytes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        await using var command = NpgsqlRead.CreateCommand(ds, SqlCatalog.Get("source.migration_retention"),
            parameters =>
            {
                parameters.AddWithValue(source);
                parameters.AddWithValue(markerType);
            });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        var buffer = new byte[65536];
        long bytes = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            await using var row = reader.GetStream(0);
            int count;
            while ((count = await row.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (count > maximumBytes - bytes)
                    throw new InvalidDataException("Source migration retention byte envelope exhausted before eviction.");
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                bytes += count;
            }
        }
        return bytes;
    }
}
