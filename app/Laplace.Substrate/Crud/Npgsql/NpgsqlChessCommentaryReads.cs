using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Occurrence read for one position entity: which game-line trajectories contain it and
/// the recorded playings behind those lines. Precedents are ordered by the mover's
/// Glicko-2 lower bound, then source Elo, then an opening or motif match. The GIN
/// probe finds the lines; it does not decide their order.
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
        string? Result,
        double? MoverEffMu,
        int? MoverElo,
        int OpeningOrMotif,
        long WitnessCount);

    public readonly record struct PositionContinuationCount(byte[] NextPositionId, long Count);

    /// <summary>
    /// Recorded playings whose line contains <paramref name="positionId"/>.
    /// <paramref name="witnessCount"/> on every row is the number of such playings.
    /// <paramref name="containerLimit"/> remains so callers keep a positive bound; it
    /// is not a cut of the GIN page.
    /// </summary>
    public static Task<IReadOnlyList<PositionHistoryRow>> PositionHistoryAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        short projectionType,
        int containerLimit,
        int limit,
        CancellationToken ct,
        bool whiteToMove = true,
        string? eco = null,
        string? motif = null,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.commentary_position_history"),
            static r => new PositionHistoryRow(
                r.GetFieldValue<byte[]>(0),
                r.GetFieldValue<byte[]>(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<byte[]>(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.GetInt32(10),
                r.GetInt64(11)),
            p =>
            {
                p.Add("position", NpgsqlDbType.Bytea).Value = positionId;
                p.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
                p.AddWithValue("projection_type", projectionType);
                p.AddWithValue("container_limit", Math.Clamp(containerLimit, 1, 256));
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
                p.AddWithValue("white_to_move", whiteToMove);
                p.Add("eco", NpgsqlDbType.Text).Value = (object?)eco ?? DBNull.Value;
                p.Add("motif", NpgsqlDbType.Text).Value = (object?)motif ?? DBNull.Value;
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_history",
            onError: onError);

    /// <summary>
    /// How the games that reached <paramref name="positionId"/> left it. The counts are
    /// the next trajectory constituent, not the order of the GIN page.
    /// </summary>
    public static Task<IReadOnlyList<PositionContinuationCount>> PositionContinuationsAsync(
        NpgsqlDataSource dataSource,
        byte[] positionId,
        byte[] gameTypeId,
        short projectionType,
        int limit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.commentary_position_continuations"),
            static r => new PositionContinuationCount(
                r.GetFieldValue<byte[]>(0),
                r.GetInt64(1)),
            p =>
            {
                p.Add("position", NpgsqlDbType.Bytea).Value = positionId;
                p.Add("game_type", NpgsqlDbType.Bytea).Value = gameTypeId;
                p.AddWithValue("projection_type", projectionType);
                p.AddWithValue("limit", Math.Clamp(limit, 1, 32));
            },
            timeoutSeconds: 3,
            ct: ct,
            label: "chess_commentary_position_continuations",
            onError: onError);
}
