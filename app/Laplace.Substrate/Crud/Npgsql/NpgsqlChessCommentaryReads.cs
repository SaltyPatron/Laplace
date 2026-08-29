using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Bounded, chess-specific read surface for live commentary. The chess service supplies the
/// Chess_Game type id; this layer only performs indexed composition/provenance reads and never
/// guesses a textual sense for a motif.
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
    /// Recorded playings whose calculated line projection contains the exact position id.
    /// The containment probe is bounded before joining provenance so live chat cannot turn into
    /// an unbounded corpus scan.
    /// </summary>
    public static Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        int containerLimit,
        int limit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, """
            WITH lines AS MATERIALIZED (
                SELECT c.entity_id AS line_id
                FROM structural.containers_of(@position, 1, @container_limit) c
                WHERE c.type_id = @game_type
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
                p.AddWithValue("container_limit", Math.Clamp(containerLimit, 1, 256));
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_history",
            onError: onError);
}
