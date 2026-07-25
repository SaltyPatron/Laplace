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
/// The split mirrors the cost. A player's own page is index lookups off his colour
/// edges and stays interactive at 9,000 games. The ROSTER is a corpus-wide aggregate
/// over every game header there is (~400k rows for ~200k games, ~10s cold), so it is
/// cached exactly like the explore catalog: one flight fills it, everyone reads it
/// for the TTL, and a stale hit serves immediately while refreshing behind. It only
/// changes when a new PGN lands.
///
/// Searching never re-runs that aggregate either. A fully-spelled name resolves by
/// content address — chess_player_id() folds it the way the decomposer did and hashes
/// it — so finding a player is a lookup, not a scan, at any depth in the corpus. A
/// partial name falls back to a substring pass over the cached roster already in
/// memory. Neither path adds a query.
/// </summary>
internal sealed partial class SubstrateClient
{
    private const int RosterDepth = 1000;
    private static readonly TimeSpan RosterTtl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _rosterGate = new(1, 1);
    private IReadOnlyList<ChessPlayerRow>? _rosterCache;
    private DateTimeOffset _rosterCachedAt;

    /// <summary>The ranked roster, cached. Callers page it; they never re-aggregate.</summary>
    public async Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(CancellationToken ct)
    {
        var cached = _rosterCache;
        if (cached is not null && DateTimeOffset.UtcNow - _rosterCachedAt < RosterTtl)
            return cached;

        if (cached is not null)
        {
            // Stale: serve it now, refresh once behind. No page load should wait on
            // a ten-second corpus aggregate when last minute's ranking is on hand.
            if (_rosterGate.Wait(0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _rosterCache = await LoadRosterAsync(CancellationToken.None);
                        _rosterCachedAt = DateTimeOffset.UtcNow;
                    }
                    catch
                    {
                        // keep serving stale; the next expiry retries
                    }
                    finally
                    {
                        _rosterGate.Release();
                    }
                });
            }

            return cached;
        }

        await _rosterGate.WaitAsync(ct);
        try
        {
            if (_rosterCache is { } refilled && DateTimeOffset.UtcNow - _rosterCachedAt < RosterTtl)
                return refilled;

            var rows = await LoadRosterAsync(ct);
            _rosterCache = rows;
            _rosterCachedAt = DateTimeOffset.UtcNow;
            return rows;
        }
        finally
        {
            _rosterGate.Release();
        }
    }

    private async Task<IReadOnlyList<ChessPlayerRow>> LoadRosterAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT rank, encode(player_id, 'hex'), name,
                   games, wins, draws, losses, unscored, score
            FROM laplace.chess_leaderboard(@depth)
            """;
        return await ReadRowsAsync(sql,
            static r => new ChessPlayerRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                new ChessRecord(r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                    r.GetInt64(7), r.IsDBNull(8) ? null : r.GetDouble(8))),
            cmd => cmd.Parameters.AddWithValue("depth", RosterDepth),
            "chess_leaderboard", ct, timeoutSeconds: 120);
    }

    /// <summary>
    /// A page of the roster, or — when a name is supplied — the players it names.
    ///
    /// Two lookups, in that order of authority. The EXACT one is a content-address
    /// resolve: the typed name folded the decomposer's way and hashed, which finds
    /// the right man anywhere in the corpus, at any depth, in one round trip. It
    /// cannot be beaten for precision and it is put first for that reason.
    ///
    /// But it only fires on a name spelled the way the source spelled it, and a
    /// person typing "carls" is not wrong, just partial. So it is unioned with a
    /// substring pass over the ranked roster — which is already in memory, already
    /// carries resolved names, and costs nothing. No index, no scan, no new
    /// storage: the same cache the landing page pages through.
    ///
    /// The seam is honest rather than hidden. Substring reach ends where the cache
    /// does, so a partial that matches nobody ranked returns nothing even though an
    /// exact name would have found him — which is why the endpoint tells the caller
    /// how deep the ranked list goes, and the UI says so when a search comes up empty.
    /// </summary>
    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, CancellationToken ct)
    {
        var roster = await ChessRosterAsync(ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            var exact = await ChessFindPlayerAsync(needle, ct);

            IEnumerable<ChessPlayerRow> matches = roster
                .Where(p => p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Where(p => exact is null || !string.Equals(p.IdHex, exact.IdHex,
                                                StringComparison.OrdinalIgnoreCase));

            // The exact resolve leads: it is the one hit we know is the right entity
            // rather than a name that merely reads alike.
            if (exact is not null) matches = matches.Prepend(exact);

            var hits = matches.Take(Math.Clamp(limit, 1, 200)).ToList();
            return new ChessPlayersResponse("chess.players", hits.Count, 0, hits, roster.Count);
        }

        var page = roster.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return new ChessPlayersResponse("chess.players", roster.Count, Math.Max(0, offset), page,
            roster.Count);
    }

    /// <summary>
    /// Name to player, in one round trip. chess_player_id() reproduces the
    /// decomposer's own name folding ("Tal, Mikhail" -> "mikhail tal") and hashes
    /// the canonical key, so the typed name lands on the identical id the ingest
    /// wrote — or on nothing. A record with no games means no such player.
    /// </summary>
    public async Task<ChessPlayerRow?> ChessFindPlayerAsync(string name, CancellationToken ct)
    {
        const string sql = """
            WITH p AS (SELECT laplace.chess_player_id(@name) AS id)
            SELECT encode(p.id, 'hex'), laplace.label_or_hex(p.id),
                   r.games, r.wins, r.draws, r.losses, r.unscored, r.score
            FROM p, LATERAL laplace.chess_player_record(p.id) r
            WHERE r.as_white IS NULL
            """;
        var rows = await ReadRowsAsync(sql,
            static r => new ChessPlayerRow(0, r.GetString(0), r.GetString(1),
                new ChessRecord(r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetInt64(6), r.IsDBNull(7) ? null : r.GetDouble(7))),
            cmd => cmd.Parameters.AddWithValue("name", name),
            "chess_player_id", ct);
        var hit = rows.Count == 0 ? null : rows[0];
        return hit is { Record.Games: > 0 } ? hit : null;
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
            SELECT encode(opponent_id, 'hex'), opponent,
                   games, wins, draws, losses, unscored, score
            FROM laplace.chess_opponents(@id, @limit)
            """;
        var opponents = await ReadRowsAsync(oppSql,
            static r => new ChessOpponentRow(r.GetString(0), r.GetString(1),
                new ChessRecord(r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                    r.GetInt64(6), r.IsDBNull(7) ? null : r.GetDouble(7))),
            cmd =>
            {
                cmd.Parameters.Add("id", NpgsqlDbType.Bytea).Value = id;
                cmd.Parameters.AddWithValue("limit", Math.Clamp(opponentLimit, 1, 200));
            },
            "chess_opponents", ct);

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
        const string sql = """
            SELECT encode(game_id, 'hex'), played_on, event, eco, as_white,
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
