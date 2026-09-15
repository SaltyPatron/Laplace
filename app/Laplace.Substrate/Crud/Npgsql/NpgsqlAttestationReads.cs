using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Shared exact reads over durable attestation testimony. Consumers must not carry
/// private SQL for presence/migration probes: this keeps partition routing and the
/// query shape in one substrate-owned implementation.
/// </summary>
public static class NpgsqlAttestationReads
{
    public readonly record struct WitnessRow(
        byte[] Id, byte[] SubjectId, byte[] TypeId, byte[]? ObjectId,
        byte[] SourceId, byte[]? ContextId, short Outcome, long ObservationCount);

    /// <summary>Read complete witness identity fields for a bounded subject/type/source set.
    /// The relation predicate retains partition pruning; callers compare their selected
    /// contexts without one database crossing per witness.</summary>
    public static Task<IReadOnlyList<WitnessRow>> WitnessesAsync(
        NpgsqlDataSource dataSource, byte[][] subjects, byte[][] types, byte[][] sources,
        byte[][] contexts, byte[][] contextlessSubjects, CancellationToken ct)
        => NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT a.id, a.subject_id, a.type_id, a.object_id, a.source_id,
                   a.context_id, a.outcome, a.observation_count
            FROM laplace.attestations a
            WHERE a.subject_id = ANY(@subjects::bytea[])
              AND a.type_id = ANY(@types::bytea[])
              AND a.source_id = ANY(@sources::bytea[])
              AND (a.context_id = ANY(@contexts::bytea[])
                   OR (a.context_id IS NULL AND a.subject_id = ANY(@contextless_subjects::bytea[])))
            """,
            static r => new WitnessRow(
                (byte[])r[0], (byte[])r[1], (byte[])r[2], r.IsDBNull(3) ? null : (byte[])r[3],
                (byte[])r[4], r.IsDBNull(5) ? null : (byte[])r[5], r.GetInt16(6), r.GetInt64(7)),
            p =>
            {
                p.Add("subjects", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = subjects;
                p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = types;
                p.Add("sources", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = sources;
                p.Add("contexts", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = contexts;
                p.Add("contextless_subjects", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = contextlessSubjects;
            }, ct: ct, label: "attestation_witnesses_batch");

    /// <summary>
    /// Return the subset of <paramref name="ids"/> already present in one relation partition.
    /// <paramref name="typeId"/> is required because attestations are LIST-partitioned by
    /// type_id; keeping the partition key in the predicate lets PostgreSQL prune before the
    /// bytea-id probe instead of opening every relation family.
    /// </summary>
    public static Task<IReadOnlyList<byte[]>> PresentIdsAsync(
        NpgsqlConnection conn, byte[] typeId, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        if (ids.Length == 0)
            return Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());

        return NpgsqlRead.ReadRowsAsync(conn, """
            SELECT a.id
            FROM laplace.attestations a
            WHERE a.type_id = @type
              AND a.id = ANY(@ids::bytea[])
            """,
            static r => r.GetFieldValue<byte[]>(0),
            p =>
            {
                p.Add("type", NpgsqlDbType.Bytea).Value = typeId;
                p.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = ids;
            },
            ct: ct, label: "attestation_present_ids", onError: onError);
    }
}
