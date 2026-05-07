namespace Laplace.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="IPhysicalityEmission"/>. Phase 2 / Track D / D6.
/// </summary>
public sealed class PhysicalityEmission : IPhysicalityEmission, IAsyncDisposable
{
    private const string StagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_staging (" +
            "physicality_type_hash bytea, entity_hash bytea, entity_tier smallint, " +
            "position point4d, hilbert_index bigint" +
        ") ON COMMIT PRESERVE ROWS";

    private const string CopyCommand =
        "COPY pg_temp.laplace_staging (physicality_type_hash, entity_hash, entity_tier, position, hilbert_index) " +
        "FROM STDIN BINARY";

    private const string InsertSelect =
        "INSERT INTO physicality (physicality_type_hash, entity_hash, entity_tier, position, hilbert_index) " +
        "SELECT physicality_type_hash, entity_hash, entity_tier, position, hilbert_index FROM pg_temp.laplace_staging " +
        "ON CONFLICT (physicality_type_hash, entity_hash, entity_tier) DO NOTHING";

    private readonly PgCopyBatchSink<PhysicalityRecord> _sink;

    public PhysicalityEmission(NpgsqlConnection connection)
    {
        _sink = new PgCopyBatchSink<PhysicalityRecord>(
            connection, StagingDdl, CopyCommand, InsertSelect, WriteRowAsync);
    }

    public ValueTask EmitAsync(PhysicalityRecord record, CancellationToken cancellationToken) =>
        _sink.EmitAsync(record, cancellationToken);

    public ValueTask DisposeAsync() => _sink.DisposeAsync();

    private static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, PhysicalityRecord record)
    {
        await importer.WriteAsync(record.PhysicalityTypeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.EntityHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false);
        // POINT4D send format: 4 doubles network byte order.
        var positionBytes = new byte[32];
        var p = record.Geometry.Length > 0 ? record.Geometry[0] : new Core.Abstractions.Point4D(0, 0, 0, 1);
        WriteDoubleBE(positionBytes,  0, p.X);
        WriteDoubleBE(positionBytes,  8, p.Y);
        WriteDoubleBE(positionBytes, 16, p.Z);
        WriteDoubleBE(positionBytes, 24, p.W);
        await importer.WriteAsync(positionBytes, NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(0L, NpgsqlDbType.Bigint).ConfigureAwait(false);
    }

    private static void WriteDoubleBE(byte[] buffer, int offset, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        for (int i = 7; i >= 0; --i)
        {
            buffer[offset + i] = (byte)(bits & 0xFF);
            bits >>= 8;
        }
    }
}
