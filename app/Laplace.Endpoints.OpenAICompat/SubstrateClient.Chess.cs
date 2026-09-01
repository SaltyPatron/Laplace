using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using System.Globalization;
using System.Text;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The chess reading room: who played, what they played, and how it went. Chess is
/// the proving domain because its ground truth is objectively checkable, and this is
/// where that shows — every number served here is counted off game headers the PGN
/// decomposer transcribed, so a career record can be checked against the games it came
/// from, and every game mainline can be replayed from its typed move trajectory.
///
/// NOTHING HERE IS CACHED, and that is the point. The roster used to be a corpus-wide
/// GROUP BY over every game header (~400k rows, ~10s) hidden behind a TTL cache and a
/// startup prewarm — a cache standing in for a fold. Each game now carries its result
/// onto the player in the aggregating lane at ingest, so a record is one consensus
/// cell: witness_count IS games played, eff_mu IS the conservative strength. Ranking
/// is an ORDER BY over a single relation partition. No TTL, no prewarm, no stale
/// window, nothing to repopulate.
///
/// Exact names still use their content address. Partial names and nearby spellings
/// use the indexed constituent set of name trajectories to find a bounded candidate
/// set, then apply human-name matching. That reaches beyond the first ranked page
/// without turning the player index into a corpus-wide rendered substring scan.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>
    /// The ranked roster, straight off the folded cells. Paging is an OFFSET over an index.
    /// </summary>
    public Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(
        int limit, int offset, CancellationToken ct)
        => ChessRosterAsync(limit, offset, "strength", "desc", ct);

    private async Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(
        int limit, int offset, string sort, string direction, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ChessRankedAsync(
            _dataSource, limit, offset, sort, direction, ct, TranslateReadError);
        return rows.Select(static r => new ChessPlayerRow(
            r.Rank, r.IdHex, r.Name, r.Games, r.Rating, r.Rd, r.EffMu)).ToList();
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
        string initial, int limit, int offset, string sort, string direction,
        CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ChessPlayersByInitialAsync(
            _dataSource, initial, limit, offset, sort, direction, ct, TranslateReadError);

        // Rank is positional within the letter; the fold's ordering already decided it.
        return [.. rows.Select((r, i) => new ChessPlayerRow(
            offset + i + 1, r.IdHex, r.Name, r.Games, r.Rating, r.Rd, r.EffMu))];
    }

    /// <summary>
    /// A page of the sortable roster, or fuzzy-ranked player matches when a name is supplied.
    /// </summary>
    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, CancellationToken ct)
        => await ChessPlayersAsync(limit, offset, search, null, null, null, ct);

    /// <inheritdoc cref="ChessPlayersAsync(int,int,string?,CancellationToken)"/>
    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, string? initial, CancellationToken ct)
        => await ChessPlayersAsync(limit, offset, search, initial, null, null, ct);

    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, string? initial,
        string? sort, string? direction, CancellationToken ct)
    {
        limit = Math.Max(0, limit);
        offset = Math.Max(0, offset);
        var normalizedSort = NormalizePlayerSort(
            sort, hasSearch: !string.IsNullOrWhiteSpace(search));
        var normalizedDirection = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase)
            ? "asc"
            : "desc";

        if (!string.IsNullOrWhiteSpace(initial))
        {
            var sqlSort = normalizedSort == "relevance" ? "strength" : normalizedSort;
            var letter = await ChessPlayersByInitialAsync(
                initial, limit, offset, sqlSort, normalizedDirection, ct);
            return new ChessPlayersResponse("chess.players", letter.Count, offset, letter);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            var canonical = PlayerAlias.Canonical(query);
            var candidates = await NpgsqlSubstrateReads.ChessPlayerSearchCandidatesAsync(
                _dataSource,
                [query, canonical, canonical.Replace(" ", "", StringComparison.Ordinal)],
                2000, ct, TranslateReadError);
            var exact = await ChessFindPlayerAsync(query, ct);
            var scored = candidates
                .Select(static r => new ChessPlayerRow(
                    0, r.IdHex, r.Name, r.Games, r.Rating, r.Rd, r.EffMu))
                .Concat(exact is null ? [] : [exact])
                .DistinctBy(static p => p.IdHex, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Player: p, Score: PlayerSearchScore(query, p.Name)))
                .Where(static x => x.Score < int.MaxValue)
                .ToList();

            IEnumerable<(ChessPlayerRow Player, int Score)> ordered = normalizedSort switch
            {
                "games" => OrderPlayers(scored, normalizedDirection, static x => x.Player.Games),
                "rating" => OrderPlayers(scored, normalizedDirection, static x => x.Player.Rating),
                "rd" => OrderPlayers(scored, normalizedDirection, static x => x.Player.Rd),
                "strength" => OrderPlayers(scored, normalizedDirection, static x => x.Player.EffMu),
                _ => scored.OrderBy(static x => x.Score)
                    .ThenByDescending(static x => x.Player.EffMu)
                    .ThenBy(static x => x.Player.Name, StringComparer.OrdinalIgnoreCase),
            };

            var page = ordered.Skip(offset).Take(limit)
                .Select((x, i) => x.Player with { Rank = offset + i + 1 })
                .ToList();
            return new ChessPlayersResponse("chess.players", scored.Count, offset, page);
        }

        var rosterSort = normalizedSort == "relevance" ? "strength" : normalizedSort;
        var roster = await ChessRosterAsync(limit, offset, rosterSort, normalizedDirection, ct);
        return new ChessPlayersResponse("chess.players", roster.Count, offset, roster);
    }

    private static string NormalizePlayerSort(string? sort, bool hasSearch) =>
        sort?.ToLowerInvariant() switch
        {
            "games" => "games",
            "rating" => "rating",
            "rd" => "rd",
            "strength" => "strength",
            "relevance" when hasSearch => "relevance",
            _ => hasSearch ? "relevance" : "strength",
        };

    private static IEnumerable<(ChessPlayerRow Player, int Score)> OrderPlayers(
        IEnumerable<(ChessPlayerRow Player, int Score)> players,
        string direction,
        Func<(ChessPlayerRow Player, int Score), double> selector) =>
        direction == "asc"
            ? players.OrderBy(selector).ThenBy(static x => x.Player.Name, StringComparer.OrdinalIgnoreCase)
            : players.OrderByDescending(selector).ThenBy(static x => x.Player.Name, StringComparer.OrdinalIgnoreCase);

    internal static int PlayerSearchScore(string query, string candidate)
    {
        var q = NormalizePlayerSearch(query);
        var name = NormalizePlayerSearch(candidate);
        if (q.Length == 0 || name.Length == 0) return int.MaxValue;
        if (name == q) return 0;

        // Provider handles commonly concatenate the same real name that FIDE writes as
        // "Last, First". Identity search must preserve that equivalence instead of finding
        // the candidate in SQL and then rejecting it in the endpoint ranker.
        var compactQuery = q.Replace(" ", "", StringComparison.Ordinal);
        var compactName = name.Replace(" ", "", StringComparison.Ordinal);
        if (compactName == compactQuery) return 1;

        var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameTokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nameTokens.Contains(q, StringComparer.Ordinal)) return 2;
        if (name.Contains(q, StringComparison.Ordinal)) return 4;
        if (qTokens.All(t => nameTokens.Contains(t, StringComparer.Ordinal))) return 6;

        var distance = 0;
        foreach (var token in qTokens)
        {
            var closest = nameTokens.Min(n => Levenshtein(token, n));
            var allowance = token.Length >= 8 ? 2 : token.Length >= 4 ? 1 : 0;
            if (closest > allowance) return int.MaxValue;
            distance += closest;
        }
        return 10 + distance;
    }

    private static string NormalizePlayerSearch(string value)
    {
        string canonical = PlayerAlias.Canonical(value);
        var decomposed = canonical.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(ch);
        return result.ToString();
    }

    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    /// <summary>
    /// Name to player, in one round trip. chess.player_id() reproduces the decomposer's own
    /// name folding ("Tal, Mikhail" -> "mikhail tal") and hashes the canonical key, so the
    /// typed name lands on the identical id the ingest wrote — or on nothing. The rating comes
    /// from his folded cell; no cell means the substrate has never witnessed him at a board.
    /// </summary>
    public async Task<ChessPlayerRow?> ChessFindPlayerAsync(string name, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ChessFindPlayerAsync(
            _dataSource, name, ct, TranslateReadError);
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new ChessPlayerRow(0, r.IdHex, r.Name, r.Games, r.Rating, r.Rd, r.EffMu);
    }

    /// <summary>The career page: record by colour, the Elo the source tagged, the rivals.</summary>
    public async Task<ChessPlayerResponse?> ChessPlayerAsync(string idHex, int opponentLimit, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        var record = await NpgsqlSubstrateReads.ChessPlayerRecordAsync(
            _dataSource, id, ct, TranslateReadError);
        var overall = MapRecord(record.FirstOrDefault(x => x.AsWhite is null));

        var ratings = await NpgsqlSubstrateReads.ChessPlayerRatingsAsync(
            _dataSource, id, ct, TranslateReadError);
        var ratingRows = ratings.Select(static r => new ChessRatingRow(r.Rating, r.Games)).ToList();

        var opponents = await NpgsqlSubstrateReads.ChessHeadToHeadAsync(
            _dataSource, id, opponentLimit, ct, TranslateReadError);
        var opponentRows = opponents.Select(static r => new ChessOpponentRow(
            r.OpponentIdHex, r.Opponent, r.Games, r.Rating, r.Rd, r.EffMu)).ToList();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var (name, _, type, exists) = await ReadEntityFacetsAsync(conn, id, ct);
        if (!exists || !string.Equals(type, "Chess_Player", StringComparison.Ordinal))
            return null;
        var profileEdges = await NpgsqlSubstrateReads.ChessPlayerProfileEdgesAsync(
            conn, id, ct, TranslateReadError);
        var profiles = MapChessProfiles(profileEdges);

        return new ChessPlayerResponse("chess.player", idHex.ToLowerInvariant(), name ?? idHex,
            overall,
            MapRecord(record.FirstOrDefault(x => x.AsWhite is true)),
            MapRecord(record.FirstOrDefault(x => x.AsWhite is false)),
            // Peak is the highest Elo any source ever tagged him with. Ratings come
            // back rating-descending, so it is the first row — no client-side max.
            ratingRows.Count == 0 ? null : ratingRows[0].Rating,
            ratingRows, opponentRows, profiles);
    }

    private static IReadOnlyList<ChessIdentityProfile> MapChessProfiles(
        IReadOnlyList<NpgsqlSubstrateReads.ChessProfileEdgeRow> edges)
    {
        var profiles = new List<ChessIdentityProfile>();
        foreach (var group in edges.GroupBy(static e => e.SubjectIdHex, StringComparer.OrdinalIgnoreCase))
        {
            string? externalId = group.FirstOrDefault(static e =>
                e.Relation.Equals(ChessSeedManifest.Relations[^4], StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(externalId)) continue;
            int separator = externalId.IndexOf(':');
            if (separator <= 0 || separator == externalId.Length - 1) continue;
            string provider = externalId[..separator];
            string providerId = externalId[(separator + 1)..];
            var aliases = group.Where(static e =>
                    e.Relation.Equals(ChessSeedManifest.Relations[^1], StringComparison.OrdinalIgnoreCase))
                .Select(static e => e.Value).Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<string>();
            string? title = null, federation = null, biography = null, avatar = null;
            foreach (var edge in group)
            {
                if (edge.Relation.Equals(ChessSeedManifest.Relations[3], StringComparison.OrdinalIgnoreCase))
                {
                    int colon = edge.Value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(edge.Value[(colon + 1)..], out int rating))
                        ratings[edge.Value[..colon]] = rating;
                    continue;
                }
                if (!edge.Relation.Equals(ChessSeedManifest.Relations[^3], StringComparison.OrdinalIgnoreCase)) continue;
                if (TakePrefixed(edge.Value, "title", out var value)) title = value;
                else if (TakePrefixed(edge.Value, "federation", out value)) federation = value;
                else if (TakePrefixed(edge.Value, "bio", out value)) biography = value;
                else if (TakePrefixed(edge.Value, "avatar", out value)) avatar = value;
                else if (TakePrefixed(edge.Value, "link", out value)) links.Add(value);
                else if (edge.Value.StartsWith("fact:", StringComparison.OrdinalIgnoreCase))
                {
                    int keyEnd = edge.Value.IndexOf(':', 5);
                    if (keyEnd > 5 && keyEnd < edge.Value.Length - 1)
                        facts[edge.Value[5..keyEnd]] = edge.Value[(keyEnd + 1)..];
                }
            }
            string display = aliases.FirstOrDefault(a =>
                !a.Equals(providerId, StringComparison.OrdinalIgnoreCase)) ?? aliases.FirstOrDefault() ?? providerId;
            profiles.Add(new ChessIdentityProfile(
                group.Key, provider, providerId, display, aliases, title, federation,
                biography, avatar, links.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ratings, facts));
        }
        return profiles.OrderBy(static p => p.Provider.Equals("fide", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(static p => p.Provider, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TakePrefixed(string text, string prefix, out string value)
    {
        string marker = prefix + ":";
        if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            value = text[marker.Length..];
            return true;
        }
        value = "";
        return false;
    }

    private static ChessRecord MapRecord(NpgsqlSubstrateReads.ChessPlayerRecordRow row)
        => new(row.Games, row.Wins, row.Draws, row.Losses, row.Unscored, row.Score);

    /// <summary>A page of one player's game log, most recent first.</summary>
    public async Task<ChessGamesResponse?> ChessPlayerGamesAsync(
        string idHex, int limit, int offset, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;
        var games = await NpgsqlSubstrateReads.ChessPlayerGamesAsync(
            _dataSource, id, limit, offset, ct, TranslateReadError);
        var rows = games.Select(static r => new ChessGameRow(
            r.EventIdHex, r.PlayedOn, r.Event, r.Eco, r.AsWhite,
            r.OpponentIdHex, r.Opponent, r.Result, r.Outcome)).ToList();

        return new ChessGamesResponse("chess.games", idHex.ToLowerInvariant(),
            Math.Max(0, offset), rows);
    }

    /// <summary>
    /// Reconstruct boards from the playing's associated line trajectory. SAN/PGN/FEN are output
    /// encodings generated here; none is the stored identity of the moves or positions.
    /// </summary>
    public async Task<ChessGamePliesResponse?> ChessGamePliesAsync(string idHex, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;

        var rows = await NpgsqlSubstrateReads.ChessGameAsync(
            _dataSource, id, ct, TranslateReadError);
        if (rows.Count == 0) return null;
        var replay = await ChessTypedReplayAsync(id, ct);
        return new ChessGamePliesResponse("chess.game.plies", idHex.ToLowerInvariant(),
            replay.StartFen, replay.HasClocks, replay.Truncated,
            [.. replay.Plies.Select(p => new ChessPlyRow(
                p.Ply, p.San, p.Uci, p.Fen, p.WhiteMoved, p.ClockSeconds, p.PositionId))]);
    }

    /// <summary>One game: source headers plus generated PGN from its typed move trajectory.</summary>
    public async Task<ChessGameResponse?> ChessGameAsync(string idHex, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;
        var rows = await NpgsqlSubstrateReads.ChessGameAsync(
            _dataSource, id, ct, TranslateReadError);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var replay = await ChessTypedReplayAsync(id, ct);
        string movetext = Laplace.Chess.Service.ChessReplay.ToMovetext(replay, r.Result);
        return new ChessGameResponse("chess.game", idHex.ToLowerInvariant(),
            r.WhiteIdHex, r.White, r.BlackIdHex, r.Black,
            r.Result, r.PlayedOn, r.Event, r.Eco,
            r.Termination, r.TimeControl, r.TcClass, movetext);
    }

    private async Task<Laplace.Chess.Service.ChessReplayResult> ChessTypedReplayAsync(
        byte[] playingId, CancellationToken ct)
    {
        byte[]? lineId = await NpgsqlSubstrateReads.ChessLineForPlayingAsync(
            _dataSource, playingId, ct, TranslateReadError);
        if (lineId is null)
            return Laplace.Chess.Service.ChessReplay.Replay(Array.Empty<Laplace.Engine.Core.Hash128>());
        var rows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync(
            _dataSource, [lineId, playingId],
            [PhysicalityType.Content, PhysicalityType.ChessComment],
            ct, TranslateReadError);
        var moves = rows.Where(r => r.Type == PhysicalityType.Content
                                  && r.ParentId.AsSpan().SequenceEqual(lineId))
            .OrderBy(static r => r.Ordinal)
            .Select(static r => Laplace.Engine.Core.Hash128.FromBytes(r.EntityId)).ToArray();
        byte[]? setupId = await NpgsqlSubstrateReads.ChessSetupPositionIdAsync(
            _dataSource, playingId, ct, TranslateReadError);
        string? setup = null;
        if (setupId is not null)
        {
            var setupRows = await NpgsqlSubstrateReads.NestedTrajectoryConstituentsAsync(
                _dataSource, [setupId], ct, TranslateReadError);
            var boards = Laplace.Chess.Service.ChessPositionTrajectory.Decode(setupRows);
            if (boards.TryGetValue(Laplace.Engine.Core.Hash128.FromBytes(setupId), out var board))
                setup = board.ToFen();
        }
        var replay = Laplace.Chess.Service.ChessReplay.Replay(moves, setup);
        var commentRows = rows.Where(r => r.Type == PhysicalityType.ChessComment
                                         && r.ParentId.AsSpan().SequenceEqual(playingId))
            .OrderBy(static r => r.Ordinal).ToArray();
        if (commentRows.Length == moves.Length && commentRows.Length > 0)
        {
            var commentIds = commentRows.Select(static r => r.EntityId).ToArray();
            var rendered = await NpgsqlSubstrateReads.RenderTextBatchAsync(
                _dataSource, commentIds, ct, TranslateReadError);
            if (rendered is not null && rendered.Length == commentRows.Length)
                replay = Laplace.Chess.Service.ChessReplay.ApplyClockComments(replay, rendered);
        }
        return replay;
    }
}
