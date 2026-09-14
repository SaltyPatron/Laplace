using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct ChessPlayerModelMoveRow(
        byte[] PlayerId,
        byte[] NextPosition,
        long Games,
        double Score);

    /// <summary>
    /// One backend read for every constituent of a composite player model at one exact position.
    /// The existing chess.player_moves function remains the semantic owner of player/color/result
    /// attribution; this caller only batches independent member projections through LATERAL.
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerModelMoveRow>> ChessPlayerModelMovesAsync(
        NpgsqlDataSource dataSource,
        byte[] rootId,
        byte[][] playerIds,
        bool whiteToMove,
        int limitPerPlayer,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        if (playerIds.Length == 0)
            return Task.FromResult<IReadOnlyList<ChessPlayerModelMoveRow>>(
                Array.Empty<ChessPlayerModelMoveRow>());

        return NpgsqlRead.ReadRowsAsync(dataSource, """
            SELECT p.player_id, m.next_position, m.games, m.score
            FROM unnest(@players::bytea[]) AS p(player_id)
            CROSS JOIN LATERAL chess.player_moves(
                @root, p.player_id, @white, @limit
            ) AS m
            ORDER BY p.player_id, m.games DESC, m.next_position
            """,
            static r => new ChessPlayerModelMoveRow(
                r.GetFieldValue<byte[]>(0),
                r.GetFieldValue<byte[]>(1),
                r.GetInt64(2),
                r.GetDouble(3)),
            p =>
            {
                p.Add("players", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = playerIds;
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.AddWithValue("white", whiteToMove);
                p.AddWithValue("limit", RequestedLimit(limitPerPlayer));
            },
            ct: ct,
            label: "chess_player_model_moves",
            onError: onError);
    }
}
