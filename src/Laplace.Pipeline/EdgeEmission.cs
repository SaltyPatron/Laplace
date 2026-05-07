namespace Laplace.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Concrete <see cref="IEdgeEmission"/>. Two underlying sinks (one for
/// edge rows, one for edge_member rows). Phase 2 / Track D / D6.
/// </summary>
public sealed class EdgeEmission : IEdgeEmission, IAsyncDisposable
{
    private const string EdgeStagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_edge_staging (" +
            "edge_hash bytea, edge_type_hash bytea, member_count smallint" +
        ") ON COMMIT PRESERVE ROWS";

    private const string EdgeCopyCommand =
        "COPY pg_temp.laplace_edge_staging (edge_hash, edge_type_hash, member_count) FROM STDIN BINARY";

    private const string EdgeInsertSelect =
        "INSERT INTO edge (edge_hash, edge_type_hash, member_count) " +
        "SELECT edge_hash, edge_type_hash, member_count FROM pg_temp.laplace_edge_staging " +
        "ON CONFLICT (edge_hash, edge_type_hash) DO NOTHING";

    private const string MemberStagingDdl =
        "CREATE TEMP TABLE IF NOT EXISTS pg_temp.laplace_member_staging (" +
            "edge_hash bytea, edge_type_hash bytea, role_hash bytea, " +
            "role_position smallint, participant_hash bytea" +
        ") ON COMMIT PRESERVE ROWS";

    private const string MemberCopyCommand =
        "COPY pg_temp.laplace_member_staging (edge_hash, edge_type_hash, role_hash, role_position, participant_hash) " +
        "FROM STDIN BINARY";

    private const string MemberInsertSelect =
        "INSERT INTO edge_member (edge_hash, edge_type_hash, role_hash, role_position, participant_hash) " +
        "SELECT edge_hash, edge_type_hash, role_hash, role_position, participant_hash FROM pg_temp.laplace_member_staging " +
        "ON CONFLICT (edge_hash, edge_type_hash, role_hash, role_position) DO NOTHING";

    private readonly PgCopyBatchSink<EdgeRecord>       _edgeSink;
    private readonly PgCopyBatchSink<EdgeMemberRecord> _memberSink;

    public EdgeEmission(NpgsqlConnection edgeConnection, NpgsqlConnection memberConnection)
    {
        _edgeSink   = new PgCopyBatchSink<EdgeRecord>(
            edgeConnection,   EdgeStagingDdl,   EdgeCopyCommand,   EdgeInsertSelect,   WriteEdgeAsync);
        _memberSink = new PgCopyBatchSink<EdgeMemberRecord>(
            memberConnection, MemberStagingDdl, MemberCopyCommand, MemberInsertSelect, WriteMemberAsync);
    }

    public ValueTask EmitEdgeAsync(EdgeRecord record, CancellationToken cancellationToken) =>
        _edgeSink.EmitAsync(record, cancellationToken);

    public ValueTask EmitMemberAsync(EdgeMemberRecord record, CancellationToken cancellationToken) =>
        _memberSink.EmitAsync(record, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _edgeSink.DisposeAsync().ConfigureAwait(false);
        await _memberSink.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask WriteEdgeAsync(NpgsqlBinaryImporter importer, EdgeRecord record)
    {
        await importer.WriteAsync(record.Hash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.EdgeTypeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)1, NpgsqlDbType.Smallint).ConfigureAwait(false);
    }

    private static async ValueTask WriteMemberAsync(NpgsqlBinaryImporter importer, EdgeMemberRecord record)
    {
        await importer.WriteAsync(record.EdgeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.EdgeTypeHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync(record.RoleHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
        await importer.WriteAsync((short)record.RolePosition, NpgsqlDbType.Smallint).ConfigureAwait(false);
        await importer.WriteAsync(record.ParticipantHash.AsSpan().ToArray(), NpgsqlDbType.Bytea).ConfigureAwait(false);
    }
}
