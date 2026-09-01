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
