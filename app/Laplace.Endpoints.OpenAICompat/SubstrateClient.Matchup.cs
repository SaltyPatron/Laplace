using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The league surface: per-band leaderboards, entity verdict records, and the
/// head-to-head matchup. The generic graph remains the default, but a Chess_Player
/// matchup must not pretend lexical contrast is a chess comparator or label generic
/// consensus standing as source Elo.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>Top consensus edges per salience band, fully labeled.</summary>
    public async Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.BandLeadersNamedAsync(
            _dataSource, bands, perBand, ct, TranslateSubstrateError).ConfigureAwait(false);

        return rows.GroupBy(r => new { r.Band, r.BandName })
            .OrderBy(g => g.Key.Band)
            .Select(g => new BandLeaders(g.Key.Band, g.Key.BandName,
                [.. g.Select(r => new LeaderRow(r.SubjectIdHex, r.Subject, r.Relation, r.ObjectIdHex, r.Object, r.EffMu, r.Witnesses))]))
            .ToList();
    }

    /// <summary>
    /// The entity's record: its edges scored by the canonical verdict logic.
    /// epistemic_status IS that logic — the counts are grouped server-side and
    /// never re-derived from raw μ in a client.
    /// </summary>
    public async Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct)
    {
        if (TryParseIdHex(idHex) is not { } id) return null;
        var (c, x, f, t) = await NpgsqlSubstrateReads.EntityRecordAsync(_dataSource, id, ct, TranslateSubstrateError);
        return new EntityRecordResponse("entity.record", idHex.ToLowerInvariant(), c, x, f, t);
    }

    /// <summary>The fast half of a matchup: both cards plus a type-appropriate tape.</summary>
    public async Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct)
    {
        var xTask = ResolveTopicAsync(xRef, ct);
        var yTask = ResolveTopicAsync(yRef, ct);
        await Task.WhenAll(xTask, yTask).ConfigureAwait(false);
        var x = xTask.Result;
        var y = yTask.Result;
        if (x is null || y is null) return null;

        var xHex = Convert.ToHexString(x.Value.Id).ToLowerInvariant();
        var yHex = Convert.ToHexString(y.Value.Id).ToLowerInvariant();

        // Type is content state, not a route hint. Determine it from the entity itself before
        // selecting the comparator; two chess players must not fall into lexical contrast().
        var xChessTask = IsChessPlayerAsync(x.Value.Id, ct);
        var yChessTask = IsChessPlayerAsync(y.Value.Id, ct);
        await Task.WhenAll(xChessTask, yChessTask).ConfigureAwait(false);
        bool xChess = xChessTask.Result;
        bool yChess = yChessTask.Result;

        var tapeTask = xChess && yChess
            ? ChessTapeAsync(x.Value.Id, x.Value.Label, y.Value.Id, y.Value.Label, ct)
            : TapeAsync(x.Value.Id, y.Value.Id, ct);
        var xSideTask = SideAsync(xHex, x.Value.Id, x.Value.Label, xChess, ct);
        var ySideTask = SideAsync(yHex, y.Value.Id, y.Value.Label, yChess, ct);
        await Task.WhenAll(tapeTask, xSideTask, ySideTask).ConfigureAwait(false);

        return new MatchupResponse("matchup", xSideTask.Result, ySideTask.Result, tapeTask.Result);
    }

    private async Task<bool> IsChessPlayerAsync(byte[] id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var facet = await NpgsqlDisplayLabels.FacetAsync(
            conn, id, ct, TranslateSubstrateError).ConfigureAwait(false);
        return facet is { Exists: true } f
            && f.TypeId.AsSpan().SequenceEqual(ChessVocabulary.PlayerType.ToBytes());
    }

    private async Task<MatchupSide> SideAsync(
        string hex, byte[] id, string label, bool chessPlayer, CancellationToken ct)
    {
        var recordTask = EntityRecordAsync(hex, ct);
        var factsTask = NpgsqlSubstrateReads.SalientFactsAsync(
            _dataSource, id, 6, ct, TranslateSubstrateError);
        var ratingsTask = chessPlayer
            ? NpgsqlSubstrateReads.ChessPlayerRatingsAsync(
                _dataSource, id, ct, TranslateSubstrateError)
            : Task.FromResult<IReadOnlyList<NpgsqlSubstrateReads.ChessPlayerRatingRow>>([]);
        var chessRecordTask = chessPlayer
            ? NpgsqlSubstrateReads.ChessPlayerRecordAsync(
                _dataSource, id, ct, TranslateSubstrateError)
            : Task.FromResult<IReadOnlyList<NpgsqlSubstrateReads.ChessPlayerRecordRow>>([]);
        await Task.WhenAll(recordTask, factsTask, ratingsTask, chessRecordTask).ConfigureAwait(false);

        var ratings = ratingsTask.Result;
        int? peak = ratings.Count == 0 ? null : ratings[0].Rating;
        long observations = ratings.Sum(static r => r.Games);
        var overall = chessRecordTask.Result.FirstOrDefault(static r => r.AsWhite is null);
        ChessMatchupSide? chess = chessPlayer
            ? new ChessMatchupSide(
                peak,
                overall.Games,
                overall.Wins,
                overall.Draws,
                overall.Losses,
                overall.Unscored,
                overall.Score)
            : null;

        return new MatchupSide(hex, label,
            recordTask.Result ?? new EntityRecordResponse("entity.record", hex, 0, 0, 0, 0),
            [.. factsTask.Result.Select(f => new SalientFactRow(f.Type, f.Fact, f.EffMu, f.Witnesses))],
            chessPlayer ? "Chess_Player" : null,
            peak,
            observations,
            chess);
    }

    private async Task<IReadOnlyList<TapeRow>> TapeAsync(byte[] x, byte[] y, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ContrastAsync(_dataSource, x, y, 60, ct, TranslateSubstrateError);
        return [.. rows.Select(r => new TapeRow(r.Holder, r.Type, r.Fact, r.Mu))];
    }

    /// <summary>
    /// Player-vs-player comparison over chess-owned folds. This is intentionally small and exact:
    /// source Elo, career result, and their already-folded pairing cell. It replaces the empty
    /// lexical contrast result without inventing a second chess database. Rich opening/time/book
    /// planes can join this same typed comparator as their ingest folds land.
    /// </summary>
    private async Task<IReadOnlyList<TapeRow>> ChessTapeAsync(
        byte[] x, string xLabel, byte[] y, string yLabel, CancellationToken ct)
    {
        var xRatingsTask = NpgsqlSubstrateReads.ChessPlayerRatingsAsync(
            _dataSource, x, ct, TranslateSubstrateError);
        var yRatingsTask = NpgsqlSubstrateReads.ChessPlayerRatingsAsync(
            _dataSource, y, ct, TranslateSubstrateError);
        var xRecordTask = NpgsqlSubstrateReads.ChessPlayerRecordAsync(
            _dataSource, x, ct, TranslateSubstrateError);
        var yRecordTask = NpgsqlSubstrateReads.ChessPlayerRecordAsync(
            _dataSource, y, ct, TranslateSubstrateError);
        var meetingsTask = NpgsqlSubstrateReads.ChessHeadToHeadAsync(
            _dataSource, x, 512, ct, TranslateSubstrateError);
        await Task.WhenAll(
            xRatingsTask, yRatingsTask, xRecordTask, yRecordTask, meetingsTask)
            .ConfigureAwait(false);

        var tape = new List<TapeRow>(5);
        AppendRating(tape, "x-only", xRatingsTask.Result);
        AppendRating(tape, "y-only", yRatingsTask.Result);
        AppendCareer(tape, "x-only", xRecordTask.Result);
        AppendCareer(tape, "y-only", yRecordTask.Result);

        string yHex = Convert.ToHexString(y).ToLowerInvariant();
        var meeting = meetingsTask.Result.FirstOrDefault(r =>
            string.Equals(r.OpponentIdHex, yHex, StringComparison.OrdinalIgnoreCase));
        if (meeting.Games > 0)
        {
            tape.Add(new TapeRow(
                "both",
                "played against",
                $"{xLabel} vs {yLabel}: {meeting.Games} witnessed games",
                (decimal)meeting.EffMu));
        }
        return tape;
    }

    private static void AppendRating(
        List<TapeRow> tape, string holder,
        IReadOnlyList<NpgsqlSubstrateReads.ChessPlayerRatingRow> ratings)
    {
        if (ratings.Count == 0) return;
        long observations = ratings.Sum(static r => r.Games);
        tape.Add(new TapeRow(
            holder,
            "source Elo",
            $"peak {ratings[0].Rating} · {observations} rating observations",
            null));
    }

    private static void AppendCareer(
        List<TapeRow> tape, string holder,
        IReadOnlyList<NpgsqlSubstrateReads.ChessPlayerRecordRow> records)
    {
        var overall = records.FirstOrDefault(static r => r.AsWhite is null);
        if (overall.Games <= 0) return;
        tape.Add(new TapeRow(
            holder,
            "career result",
            $"{overall.Wins}W {overall.Draws}D {overall.Losses}L · {overall.Games} games",
            overall.Score is { } s ? (decimal)(s * 100.0) : null));
    }

    private async Task<long> ChessMeetingsAsync(byte[] x, byte[] y, CancellationToken ct)
    {
        // Pairing evidence was historically stored under the badly named PLAYED_BY relation.
        // Treat it according to its actual chess grain here (player met opponent), never as an
        // English assertion that the opponent somehow "played" the player.
        var xId = Hash128.FromBytes(x);
        var yId = Hash128.FromBytes(y);
        var xy = ConsensusKeys.EdgeId(xId, ChessVocabulary.PlayedByType, yId);
        var yx = ConsensusKeys.EdgeId(yId, ChessVocabulary.PlayedByType, xId);
        var pair = await NpgsqlConsensusByIds.ReadAsync(
            _dataSource, [xy, yx], ChessVocabulary.PlayedByType, ct).ConfigureAwait(false);
        long meetings = 0;
        if (pair.TryGetValue(xy, out var xr)) meetings = Math.Max(meetings, (long)Math.Round(xr.Witnesses));
        if (pair.TryGetValue(yx, out var yr)) meetings = Math.Max(meetings, (long)Math.Round(yr.Witnesses));
        return meetings;
    }

    /// <summary>
    /// Domain-specific verdicts must use the domain's witnessed evidence. Sending Chess_Player
    /// through lexical relation_summary produced "no witnessed conceptual path" even for players
    /// with directly witnessed games against one another.
    /// </summary>
    public async Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct)
    {
        var xTask = ResolveTopicAsync(xRef, ct);
        var yTask = ResolveTopicAsync(yRef, ct);
        await Task.WhenAll(xTask, yTask).ConfigureAwait(false);
        var x = xTask.Result;
        var y = yTask.Result;
        if (x is null || y is null) return null;

        var xChessTask = IsChessPlayerAsync(x.Value.Id, ct);
        var yChessTask = IsChessPlayerAsync(y.Value.Id, ct);
        await Task.WhenAll(xChessTask, yChessTask).ConfigureAwait(false);
        if (xChessTask.Result && yChessTask.Result)
        {
            long meetings = await ChessMeetingsAsync(x.Value.Id, y.Value.Id, ct).ConfigureAwait(false);
            return meetings > 0
                ? new MatchupVerdictResponse(
                    "matchup.verdict", "played against", "chess pairing", null,
                    meetings, null, $"{meetings:N0} witnessed direct games")
                : new MatchupVerdictResponse(
                    "matchup.verdict", "Chess_Player", "chess career", null,
                    0, null, "no direct games witnessed");
        }

        var s = await NpgsqlSubstrateReads.RelationSummaryAsync(_dataSource, x.Value.Id, y.Value.Id, ct, TranslateSubstrateError);
        return new MatchupVerdictResponse("matchup.verdict",
            s?.Relation, s?.Plane, s?.Mu, s?.Usage, s?.Geodesic, s?.Verdict);
    }

    /// <summary>
    /// A source's roster: a bounded sample of what it witnessed, fully labeled.
    /// </summary>
    public async Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SourceRosterAsync(_dataSource, sourceId, limit, ct, TranslateSubstrateError);
        return [.. rows.Select(r => new SourceRosterRow(r.SubjectIdHex, r.Subject, r.Relation, r.ObjectIdHex, r.Object, r.Observations))];
    }
}