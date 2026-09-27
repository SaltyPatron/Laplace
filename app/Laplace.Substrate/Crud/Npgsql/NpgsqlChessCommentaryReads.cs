using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Occurrence read for one position entity: which game-line trajectories contain it and
/// the recorded playings behind those lines. The caller supplies the line entity type and
/// the projection physicality type; the read is a bounded trajectory-constituent probe
/// followed by provenance joins.
/// </summary>
public static class NpgsqlChessCommentaryReads
{
    public readonly record struct PositionHistoryRow(
        byte[] LineId,
        byte[] PlayingId,
        string? PlayedOn,
        byte[]? WhiteId,
        string White,
        byte[]? BlackId,
        string Black,
        string? Result);

    /// <summary>
    /// Recorded playings whose line trajectory contains <paramref name="positionId"/> as a
    /// constituent. The reverse probe uses the trajectory-constituent GIN expression and is
    /// capped at <paramref name="containerLimit"/> lines before provenance is joined.
    /// </summary>
    public static Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        short projectionType,
        int containerLimit,
        int limit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH raw_lines AS MATERIALIZED (
                SELECT p.entity_id AS line_id
                FROM laplace.physicalities p
                JOIN laplace.entities e ON e.id = p.entity_id
                WHERE p.type = @projection_type
                  AND e.type_id = @game_type
                  AND p.trajectory IS NOT NULL
                  AND public.laplace_trajectory_constituent_ids(p.trajectory)
                      @> ARRAY[@position]::bytea[]
                LIMIT @container_limit
            ),
            lines AS MATERIALIZED (
                SELECT DISTINCT line_id FROM raw_lines
            )
            SELECT l.line_id, h.event_id, h.played_on,
                   g.white_id, g.white, g.black_id, g.black, g.result
            FROM lines l
            CROSS JOIN LATERAL chess.line(l.line_id) h
            CROSS JOIN LATERAL chess.game(h.event_id) g
            ORDER BY h.played_on ASC NULLS LAST, h.event_id
            LIMIT @limit
            """,
            static r => new PositionHistoryRow(
                r.GetFieldValue<byte[]>(0),
                r.GetFieldValue<byte[]>(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)),
            p =>
            {
                p.Add("position", NpgsqlDbType.Bytea).Value = positionId;
                p.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
                p.AddWithValue("projection_type", projectionType);
                p.AddWithValue("container_limit", Math.Clamp(containerLimit, 1, 256));
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_history",
            onError: onError);
}
