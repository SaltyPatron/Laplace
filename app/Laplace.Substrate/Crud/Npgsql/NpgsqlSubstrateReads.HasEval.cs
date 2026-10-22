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

        using var connection = dataSource.OpenConnection();
        using var command = NpgsqlCatalog.Command(connection, null, SqlCatalog.Get("chess.multi_ply_horizon_steps"), parameters =>
        {
            parameters.Add("root", NpgsqlDbType.Bytea).Value = rootPositionId;
            parameters.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
            parameters.AddWithValue("projection_type", projectionType);
            parameters.AddWithValue("max_plies", Math.Clamp(maxPlies, 1, 12));
            parameters.AddWithValue("max_lines", Math.Clamp(maxLines, 1, 64));
        });
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

        using var connection = dataSource.OpenConnection();
        using var command = NpgsqlCatalog.Command(connection, null, SqlCatalog.Get("chess.has_eval_sums"), parameters =>
        {
            parameters.Add("type", NpgsqlDbType.Bytea).Value = typeId;
            parameters.Add("obj", NpgsqlDbType.Bytea).Value = objectId;
            var ids = parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea);
            ids.Value = positionIds as byte[][] ?? positionIds.ToArray();
        });
        using var reader = command.ExecuteReader();
        var rows = new List<EvalSumRow>();
        while (reader.Read())
            rows.Add(new EvalSumRow((byte[])reader.GetValue(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }
}
