using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct EvalSumRow(byte[] Position, long SumScoreFp1e9, long Observations);

    public readonly record struct HorizonStepRow(byte[] FromPosition, byte[] ToPosition, long Count);

    /// <summary>
    /// Extracts up to <paramref name="maxPlies"/> forward trajectory steps starting from
    /// <paramref name="rootPositionId"/> across witnessed game-line projections.
    /// </summary>
    public static IReadOnlyList<HorizonStepRow> MultiPlyHorizonSteps(
        NpgsqlDataSource dataSource,
        byte[] rootPositionId,
        byte[] gameTypeId,
        short projectionType,
        int maxPlies = 6,
        int maxLines = 16)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (rootPositionId.Length == 0 || maxPlies < 1 || maxLines < 1) return [];

        const string sql = """
            WITH matched_lines AS MATERIALIZED (
                SELECT p.trajectory
                FROM laplace.physicalities p
                JOIN laplace.entities e ON e.id = p.entity_id
                WHERE p.type = @projection_type
                  AND e.type_id = @game_type
                  AND p.trajectory IS NOT NULL
                  AND public.laplace_trajectory_constituent_ids(p.trajectory)
                      @> ARRAY[@root]::bytea[]
                LIMIT @max_lines
            ),
            unpacked AS MATERIALIZED (
                SELECT public.laplace_trajectory_constituent_ids(m.trajectory) AS ids
                FROM matched_lines m
            ),
            anchored AS MATERIALIZED (
                SELECT u.ids, min(s.idx) AS root_idx
                FROM unpacked u
                CROSS JOIN LATERAL generate_subscripts(u.ids, 1) AS s(idx)
                WHERE u.ids[s.idx] = @root
                GROUP BY u.ids
            ),
            steps AS (
                SELECT a.ids[a.root_idx + k] AS from_pos,
                       a.ids[a.root_idx + k + 1] AS to_pos
                FROM anchored a
                CROSS JOIN LATERAL generate_series(0, @max_plies - 1) AS g(k)
                WHERE a.root_idx + k + 1 <= cardinality(a.ids)
            )
            SELECT from_pos, to_pos, count(*)::bigint AS cnt
            FROM steps
            WHERE from_pos IS NOT NULL AND to_pos IS NOT NULL
            GROUP BY from_pos, to_pos
            ORDER BY cnt DESC, from_pos, to_pos
            """;

        using var connection = dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("root", NpgsqlDbType.Bytea).Value = rootPositionId;
        command.Parameters.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
        command.Parameters.AddWithValue("projection_type", projectionType);
        command.Parameters.AddWithValue("max_plies", Math.Clamp(maxPlies, 1, 12));
        command.Parameters.AddWithValue("max_lines", Math.Clamp(maxLines, 1, 64));
        using var reader = command.ExecuteReader();
        var rows = new List<HorizonStepRow>();
        while (reader.Read())
        {
            rows.Add(new HorizonStepRow(
                (byte[])reader.GetValue(0),
                (byte[])reader.GetValue(1),
                reader.GetInt64(2)));
        }
        return rows;
    }

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
