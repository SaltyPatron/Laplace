using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Per-band leaderboards, entity verdict records, and head-to-head matchups over consensus.
/// The comparator is chosen from each entity's stored type: two Chess_Player entities are
/// compared by their result standing and pairing cell; everything else by contrast.
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
    /// The entity's edges counted by verdict class. epistemic_status assigns the classes
    /// and groups the counts server-side; nothing is re-derived from μ here.
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

        // The comparator follows each entity's stored type, read before the tape is built.
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
    /// Player-vs-player tape: each side's source ratings and career result, plus the
    /// folded head-to-head pairing cell when the two have met.
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
        // Pairing cells are keyed by PlayedByType in either direction; each means the two
        // players met, and the count is the larger witness count of the two cells.
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
    /// The relation verdict between two references. Two Chess_Player entities are answered
    /// from their pairing cells; any other pair from relation_summary.
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