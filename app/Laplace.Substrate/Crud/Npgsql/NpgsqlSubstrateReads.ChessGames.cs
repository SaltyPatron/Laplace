using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    public readonly record struct ChessProfileEdgeRow(
        string SubjectIdHex, string Relation, string Value);

    public readonly record struct ChessGameDetailRow(
        string? WhiteIdHex, string White, string? BlackIdHex, string Black,
        string? Result, string? PlayedOn, string? Event, string? Eco,
        string? Termination, string? TimeControl, string? TcClass);

    /// <summary><c>chess.game(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessGameDetailRow>> ChessGameAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.game"),
            static r => new ChessGameDetailRow(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_game", onError: onError,
            timeoutSeconds: 60);

    public static Task<byte[]?> ChessSetupPositionIdAsync(
        NpgsqlDataSource dataSource, byte[] playingId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource, SqlCatalog.Get("chess.setup_position"),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = playingId,
            ct: ct, label: "chess_setup_position", onError: onError);

    public static Task<byte[]?> ChessLineForPlayingAsync(
        NpgsqlDataSource dataSource, byte[] playingId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource, SqlCatalog.Get("chess.line_for_playing"),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = playingId,
            ct: ct, label: "chess_line_for_playing", onError: onError);

    /// <summary>One stored move entity: its id and its Content constituent ids.</summary>
    public readonly record struct ChessMoveEntityRow(byte[] MoveId, byte[][] Atoms);

    /// <summary>
    /// Every entity of <paramref name="moveTypeId"/> with its Content constituent ids. Moves
    /// are content-addressed, so the set is bounded by distinct moves, not by games; callers
    /// decode the constituents and read consensus cells without replaying any game.
    /// </summary>
    public static Task<IReadOnlyList<ChessMoveEntityRow>> ChessMoveEntitiesAsync(
        NpgsqlDataSource dataSource, byte[] moveTypeId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.move_entities"),
            static r => new ChessMoveEntityRow((byte[])r[0], (byte[][])r[1]),
            p => p.Add("type", NpgsqlDbType.Bytea).Value = moveTypeId,
            ct: ct, label: "chess_move_entities", onError: onError);

    /// <summary>One row of <c>chess.learned_moves</c> with the move's own atoms.</summary>
    public readonly record struct ChessLearnedMoveRow(
        long Plays, double MoverScore, byte[][] Atoms);

    /// <summary>
    /// <c>chess.learned_moves(p_games)</c> joined to each move's Content trajectory.
    ///
    /// The move's constituent ids come back with each row; they decode to the move's
    /// parts, so a caller can project onto piece-square cells without a board or replay.
    /// </summary>
    public static Task<IReadOnlyList<ChessLearnedMoveRow>> ChessLearnedMovesAsync(
        NpgsqlDataSource dataSource, int games, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.learned_moves_atoms"),
            static r => new ChessLearnedMoveRow(
                r.GetInt64(0), r.GetDouble(1), (byte[][])r[2]),
            p => p.AddWithValue("games", games),
            ct: ct, label: "chess_learned_moves", onError: onError);

    public readonly record struct ChessMoveRow(
        byte[] NextPosition, double EffMu, double Rd, long WitnessCount);

    /// <summary><c>chess.moves(root, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessMoveRow>> ChessMovesAsync(
        NpgsqlDataSource dataSource, byte[] rootId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.moves"),
            static r => new ChessMoveRow(
                (byte[])r[0], r.GetDouble(1), r.GetDouble(2), r.GetInt64(3)),
            p =>
            {
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_moves", onError: onError);

    /// <summary>
    /// Every (terminal position, name, ECO) from the source's named line trajectories, in
    /// one set read that the caller probes in memory. The name and ECO testimony is on the
    /// line; the terminal position is recovered as the last vertex of the line's
    /// projection trajectory, not testified separately.
    /// </summary>
    public static Task<IReadOnlyList<(byte[] Position, byte[] Name, byte[]? Eco)>>
        OpeningCatalogAsync(
            NpgsqlDataSource dataSource, byte[] openingNameType, byte[] hasEcoType,
            byte[] sourceId, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        // The ORDER BY makes naming deterministic: the consumer keeps the first row per
        // position, so rows are ranked by the name cell's folded standing
        // (consensus.eff_mu), then name id, then ECO id with absent ECOs last.
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.opening_catalog"),
            r => ((byte[])r[0], (byte[])r[1], r.IsDBNull(2) ? null : (byte[])r[2]),
            p =>
            {
                p.Add("name", NpgsqlDbType.Bytea).Value = openingNameType;
                p.Add("eco", NpgsqlDbType.Bytea).Value = hasEcoType;
                p.Add("src", NpgsqlDbType.Bytea).Value = sourceId;
            },
            ct: ct, label: "opening_catalog", onError: onError);

    /// <summary>Count distinct event entities with PLAYS_LINE under given sources.</summary>
    public static async Task<long> CountChessEventsWithPlaysLineAsync(
        NpgsqlDataSource dataSource, byte[] eventTypeId, byte[] playsLineTypeId,
        byte[][] sourceIds, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null)
    {
        var n = await NpgsqlRead.ExecuteScalarAsync<long>(dataSource, SqlCatalog.Get("chess.count_events_with_plays_line"),
            p =>
            {
                p.Add("event_type", NpgsqlDbType.Bytea).Value = eventTypeId;
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "chess_count_events", onError: onError).ConfigureAwait(false);
        return n;
    }

    /// <summary>Count distinct PLAYS_LINE objects under given sources.</summary>
    public static async Task<long> CountChessLinesWithPlaysLineAsync(
        NpgsqlDataSource dataSource, byte[] playsLineTypeId, byte[][] sourceIds,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var n = await NpgsqlRead.ExecuteScalarAsync<long>(dataSource, SqlCatalog.Get("chess.count_lines_with_plays_line"),
            p =>
            {
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "chess_count_lines", onError: onError).ConfigureAwait(false);
        return n;
    }

    /// <summary>Page of recorded event ids (keyset on e.id).</summary>
    public static Task<IReadOnlyList<byte[]>> ChessEventIdPageAsync(
        NpgsqlDataSource dataSource, byte[] eventTypeId, byte[] playsLineTypeId,
        byte[][] sourceIds, byte[] afterId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.event_id_page"),
            static r => (byte[])r[0],
            p =>
            {
                p.Add("event_type", NpgsqlDbType.Bytea).Value = eventTypeId;
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("after", NpgsqlDbType.Bytea).Value =
                    afterId.Length == 0 ? Array.Empty<byte>() : afterId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_event_id_page", onError: onError);

    /// <summary>Page of recorded line ids (keyset on PLAYS_LINE object_id).</summary>
    public static Task<IReadOnlyList<byte[]>> ChessLineIdPageAsync(
        NpgsqlDataSource dataSource, byte[] playsLineTypeId, byte[][] sourceIds,
        byte[] afterId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.line_id_page"),
            static r => (byte[])r[0],
            p =>
            {
                p.Add("plays", NpgsqlDbType.Bytea).Value = playsLineTypeId;
                var sources = p.AddWithValue("sources", sourceIds);
                sources.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.Add("after", NpgsqlDbType.Bytea).Value =
                    afterId.Length == 0 ? Array.Empty<byte>() : afterId;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_line_id_page", onError: onError);
}
