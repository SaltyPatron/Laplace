namespace Laplace.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="IEntityEmission"/> over <see cref="PgCopyBatchSink{T}"/>.
/// Phase 2 / Track D / D6.
/// </summary>
public sealed class EntityEmission : IEntityEmission, IAsyncDisposable
{
    private const string StagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_staging (" +
            "entity_hash bytea, tier smallint, codepoint integer, content bytea, " +
            "centroid_4d point4d, prime_flags bigint, structural_flags smallint" +
        ") ON COMMIT PRESERVE ROWS";

    private const string CopyCommand =
        "COPY pg_temp.laplace_staging (entity_hash, tier, codepoint, content, " +
        "centroid_4d, prime_flags, structural_flags) FROM STDIN BINARY";

    private const string InsertSelect =
        "INSERT INTO entity (entity_hash, tier, codepoint, content, centroid_4d, prime_flags, structural_flags) " +
        "SELECT entity_hash, tier, codepoint, content, centroid_4d, prime_flags, structural_flags FROM pg_temp.laplace_staging " +
        "ON CONFLICT (entity_hash, tier) DO NOTHING";

    private readonly PgCopyBatchSink<EntityRecord> _sink;

    public EntityEmission(NpgsqlConnection connection)
    {
        _sink = new PgCopyBatchSink<EntityRecord>(
            connection, StagingDdl, CopyCommand, InsertSelect, WriteRowAsync);
    }

    public ValueTask EmitAsync(EntityRecord record, CancellationToken cancellationToken) =>
        _sink.EmitAsync(record, cancellationToken);

    public ValueTask DisposeAsync() => _sink.DisposeAsync();

    private static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, EntityRecord record)
    {
        await importer.WriteAsync(record.Hash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.Tier, NpgsqlDbType.Smallint).ConfigureAwait(false);
        await importer.WriteNullAsync().ConfigureAwait(false); // codepoint — set by tier-0 specific path
        await importer.WriteAsync(record.Content ?? Array.Empty<byte>(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        // POINT4D wire format: 4 doubles in network byte order (matches point4d_send).
        var centroidBytes = new byte[32];
        WriteDoubleBigEndian(centroidBytes,  0, record.Centroid.X);
        WriteDoubleBigEndian(centroidBytes,  8, record.Centroid.Y);
        WriteDoubleBigEndian(centroidBytes, 16, record.Centroid.Z);
        WriteDoubleBigEndian(centroidBytes, 24, record.Centroid.W);
        await importer.WriteAsync(centroidBytes, NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(0L, NpgsqlDbType.Bigint).ConfigureAwait(false);  // prime_flags — set by attestation
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false);  // structural_flags
    }

    private static void WriteDoubleBigEndian(byte[] buffer, int offset, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        for (int i = 7; i >= 0; --i)
        {
            buffer[offset + i] = (byte)(bits & 0xFF);
            bits >>= 8;
        }
    }
}
