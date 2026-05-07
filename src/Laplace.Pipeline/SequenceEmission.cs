namespace Laplace.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="ISequenceEmission"/>. Phase 2 / Track D / D6.
/// </summary>
public sealed class SequenceEmission : ISequenceEmission, IAsyncDisposable
{
    private const string StagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_staging (" +
            "parent_hash bytea, parent_tier smallint, position integer, " +
            "child_hash bytea, child_tier smallint" +
        ") ON COMMIT PRESERVE ROWS";

    private const string CopyCommand =
        "COPY pg_temp.laplace_staging (parent_hash, parent_tier, position, child_hash, child_tier) " +
        "FROM STDIN BINARY";

    private const string InsertSelect =
        "INSERT INTO sequence (parent_hash, parent_tier, position, child_hash, child_tier) " +
        "SELECT parent_hash, parent_tier, position, child_hash, child_tier FROM pg_temp.laplace_staging " +
        "ON CONFLICT (parent_hash, parent_tier, position) DO NOTHING";

    private readonly PgCopyBatchSink<SequenceRecord> _sink;

    public SequenceEmission(NpgsqlConnection connection)
    {
        _sink = new PgCopyBatchSink<SequenceRecord>(
            connection, StagingDdl, CopyCommand, InsertSelect, WriteRowAsync);
    }

    public ValueTask EmitAsync(SequenceRecord record, CancellationToken cancellationToken) =>
        _sink.EmitAsync(record, cancellationToken);

    public ValueTask DisposeAsync() => _sink.DisposeAsync();

    private static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, SequenceRecord record)
    {
        await importer.WriteAsync(record.DocumentHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false);
        await importer.WriteAsync((int)record.LeafPosition, NpgsqlDbType.Integer).ConfigureAwait(false);
        await importer.WriteAsync(record.LeafAtomHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false);
    }
}
