namespace Laplace.Pipeline;

using System.Threading;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="IEntityChildEmission"/>. Phase 2 / Track D / D6.
/// </summary>
public sealed class EntityChildEmission : IEntityChildEmission, IAsyncDisposable
{
    private const string StagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_staging (" +
            "parent_hash bytea, parent_tier smallint, position integer, " +
            "child_hash bytea, child_tier smallint, rle_count integer" +
        ") ON COMMIT PRESERVE ROWS";

    private const string CopyCommand =
        "COPY pg_temp.laplace_staging (parent_hash, parent_tier, position, child_hash, child_tier, rle_count) " +
        "FROM STDIN BINARY";

    private const string InsertSelect =
        "INSERT INTO entity_child (parent_hash, parent_tier, position, child_hash, child_tier, rle_count) " +
        "SELECT parent_hash, parent_tier, position, child_hash, child_tier, rle_count FROM pg_temp.laplace_staging " +
        "ON CONFLICT (parent_hash, parent_tier, position) DO NOTHING";

    private readonly PgCopyBatchSink<EntityChildRecord> _sink;

    public EntityChildEmission(NpgsqlConnection connection)
    {
        _sink = new PgCopyBatchSink<EntityChildRecord>(
            connection, StagingDdl, CopyCommand, InsertSelect, WriteRowAsync);
    }

    public ValueTask EmitAsync(EntityChildRecord record, CancellationToken cancellationToken) =>
        _sink.EmitAsync(record, cancellationToken);

    public ValueTask DisposeAsync() => _sink.DisposeAsync();

    private static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, EntityChildRecord record)
    {
        await importer.WriteAsync(record.ParentHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false); // parent_tier — caller-known; record needs extending later
        await importer.WriteAsync(record.Ordinal, NpgsqlDbType.Integer).ConfigureAwait(false);
        await importer.WriteAsync(record.ChildHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false); // child_tier — same
        await importer.WriteAsync(record.RleCount, NpgsqlDbType.Integer).ConfigureAwait(false);
    }
}
