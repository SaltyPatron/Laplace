namespace Laplace.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="IProvenance"/>. Resolves source entities (sources are
/// substrate entities — content-addressed, NOT integer source_ids), and
/// emits per-entity / per-edge provenance edges via PgCopyBatchSink.
/// Phase 2 / Track D / D7.
/// </summary>
public sealed class Provenance : IProvenance, IAsyncDisposable
{
    private const string EntityProvStagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_entityprov_staging (" +
            "entity_hash bytea, entity_tier smallint, provenance_hash bytea" +
        ") ON COMMIT PRESERVE ROWS";

    private const string EntityProvCopyCommand =
        "COPY pg_temp.laplace_entityprov_staging (entity_hash, entity_tier, provenance_hash) FROM STDIN BINARY";

    private const string EntityProvInsertSelect =
        "INSERT INTO entity_provenance (entity_hash, entity_tier, provenance_hash) " +
        "SELECT entity_hash, entity_tier, provenance_hash FROM pg_temp.laplace_entityprov_staging " +
        "ON CONFLICT (entity_hash, entity_tier, provenance_hash) DO NOTHING";

    private const string EdgeProvStagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_edgeprov_staging (" +
            "edge_hash bytea, edge_type_hash bytea, provenance_hash bytea" +
        ") ON COMMIT PRESERVE ROWS";

    private const string EdgeProvCopyCommand =
        "COPY pg_temp.laplace_edgeprov_staging (edge_hash, edge_type_hash, provenance_hash) FROM STDIN BINARY";

    private const string EdgeProvInsertSelect =
        "INSERT INTO edge_provenance (edge_hash, edge_type_hash, provenance_hash) " +
        "SELECT edge_hash, edge_type_hash, provenance_hash FROM pg_temp.laplace_edgeprov_staging " +
        "ON CONFLICT (edge_hash, edge_type_hash, provenance_hash) DO NOTHING";

    private readonly IIdentityHashing                  _hashing;
    private readonly ConcurrentDictionary<string, AtomId> _sourceCache = new(StringComparer.Ordinal);
    private readonly PgCopyBatchSink<EntityProvenanceRecord> _entitySink;
    private readonly PgCopyBatchSink<EdgeProvenanceRecord>   _edgeSink;

    public Provenance(IIdentityHashing hashing, NpgsqlConnection entityConn, NpgsqlConnection edgeConn)
    {
        _hashing    = hashing;
        _entitySink = new PgCopyBatchSink<EntityProvenanceRecord>(
            entityConn, EntityProvStagingDdl, EntityProvCopyCommand, EntityProvInsertSelect, WriteEntityProv);
        _edgeSink   = new PgCopyBatchSink<EdgeProvenanceRecord>(
            edgeConn, EdgeProvStagingDdl, EdgeProvCopyCommand, EdgeProvInsertSelect, WriteEdgeProv);
    }

    public Task<AtomId> ResolveSourceAsync(string canonicalName, CancellationToken cancellationToken)
    {
        if (_sourceCache.TryGetValue(canonicalName, out var hash))
        {
            return Task.FromResult(hash);
        }
        // The canonical source identity is BLAKE3 of the UTF-8 bytes of the
        // source's canonical name (e.g., "iso_639_3_registry"). Equivalent
        // to a tier-1 atom whose content IS the canonical name string;
        // sources thus get one entity hash regardless of who references them.
        var bytes = Encoding.UTF8.GetBytes(canonicalName);
        hash = _hashing.AtomId(bytes);
        _sourceCache[canonicalName] = hash;
        return Task.FromResult(hash);
    }

    public ValueTask EmitEntityProvenanceAsync(EntityProvenanceRecord record, CancellationToken cancellationToken) =>
        _entitySink.EmitAsync(record, cancellationToken);

    public ValueTask EmitEdgeProvenanceAsync(EdgeProvenanceRecord record, CancellationToken cancellationToken) =>
        _edgeSink.EmitAsync(record, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _entitySink.DisposeAsync().ConfigureAwait(false);
        await _edgeSink.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteEntityProv(NpgsqlBinaryImporter importer, EntityProvenanceRecord record)
    {
        await importer.WriteAsync(record.EntityHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)0, NpgsqlDbType.Smallint).ConfigureAwait(false);
        await importer.WriteAsync(record.SourceHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
    }

    private static async ValueTask WriteEdgeProv(NpgsqlBinaryImporter importer, EdgeProvenanceRecord record)
    {
        await importer.WriteAsync(record.EdgeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.EdgeTypeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.SourceHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
    }
}
