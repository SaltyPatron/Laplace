using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct EvalSumRow(byte[] Position, long SumScoreFp1e9, long Observations);

    /// <summary>
    /// Summed <c>HAS_EVAL</c> score and observation count for a bounded set of position
    /// ids. The centipawn mean is this sum, not the folded Glicko rating.
    /// </summary>
    public static IReadOnlyList<EvalSumRow> HasEvalSums(
        NpgsqlDataSource dataSource, byte[] typeId, byte[] objectId, IReadOnlyCollection<byte[]> positionIds)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (positionIds.Count == 0) return [];

        const string sql = """
            SELECT subject_id,
                   COALESCE(SUM(sum_score_fp1e9), 0)::bigint,
                   COALESCE(SUM(observation_count), 0)::bigint
            FROM laplace.attestations
            WHERE type_id = @type
              AND object_id = @obj
              AND subject_id = ANY(@ids)
            GROUP BY subject_id
            """;

        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("type", NpgsqlDbType.Bytea).Value = typeId;
        command.Parameters.Add("obj", NpgsqlDbType.Bytea).Value = objectId;
        var ids = command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea);
        ids.Value = positionIds as byte[][] ?? positionIds.ToArray();
        using var reader = command.ExecuteReader();
        var rows = new List<EvalSumRow>();
        while (reader.Read())
            rows.Add(new EvalSumRow((byte[])reader.GetValue(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }
}
