using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

internal static class LegacyBootstrapVerifier
{
    internal enum Result { NoLegacyMarker, Reconciled }
    internal readonly record struct Verification(Result Disposition, int RoundTrips);

    internal static async Task<Verification> VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkingSetReconciliation reconciliation,
        IReadOnlyList<(IntPtr Ptr, long Len)> entityBlobs,
        IReadOnlyList<StagedRowRef> entityRows,
        IReadOnlyList<(IntPtr Ptr, long Len)> physicalityBlobs,
        IReadOnlyList<StagedRowRef> physicalityRows,
        IReadOnlyList<(IntPtr Ptr, long Len)> attestationBlobs,
        IReadOnlyList<StagedRowRef> attestationRows,
        CancellationToken ct)
    {
        await using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText =
                "SELECT EXISTS (SELECT 1 FROM laplace.attestations WHERE id = $1)";
            marker.Parameters.AddWithValue(
                NpgsqlDbType.Bytea, reconciliation.LegacyMarkerAttestationId.ToBytes());
            if (await marker.ExecuteScalarAsync(ct) is not true)
                return new Verification(Result.NoLegacyMarker, 1);
        }

        bool entitiesMatch = await VerifyEntitiesAsync(
            connection, transaction, entityBlobs, entityRows, ct).ConfigureAwait(false);
        if (!entitiesMatch) throw Mismatch(reconciliation, "entity set is incomplete or mismatched");

        bool physicalitiesMatch = await VerifyPhysicalitiesAsync(
            connection, transaction, physicalityBlobs, physicalityRows, ct).ConfigureAwait(false);
        if (!physicalitiesMatch)
            throw Mismatch(reconciliation, "physicality set is incomplete or mismatched");

        bool evidenceAndConsensusMatch = await VerifyEvidenceAndConsensusAsync(
            connection, transaction, attestationBlobs, attestationRows, ct).ConfigureAwait(false);
        if (!evidenceAndConsensusMatch)
            throw Mismatch(reconciliation,
                "evidence is incomplete/non-uniform or standing consensus is not caught up");

        return new Verification(Result.Reconciled, 4);
    }

    private static LegacyBootstrapReconciliationException Mismatch(
        WorkingSetReconciliation reconciliation, string reason) =>
        new(reconciliation.LegacyMarkerAttestationId, reason);

    private static async Task<bool> VerifyEntitiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        IReadOnlyList<StagedRowRef> rows,
        CancellationToken ct)
    {
        var ids = new byte[rows.Count][];
        var tiers = new short[rows.Count];
        var types = new byte[rows.Count][];
        for (int i = 0; i < rows.Count; i++)
        {
            byte[] row = CopyRow(blobs, rows[i]);
            byte[]?[] fields = Fields(row, 4);
            ids[i] = Required(fields[0]);
            tiers[i] = Int16(fields[1]);
            types[i] = Required(fields[2]);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) = cardinality($1)
            FROM unnest($1::bytea[], $2::smallint[], $3::bytea[]) AS expected(id, tier, type_id)
            JOIN laplace.entities stored
              ON stored.id = expected.id
             AND stored.tier = expected.tier
             AND stored.type_id = expected.type_id
            """;
        Add(command, ids, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, tiers, NpgsqlDbType.Array | NpgsqlDbType.Smallint);
        Add(command, types, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> VerifyPhysicalitiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        IReadOnlyList<StagedRowRef> rows,
        CancellationToken ct)
    {
        var ids = new byte[rows.Count][];
        var entityIds = new byte[rows.Count][];
        var types = new short[rows.Count];
        var coords = new byte[rows.Count][];
        var hilberts = new byte[rows.Count][];
        var trajectories = new byte[rows.Count][];
        var trajectoryNull = new bool[rows.Count];
        var constituents = new int[rows.Count];
        var residuals = new double[rows.Count];
        var residualNull = new bool[rows.Count];
        var sourceDims = new int[rows.Count];
        var sourceDimNull = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            byte[] row = CopyRow(blobs, rows[i]);
            byte[]?[] fields = Fields(row, 10);
            ids[i] = Required(fields[0]);
            entityIds[i] = Required(fields[1]);
            types[i] = Int16(fields[2]);
            coords[i] = Required(fields[3]);
            hilberts[i] = Required(fields[4]);
            trajectoryNull[i] = fields[5] is null;
            trajectories[i] = fields[5] ?? [];
            constituents[i] = Int32(fields[6]);
            residualNull[i] = fields[7] is null;
            residuals[i] = fields[7] is null ? 0 : Float64(fields[7]);
            sourceDimNull[i] = fields[8] is null;
            sourceDims[i] = fields[8] is null ? 0 : Int32(fields[8]);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) = cardinality($1) AND coalesce(bool_and(
                stored.entity_id = expected.entity_id
                AND stored.type = expected.type
                AND public.ST_AsEWKB(stored.coord, 'NDR') = expected.coord
                AND stored.hilbert_index = expected.hilbert
                AND stored.n_constituents = expected.n_constituents
                AND stored.alignment_residual IS NOT DISTINCT FROM
                    CASE WHEN expected.residual_null THEN NULL ELSE expected.residual END
                AND stored.source_dim IS NOT DISTINCT FROM
                    CASE WHEN expected.source_dim_null THEN NULL ELSE expected.source_dim END
                AND ((stored.trajectory IS NULL AND expected.trajectory_null)
                  OR (stored.trajectory IS NOT NULL AND NOT expected.trajectory_null
                    AND public.laplace_trajectory_equivalent(
                        stored.trajectory,
                        public.ST_GeomFromEWKB(expected.trajectory))))), cardinality($1) = 0)
            FROM unnest($1::bytea[], $2::bytea[], $3::smallint[], $4::bytea[],
                        $5::bytea[], $6::bytea[], $7::boolean[], $8::integer[],
                        $9::double precision[], $10::boolean[], $11::integer[], $12::boolean[])
              AS expected(id, entity_id, type, coord, hilbert, trajectory, trajectory_null,
                          n_constituents, residual, residual_null, source_dim, source_dim_null)
            JOIN laplace.physicalities stored ON stored.id = expected.id
            """;
        Add(command, ids, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, entityIds, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, types, NpgsqlDbType.Array | NpgsqlDbType.Smallint);
        Add(command, coords, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, hilberts, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, trajectories, NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, trajectoryNull, NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        Add(command, constituents, NpgsqlDbType.Array | NpgsqlDbType.Integer);
        Add(command, residuals, NpgsqlDbType.Array | NpgsqlDbType.Double);
        Add(command, residualNull, NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        Add(command, sourceDims, NpgsqlDbType.Array | NpgsqlDbType.Integer);
        Add(command, sourceDimNull, NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> VerifyEvidenceAndConsensusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        IReadOnlyList<StagedRowRef> rows,
        CancellationToken ct)
    {
        var columns = Enumerable.Range(0, 12).Select(_ => new List<object>(rows.Count)).ToArray();
        var masks = new List<byte[]>(rows.Count);
        var objectNull = new List<bool>(rows.Count);
        var contextNull = new List<bool>(rows.Count);
        var maskNull = new List<bool>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            byte[]?[] f = Fields(CopyRow(blobs, rows[i]), 13);
            columns[0].Add(Required(f[0]));
            columns[1].Add(Required(f[1]));
            columns[2].Add(Required(f[2]));
            columns[3].Add(f[3] ?? new byte[16]); objectNull.Add(f[3] is null);
            columns[4].Add(Required(f[4]));
            columns[5].Add(f[5] ?? new byte[16]); contextNull.Add(f[5] is null);
            columns[6].Add(Int16(f[6]));
            columns[7].Add(Int64(f[8]));
            columns[8].Add(Int64(f[9]));
            columns[9].Add(Int64(f[10]));
            columns[10].Add(Int64(f[11]));
            masks.Add(f[12] ?? []); maskNull.Add(f[12] is null);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH expected_rows AS (
              SELECT * FROM unnest(
                $1::bytea[], $2::bytea[], $3::bytea[], $4::bytea[], $5::boolean[],
                $6::bytea[], $7::bytea[], $8::boolean[], $9::smallint[], $10::bigint[],
                $11::bigint[], $12::bigint[], $13::bigint[], $14::bytea[], $15::boolean[])
              AS x(id, subject_id, type_id, object_value, object_null, source_id,
                   context_value, context_null, outcome, games, score_sum,
                   opponent_rd, opponent_rating, highway_mask, mask_null)
            ), expected AS (
              SELECT id, subject_id, type_id,
                     CASE WHEN object_null THEN NULL ELSE object_value END object_id,
                     source_id,
                     CASE WHEN context_null THEN NULL ELSE context_value END context_id,
                     outcome, sum(games)::bigint games, sum(score_sum)::bigint score_sum,
                     opponent_rd, opponent_rating,
                     CASE WHEN mask_null THEN NULL ELSE highway_mask END highway_mask
              FROM expected_rows
              GROUP BY id, subject_id, type_id, object_value, object_null, source_id,
                       context_value, context_null, outcome, opponent_rd, opponent_rating,
                       highway_mask, mask_null
            ), matched AS (
              SELECT expected.*,
                     stored.observation_count stored_games,
                     stored.sum_score_fp1e9 stored_sum,
                     CASE WHEN expected.games > 0
                            AND stored.observation_count % expected.games = 0
                          THEN stored.observation_count / expected.games ELSE 0 END multiplier
              FROM expected
              JOIN laplace.attestations stored
                ON stored.id = expected.id
               AND stored.subject_id = expected.subject_id
               AND stored.type_id = expected.type_id
               AND stored.object_id IS NOT DISTINCT FROM expected.object_id
               AND stored.source_id = expected.source_id
               AND stored.context_id IS NOT DISTINCT FROM expected.context_id
               AND stored.outcome = expected.outcome
               AND stored.opponent_rd_fp1e9 = expected.opponent_rd
               AND stored.opponent_rating_fp1e9 = expected.opponent_rating
               AND stored.highway_mask IS NOT DISTINCT FROM expected.highway_mask
            ), cells AS (
              SELECT DISTINCT subject_id, type_id, object_id FROM expected
            ), evidence_totals AS (
              SELECT cells.subject_id, cells.type_id, cells.object_id,
                     sum(a.observation_count)::bigint games, max(a.last_observed_at) last_at
              FROM cells
              JOIN laplace.attestations a
                ON a.subject_id = cells.subject_id AND a.type_id = cells.type_id
               AND a.object_id IS NOT DISTINCT FROM cells.object_id
              GROUP BY cells.subject_id, cells.type_id, cells.object_id
            ), consensus_parity AS (
              SELECT count(*) count, bool_and(c.witness_count = e.games
                                               AND c.last_observed_at = e.last_at) ok
              FROM evidence_totals e
              JOIN laplace.consensus c
                ON c.subject_id = e.subject_id AND c.type_id = e.type_id
               AND c.object_id IS NOT DISTINCT FROM e.object_id
            )
            SELECT (SELECT count(*) FROM matched) = (SELECT count(*) FROM expected)
               AND (SELECT bool_and(outcome = 2 AND multiplier >= 1
                         AND stored_sum::numeric * games = score_sum::numeric * stored_games)
                    FROM matched)
               AND (SELECT min(multiplier) = max(multiplier) FROM matched)
               AND (SELECT count = (SELECT count(*) FROM cells) AND ok FROM consensus_parity)
               AND NOT EXISTS (
                    SELECT 1
                    FROM laplace.attestations historical
                    WHERE historical.source_id IN (SELECT DISTINCT source_id FROM expected)
                      AND NOT EXISTS (
                          SELECT 1 FROM expected WHERE expected.id = historical.id)
                      AND EXISTS (
                          SELECT 1
                          FROM expected
                          WHERE expected.subject_id = historical.subject_id
                            AND expected.type_id = historical.type_id
                            AND expected.object_id IS NOT DISTINCT FROM historical.object_id))
            """;
        Add(command, Cast<byte[]>(columns[0]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, Cast<byte[]>(columns[1]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, Cast<byte[]>(columns[2]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, Cast<byte[]>(columns[3]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, objectNull.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        Add(command, Cast<byte[]>(columns[4]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, Cast<byte[]>(columns[5]), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, contextNull.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        Add(command, Cast<short>(columns[6]), NpgsqlDbType.Array | NpgsqlDbType.Smallint);
        Add(command, Cast<long>(columns[7]), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        Add(command, Cast<long>(columns[8]), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        Add(command, Cast<long>(columns[9]), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        Add(command, Cast<long>(columns[10]), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        Add(command, masks.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        Add(command, maskNull.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Boolean);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static T[] Cast<T>(List<object> values) => values.Cast<T>().ToArray();

    private static void Add(NpgsqlCommand command, object value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter { Value = value, NpgsqlDbType = type });

    private static byte[] CopyRow(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, StagedRowRef row)
    {
        var bytes = new byte[row.Length];
        Marshal.Copy(IntPtr.Add(blobs[row.Blob].Ptr, checked((int)row.Offset)), bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[]?[] Fields(byte[] row, int expected)
    {
        int offset = 0;
        int count = BinaryPrimitives.ReadInt16BigEndian(row.AsSpan(offset, 2)); offset += 2;
        if (count != expected) throw new InvalidDataException($"expected {expected} fields, got {count}");
        var fields = new byte[]?[count];
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(row.AsSpan(offset, 4)); offset += 4;
            if (length < 0) continue;
            fields[i] = row.AsSpan(offset, length).ToArray();
            offset += length;
        }
        if (offset != row.Length) throw new InvalidDataException("staged row has trailing bytes");
        return fields;
    }

    private static byte[] Required(byte[]? value) =>
        value ?? throw new InvalidDataException("required staged field is null");
    private static short Int16(byte[]? value) =>
        BinaryPrimitives.ReadInt16BigEndian(Required(value));
    private static int Int32(byte[]? value) =>
        BinaryPrimitives.ReadInt32BigEndian(Required(value));
    private static long Int64(byte[]? value) =>
        BinaryPrimitives.ReadInt64BigEndian(Required(value));
    private static double Float64(byte[]? value) =>
        BitConverter.Int64BitsToDouble(Int64(value));
}
