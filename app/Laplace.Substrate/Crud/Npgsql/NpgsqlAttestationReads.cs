using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Exact set reads over durable attestation testimony. Every predicate carries the
/// relation type so the LIST-partitioned attestation table prunes before the id probe.
/// </summary>
public static class NpgsqlAttestationReads
{
    public readonly record struct WitnessRow(
        byte[] Id, byte[] SubjectId, byte[] TypeId, byte[]? ObjectId,
        byte[] SourceId, byte[]? ContextId, short Outcome, long ObservationCount);

    public readonly record struct WitnessScope(
        Hash128 SubjectId, Hash128 TypeId, Hash128 SourceId, Hash128? ContextId);

    /// <summary>Reads every witness in the selected (subject, type, source, context) scopes
    /// in one set-sized read. Object and outcome are not filtered, so confirming,
    /// drawing and refuting testimony on the same scope all come back.</summary>
    public static Task<IReadOnlyList<WitnessRow>> WitnessesAsync(
        NpgsqlDataSource dataSource, IReadOnlyList<WitnessScope> scopes, CancellationToken ct)
    {
        // Deduplicate complete tuples, never independent columns: independent ANY
        // sets select their Cartesian product and admit unrelated header witnesses.
        var selected = scopes.Distinct().ToArray();
        if (selected.Length == 0)
            return Task.FromResult<IReadOnlyList<WitnessRow>>(Array.Empty<WitnessRow>());
        return NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("attestations.witnesses_selected"),
            static r => new WitnessRow(
                (byte[])r[0], (byte[])r[1], (byte[])r[2], r.IsDBNull(3) ? null : (byte[])r[3],
                (byte[])r[4], r.IsDBNull(5) ? null : (byte[])r[5], r.GetInt16(6), r.GetInt64(7)),
            p =>
            {
                p.Add("subjects", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = selected.Select(s => s.SubjectId.ToBytes()).ToArray();
                p.Add("types", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = selected.Select(s => s.TypeId.ToBytes()).ToArray();
                p.Add("sources", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = selected.Select(s => s.SourceId.ToBytes()).ToArray();
                p.Add("contexts", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = selected.Select(s => s.ContextId?.ToBytes()).ToArray();
            }, ct: ct, label: "attestation_witnesses_batch");
    }

    /// <summary>
    /// Returns the subset of <paramref name="ids"/> already present in the
    /// <paramref name="typeId"/> partition. The partition key in the predicate lets
    /// PostgreSQL prune to one relation family before the bytea-id probe.
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
