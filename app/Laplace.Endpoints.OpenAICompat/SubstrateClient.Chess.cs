using Laplace.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The chess reading room: who played, what they played, and how it went. Chess is
/// the proving domain because its ground truth is objectively checkable, and this is
/// where that shows — every number served here is counted off game headers the PGN
/// decomposer transcribed verbatim, so a career record can be checked against the
/// games it came from, and every game against the movetext its own content hash
/// rebuilds.
///
/// NOTHING HERE IS CACHED, and that is the point. The roster used to be a corpus-wide
/// GROUP BY over every game header (~400k rows, ~10s) hidden behind a TTL cache and a
/// startup prewarm — a cache standing in for a fold. Each game now carries its result
/// onto the player in the aggregating lane at ingest, so a record is one consensus
/// cell: witness_count IS games played, eff_mu IS the conservative strength. Ranking
/// is an ORDER BY over a single relation partition. No TTL, no prewarm, no stale
/// window, nothing to repopulate.
///
/// Search is a content-address lookup: chess_player_id() folds a typed name the way
/// the decomposer did and hashes it, so a fully-spelled name resolves in one round
/// trip at any depth in the corpus.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>
    /// The ranked roster, straight off the folded cells. Paging is an OFFSET over an index.
    /// </summary>
    public async Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(int limit, int offset, CancellationToken ct)
    {
        const string sql = """
            SELECT rank, encode(player_id, 'hex'), name, games, rating, rd, eff_mu
            FROM laplace.chess_ranked(@limit, @offset)
            """;
        return await ReadRowsAsync(sql,
            static r => new ChessPlayerRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)),
            cmd =>
            {
                cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
                cmd.Parameters.AddWithValue("offset", Math.Max(0, offset));
            },
            "chess_ranked", ct);
    }

    /// <summary>
    /// Players whose name begins with a given letter, ranked by strength within it.
    ///
    /// Not a rendered sort and not a cached window: a name's first codepoint is vertex 1 of
    /// its trajectory, so this is an equality test on an indexed expression over the
    /// authoritative geometry. Browsing reaches every player in the corpus, not just the
    /// ones a warm list happened to hold.
    /// </summary>
    public async Task<IReadOnlyList<ChessPlayerRow>> ChessPlayersByInitialAsync(
        string initial, int limit, int offset, CancellationToken ct)
    {
        const string sql = """
            SELECT encode(player_id, 'hex'), name, games, rating, rd, eff_mu
            FROM laplace.chess_players_by_initial(@initial, @limit, @offset)
            """;
        var rows = await ReadRowsAsync(sql,
            static r => new ChessPlayerRow(0, r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            cmd =>
            {
                cmd.Parameters.AddWithValue("initial", initial);
                cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
                cmd.Parameters.AddWithValue("offset", Math.Max(0, offset));
            },
            "chess_players_by_initial", ct, timeoutSeconds: 60);

        // Rank is positional within the letter; the fold's ordering already decided it.
        return [.. rows.Select((r, i) => r with { Rank = offset + i + 1 })];
    }

    /// <summary>
    /// A page of the roster, or — when a name is supplied — the player it names.
    ///
    /// The partial-name pass that used to run over the cached ranking went with the cache.
    /// It was only ever a substring scan of the top N held in memory: it could not see past
    /// that window, and it existed because the window existed. A name resolves by content
    /// address instead, which has no window — it finds the right man at any depth in one
    /// round trip, or honestly finds nobody.
    /// </summary>
    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, CancellationToken ct)
        => await ChessPlayersAsync(limit, offset, search, null, ct);

    /// <inheritdoc cref="ChessPlayersAsync(int,int,string?,CancellationToken)"/>
    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, string? initial, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(initial))
        {
            var letter = await ChessPlayersByInitialAsync(initial, limit, offset, ct);
            return new ChessPlayersResponse("chess.players", letter.Count, Math.Max(0, offset), letter);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var hit = await ChessFindPlayerAsync(search.Trim(), ct);
            return new ChessPlayersResponse("chess.players", hit is null ? 0 : 1, 0,
                hit is null ? [] : [hit]);
        }

        var page = await ChessRosterAsync(limit, offset, ct);
        return new ChessPlayersResponse("chess.players", page.Count, Math.Max(0, offset), page);
    }

    /// <summary>
    /// Name to player, in one round trip. chess_player_id() reproduces the decomposer's own
    /// name folding ("Tal, Mikhail" -> "mikhail tal") and hashes the canonical key, so the
    /// typed name lands on the identical id the ingest wrote — or on nothing. The rating comes
    /// from his folded cell; no cell means the substrate has never witnessed him at a board.
    /// </summary>
    public async Task<ChessPlayerRow?> ChessFindPlayerAsync(string name, CancellationToken ct)
    {
        const string sql = """
            WITH p AS (SELECT laplace.chess_player_id(@name) AS id)
            SELECT encode(p.id, 'hex'), laplace.label_or_hex(p.id),
                   c.witness_count, c.rating::double precision, c.rd::double precision,
                   laplace.eff_mu(c.rating, c.rd)::double precision
            FROM p
            JOIN laplace.consensus c
              ON c.subject_id = p.id
             AND c.type_id = laplace.relation_type_id('OUTCOME')
            """;
        var rows = await ReadRowsAsync(sql,
            static r => new ChessPlayerRow(0, r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            cmd => cmd.Parameters.AddWithValue("name", name),
            "chess_player_id", ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The career page: record by colour, the Elo the source tagged, the rivals.</summary>
    public async Task<ChessPlayerResponse?> ChessPlayerAsync(string idHex, int opponentLimit, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        const string recordSql = """
            SELECT as_white, games, wins, draws, losses, unscored, score
            FROM laplace.chess_player_record(@id)
            """;
        var record = await ReadRowsAsync(recordSql,
            static r => (
                AsWhite: r.IsDBNull(0) ? (bool?)null : r.GetBoolean(0),
                Rec: new ChessRecord(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                    r.GetInt64(5), r.IsDBNull(6) ? null : r.GetDouble(6))),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "chess_player_record", ct);

        var overall = record.FirstOrDefault(x => x.AsWhite is null).Rec ?? Empty;
        if (overall.Games == 0) return null;

        const string ratingSql = "SELECT rating, games FROM laplace.chess_player_ratings(@id)";
        var ratings = await ReadRowsAsync(ratingSql,
            static r => new ChessRatingRow(r.GetInt32(0), r.GetInt64(1)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "chess_player_ratings", ct);

        const string oppSql = """
            SELECT encode(opponent_id, 'hex'), opponent, games, rating, rd, eff_mu
            FROM laplace.chess_head_to_head(@id, @limit)
            """;
        var opponents = await ReadRowsAsync(oppSql,
            static r => new ChessOpponentRow(r.GetString(0), r.GetString(1),
                r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
            cmd =>
            {
                cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
                cmd.Parameters.AddWithValue("limit", Math.Clamp(opponentLimit, 1, 200));
            },
            "chess_head_to_head", ct);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var name = await ReadLabelAsync(conn, id, ct) ?? idHex;

        return new ChessPlayerResponse("chess.player", idHex.ToLowerInvariant(), name,
            overall,
            record.FirstOrDefault(x => x.AsWhite is true).Rec ?? Empty,
            record.FirstOrDefault(x => x.AsWhite is false).Rec ?? Empty,
            // Peak is the highest Elo any source ever tagged him with. Ratings come
            // back rating-descending, so it is the first row — no client-side max.
            ratings.Count == 0 ? null : ratings[0].Rating,
            ratings, opponents);
    }

    private static readonly ChessRecord Empty = new(0, 0, 0, 0, 0, null);

    /// <summary>A page of one player's game log, most recent first.</summary>
    public async Task<ChessGamesResponse?> ChessPlayerGamesAsync(
        string idHex, int limit, int offset, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;
        // GH #736: the game-log key column is the playing-EVENT id (the row identifies a
        // playing; the line is shared content reachable through PLAYS_LINE).
        const string sql = """
            SELECT encode(event_id, 'hex'), played_on, event, eco, as_white,
                   encode(opponent_id, 'hex'), opponent, result, outcome
            FROM laplace.chess_player_games(@id, @limit, @offset)
            """;
        var games = await ReadRowsAsync(sql,
            static r => new ChessGameRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt16(8)),
            cmd =>
            {
                cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
                cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
                cmd.Parameters.AddWithValue("offset", Math.Max(0, offset));
            },
            "chess_player_games", ct, timeoutSeconds: 60);

        return new ChessGamesResponse("chess.games", idHex.ToLowerInvariant(),
            Math.Max(0, offset), games);
    }

    /// <summary>
    /// The game as a board sequence, read as LOOKUPS.
    ///
    /// Every per-ply fact is already an attestation keyed by context_id = the game — the MOVE
    /// edge, HAS_SAN, HAS_CLOCK, HAS_EVAL_TOKEN, HAS_THINK_CLASS, MOVE_QUALITY — and the
    /// ordered line of boards is the game's own trajectory. chess_game_plies() is one
    /// trajectory decode plus one indexed pass per relation. Nothing here parses chess.
    ///
    /// This used to replay the verbatim movetext through the engine on every request:
    /// parsing SAN, applying moves, re-deriving boards the analyzer had already composed and
    /// deposited. That recomputed at query time what ingest had already written.
    ///
    /// The replay survives as the FALLBACK for games recorded before the analyzer ran, which
    /// carry no trajectory and therefore have no stored line to read. It is a compatibility
    /// path, not the design: once a game is analyzed, reading it never touches the engine.
    /// </summary>
    public async Task<ChessGamePliesResponse?> ChessGamePliesAsync(string idHex, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        const string sql = """
            SELECT ply, encode(position_id, 'hex'), san, clock, eval_token
            FROM laplace.chess_game_plies(@id)
            """;
        var stored = await ReadRowsAsync(sql,
            static r => (
                Ply: r.GetInt32(0),
                PositionId: r.GetString(1),
                San: r.IsDBNull(2) ? null : r.GetString(2),
                Clock: r.IsDBNull(3) ? null : r.GetString(3),
                Eval: r.IsDBNull(4) ? null : r.GetString(4)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "chess_game_plies", ct, timeoutSeconds: 60);

        // Vertex 1 is the starting position; ply N is vertex N+1, and the SAN on vertex N is
        // the move that LEFT it. A game with no trajectory yields nothing here.
        if (stored.Count > 1)
        {
            var plies = new List<ChessPlyRow>(stored.Count - 1);
            for (int i = 0; i + 1 < stored.Count; i++)
            {
                var from = stored[i];
                var to = stored[i + 1];
                plies.Add(new ChessPlyRow(
                    Ply: i + 1,
                    San: from.San ?? "",
                    Uci: "",
                    Fen: "",
                    WhiteMoved: i % 2 == 0,
                    ClockSeconds: ParseClockSeconds(from.Clock),
                    PositionId: to.PositionId));
            }
            return new ChessGamePliesResponse("chess.game.plies", idHex.ToLowerInvariant(),
                stored[0].PositionId, plies.Any(p => p.ClockSeconds is not null), null, plies);
        }

        // Fallback: pre-analysis game, no stored line. Replay the verbatim movetext.
        var game = await ChessGameAsync(idHex, ct);
        if (game is null) return null;
        var replay = Laplace.Chess.Service.ChessReplay.Replay(game.Movetext);
        return new ChessGamePliesResponse("chess.game.plies", idHex.ToLowerInvariant(),
            replay.StartFen, replay.HasClocks, replay.Truncated,
            [.. replay.Plies.Select(p => new ChessPlyRow(
                p.Ply, p.San, p.Uci, p.Fen, p.WhiteMoved, p.ClockSeconds, p.PositionId))]);
    }

    /// <summary>"0:02:59.8" -> 179.8. The clock the source recorded, as the source wrote it.</summary>
    private static double? ParseClockSeconds(string? clock)
    {
        if (string.IsNullOrWhiteSpace(clock)) return null;
        var parts = clock.Split(':');
        if (parts.Length != 3) return null;
        return double.TryParse(parts[0], out var h)
            && double.TryParse(parts[1], out var m)
            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var sec)
            ? h * 3600 + m * 60 + sec
            : null;
    }

    /// <summary>One game: its headers and the movetext its own content hash rebuilds.</summary>
    public async Task<ChessGameResponse?> ChessGameAsync(string idHex, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;
        const string sql = """
            SELECT encode(white_id, 'hex'), white, encode(black_id, 'hex'), black,
                   result, played_on, event, eco, termination, time_control,
                   tc_class, movetext
            FROM laplace.chess_game(@id)
            """;
        var rows = await ReadRowsAsync(sql,
            static r => new ChessGameResponse("chess.game", "",
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
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11)),
            cmd => cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id,
            "chess_game", ct, timeoutSeconds: 60);

        return rows.Count == 0 ? null : rows[0] with { IdHex = idHex.ToLowerInvariant() };
    }
}
