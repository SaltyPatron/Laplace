using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    public readonly record struct ChessRankedPlayerRow(
        long Rank, string IdHex, string Name, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.ranked(limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessRankedPlayerRow>> ChessRankedAsync(
        NpgsqlDataSource dataSource, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        ChessRankedAsync(dataSource, limit, offset, "strength", "desc", ct, onError);

    public static Task<IReadOnlyList<ChessRankedPlayerRow>> ChessRankedAsync(
        NpgsqlDataSource dataSource, int limit, int offset, string sort, string direction,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.ranked"),
            static r => new ChessRankedPlayerRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)),
            p =>
            {
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("sort", sort);
                p.AddWithValue("direction", direction);
            }, ct: ct, label: "chess_ranked", onError: onError);

    public readonly record struct ChessPlayerStrengthRow(
        string IdHex, string Name, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.players_by_initial(initial, limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayersByInitialAsync(
        NpgsqlDataSource dataSource, string initial, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        ChessPlayersByInitialAsync(
            dataSource, initial, limit, offset, "strength", "desc", ct, onError);

    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayersByInitialAsync(
        NpgsqlDataSource dataSource, string initial, int limit, int offset,
        string sort, string direction, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.players_by_initial"),
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add("initials", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                    PlayerCaseForms(initial);
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
                p.AddWithValue("sort", sort);
                p.AddWithValue("direction", direction);
            }, ct: ct, label: "chess_players_by_initial", onError: onError,
            timeoutSeconds: 60);

    /// <summary>
    /// <c>chess.search_page</c> — player candidates found by content containment of the
    /// query and by witnessed names, with exact lookup, standing, ranking, paging and
    /// labels all computed in the one native operation.
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessPlayerSearchCandidatesAsync(
        NpgsqlDataSource dataSource, IReadOnlyList<string> queries, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null, int offset = 0,
        string sort = "strength", string direction = "desc", bool exactOnly = false) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.search_page"),
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = queries
                    .SelectMany(PlayerCaseForms).Distinct(StringComparer.Ordinal).ToArray()
                });
                p.Add(new NpgsqlParameter<int> { TypedValue = RequestedLimit(limit) });
                p.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(0, offset) });
                p.Add(new NpgsqlParameter<string> { TypedValue = sort });
                p.Add(new NpgsqlParameter<string> { TypedValue = direction });
                p.Add(new NpgsqlParameter<bool> { TypedValue = exactOnly });
            }, ct: ct, label: "chess_player_search_candidates", onError: onError,
            timeoutSeconds: 30);

    internal static string[] PlayerCaseForms(string value)
    {
        var exact = value.Trim();
        if (exact.Length == 0) return [];

        var lower = exact.ToLowerInvariant();
        var title = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
        return [.. new[] { exact, lower, title }.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Name → player via <c>chess.player_id</c>, with standing from the strongest OUTCOME
    /// consensus cell through <c>edges_raw</c> (neutral rating and initial deviation when
    /// the player has none).
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerStrengthRow>> ChessFindPlayerAsync(
        NpgsqlDataSource dataSource, string name, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.find_player"),
            static r => new ChessPlayerStrengthRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p => p.AddWithValue("name", name),
            ct: ct, label: "chess_player_id", onError: onError);

    public readonly record struct ChessPlayerRecordRow(
        bool? AsWhite, long Games, long Wins, long Draws, long Losses, long Unscored, double? Score);

    /// <summary><c>chess.player_record(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerRecordRow>> ChessPlayerRecordAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.player_record"),
            static r => new ChessPlayerRecordRow(
                r.IsDBNull(0) ? null : r.GetBoolean(0),
                r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                r.GetInt64(5), r.IsDBNull(6) ? null : r.GetDouble(6)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_player_record", onError: onError);

    public readonly record struct ChessPlayerRatingRow(int Rating, long Games);

    /// <summary><c>chess.player_ratings(id)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerRatingRow>> ChessPlayerRatingsAsync(
        NpgsqlDataSource dataSource, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.player_ratings"),
            static r => new ChessPlayerRatingRow(r.GetInt32(0), r.GetInt64(1)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_player_ratings", onError: onError);

    /// <summary>
    /// Profile facts for a player and its directly asserted identity peers, from
    /// <c>chess.player_profile_edges</c>. Relations are filtered to profile relations before
    /// anything is rendered, and relation and value labels are rendered in one batch each.
    /// </summary>
    public static Task<IReadOnlyList<ChessProfileEdgeRow>> ChessPlayerProfileEdgesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("chess.player_profile_edges_rendered"),
            static r => new ChessProfileEdgeRow(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "chess_player_profiles", onError: onError,
            timeoutSeconds: 30);

    public readonly record struct ChessHeadToHeadRow(
        string OpponentIdHex, string Opponent, long Games, double Rating, double Rd, double EffMu);

    /// <summary><c>chess.head_to_head(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessHeadToHeadRow>> ChessHeadToHeadAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.head_to_head"),
            static r => new ChessHeadToHeadRow(
                r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", RequestedLimit(limit));
            }, ct: ct, label: "chess_head_to_head", onError: onError);

    public readonly record struct ChessPlayerGameRow(
        string EventIdHex, string? PlayedOn, string? Event, string? Eco, bool AsWhite,
        string? OpponentIdHex, string Opponent, string? Result, short? Outcome);

    /// <summary><c>chess.player_games(id, limit, offset)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerGameRow>> ChessPlayerGamesAsync(
        NpgsqlDataSource dataSource, byte[] id, int limit, int offset, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.player_games"),
            static r => new ChessPlayerGameRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt16(8)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", RequestedLimit(limit));
                p.AddWithValue("offset", Math.Max(0, offset));
            }, ct: ct, label: "chess_player_games", onError: onError,
            timeoutSeconds: 60);

    public readonly record struct ChessPlayerMoveRow(
        byte[] NextPosition, long Games, double Score);

    /// <summary><c>chess.player_moves(root, player, white, limit)</c>.</summary>
    public static Task<IReadOnlyList<ChessPlayerMoveRow>> ChessPlayerMovesAsync(
        NpgsqlDataSource dataSource, byte[] rootId, byte[] playerId, bool whiteToMove,
        int limit, CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.player_moves"),
            static r => new ChessPlayerMoveRow((byte[])r[0], r.GetInt64(1), r.GetDouble(2)),
            p =>
            {
                p.Add("root", NpgsqlDbType.Bytea).Value = rootId;
                p.Add("player", NpgsqlDbType.Bytea).Value = playerId;
                p.AddWithValue("white", whiteToMove);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "chess_player_moves", onError: onError);

    public readonly record struct ChessPlayerPlacementRow(byte[] PlayerId, string? Name);

    public static async Task<long> CountChessPlayersMissingPhysicalityAsync(
        NpgsqlDataSource dataSource, byte[] playerTypeId, short physicalityType,
        CancellationToken ct, NpgsqlRead.ErrorTranslator? onError = null)
    {
        var n = await NpgsqlRead.ExecuteScalarAsync<long>(dataSource, SqlCatalog.Get("chess.count_players_missing_physicality"),
            p =>
            {
                p.Add("player_type", NpgsqlDbType.Bytea).Value = playerTypeId;
                p.AddWithValue("physicality_type", physicalityType);
            }, ct: ct, label: "chess_count_players_missing_physicality", onError: onError)
            .ConfigureAwait(false);
        return n;
    }

    /// <summary>
    /// Keyset page of player entities lacking a physicality of
    /// <paramref name="physicalityType"/>. Names are resolved only for the bounded page;
    /// a nameless row comes back with a null name so the caller's keyset still advances.
    /// </summary>
    public static Task<IReadOnlyList<ChessPlayerPlacementRow>> ChessPlayersMissingPhysicalityPageAsync(
        NpgsqlDataSource dataSource, byte[] playerTypeId, short physicalityType,
        byte[] afterId, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("chess.players_missing_physicality_page"),
            static r => new ChessPlayerPlacementRow(
                (byte[])r[0], r.IsDBNull(1) ? null : r.GetString(1)),
            p =>
            {
                p.Add("player_type", NpgsqlDbType.Bytea).Value = playerTypeId;
                p.AddWithValue("physicality_type", physicalityType);
                p.Add("after", NpgsqlDbType.Bytea).Value =
                    afterId.Length == 0 ? Array.Empty<byte>() : afterId;
                p.AddWithValue("limit", Math.Max(1, limit));
            }, ct: ct, label: "chess_players_missing_physicality", onError: onError,
            timeoutSeconds: 60);
}
