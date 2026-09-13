using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The league surface: per-band leaderboards, entity verdict records, and the
/// head-to-head matchup. Generic entities compare through converse.contrast; Chess_Player is
/// deliberately type-aware because lexical contrast excludes the chess evidence families and
/// source Elo is not the same quantity as consensus relation standing.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>Top consensus edges per salience band, fully labeled.</summary>
    public async Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.BandLeadersAsync(_dataSource, bands, perBand, ct, TranslateSubstrateError);

        var catalog = await RelationBandsAsync(ct);
        var names = catalog.ToDictionary(b => b.Band, b => b.Name);
        return rows.GroupBy(r => r.Band)
            .OrderBy(g => g.Key)
            .Select(g => new BandLeaders(g.Key, names.GetValueOrDefault(g.Key, $"band {g.Key}"),
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

    /// <summary>The fast half of a matchup: both cards plus the tale of the tape.</summary>
    public async Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct)
    {
        var x = await ResolveTopicAsync(xRef, ct);
        var y = await ResolveTopicAsync(yRef, ct);
        if (x is null || y is null) return null;

        var xHex = Convert.ToHexString(x.Value.Id).ToLowerInvariant();
        var yHex = Convert.ToHexString(y.Value.Id).ToLowerInvariant();

        var xSideTask = SideAsync(xHex, x.Value.Id, x.Value.Label, ct);
        var ySideTask = SideAsync(yHex, y.Value.Id, y.Value.Label, ct);
        await Task.WhenAll(xSideTask, ySideTask);
        var xSide = xSideTask.Result;
        var ySide = ySideTask.Result;

        IReadOnlyList<TapeRow> tape = xSide.Chess is not null && ySide.Chess is not null
            ? await ChessTapeAsync(x.Value.Id, y.Value.Id, xSide.Chess, ySide.Chess, ct)
            : await TapeAsync(x.Value.Id, y.Value.Id, ct);

        return new MatchupResponse("matchup", xSide, ySide, tape);
    }

    private async Task<MatchupSide> SideAsync(string hex, byte[] id, string label, CancellationToken ct)
    {
        var recordTask = EntityRecordAsync(hex, ct);
        var factsTask = NpgsqlSubstrateReads.SalientFactsAsync(_dataSource, id, 12, ct, TranslateSubstrateError);
        var chessTask = ChessMatchupSideAsync(id, ct);
        await Task.WhenAll(recordTask, factsTask, chessTask);
        return new MatchupSide(hex, label,
            recordTask.Result ?? new EntityRecordResponse("entity.record", hex, 0, 0, 0, 0),
            [.. factsTask.Result.Select(f => new SalientFactRow(f.Type, f.Fact, f.EffMu, f.Witnesses))],
            chessTask.Result);
    }

    private async Task<ChessMatchupSide?> ChessMatchupSideAsync(byte[] id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var facet = await NpgsqlDisplayLabels.FacetAsync(conn, id, ct);
        if (facet is not { Exists: true } f
            || !f.TypeId.AsSpan().SequenceEqual(ChessVocabulary.PlayerType.ToBytes()))
            return null;

        var recordTask = NpgsqlSubstrateReads.ChessPlayerRecordAsync(
            _dataSource, id, ct, TranslateReadError);
        var ratingsTask = NpgsqlSubstrateReads.ChessPlayerRatingsAsync(
            _dataSource, id, ct, TranslateReadError);
        await Task.WhenAll(recordTask, ratingsTask);

        var overall = recordTask.Result.FirstOrDefault(static r => r.AsWhite is null);
        int? peak = ratingsTask.Result.Count == 0 ? null : ratingsTask.Result[0].Rating;
        return new ChessMatchupSide(
            peak,
            overall.Games,
            overall.Wins,
            overall.Draws,
            overall.Losses,
            overall.Unscored,
            overall.Score);
    }

    private async Task<IReadOnlyList<TapeRow>> ChessTapeAsync(
        byte[] x,
        byte[] y,
        ChessMatchupSide xs,
        ChessMatchupSide ys,
        CancellationToken ct)
    {
        var rows = new List<TapeRow>(9);
        AddChessSide(rows, "x-only", xs);
        AddChessSide(rows, "y-only", ys);

        // Pairing evidence was historically stored under the badly named PLAYED_BY relation.
        // Do not expose that predicate here as English: its actual chess meaning is a direct
        // opponent meeting. The relation migration is handled separately; this read reports the
        // witnessed fact rather than repeating the ontology label into the product.
        var xId = Hash128.FromBytes(x);
        var yId = Hash128.FromBytes(y);
        var xy = ConsensusKeys.EdgeId(xId, ChessVocabulary.PlayedByType, yId);
        var yx = ConsensusKeys.EdgeId(yId, ChessVocabulary.PlayedByType, xId);
        var pair = await NpgsqlConsensusByIds.ReadAsync(
            _dataSource, [xy, yx], ChessVocabulary.PlayedByType, ct);
        long meetings = 0;
        if (pair.TryGetValue(xy, out var xr)) meetings = Math.Max(meetings, (long)Math.Round(xr.Witnesses));
        if (pair.TryGetValue(yx, out var yr)) meetings = Math.Max(meetings, (long)Math.Round(yr.Witnesses));
        if (meetings > 0)
            rows.Insert(0, new TapeRow("both", "direct meetings", $"{meetings:N0} witnessed games", null));

        return rows;
    }

    private static void AddChessSide(List<TapeRow> rows, string holder, ChessMatchupSide side)
    {
        if (side.PeakSourceElo is { } elo)
            rows.Add(new TapeRow(holder, "peak source Elo", elo.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
        rows.Add(new TapeRow(holder, "career record",
            $"{side.Wins:N0}-{side.Draws:N0}-{side.Losses:N0} over {side.Games:N0} witnessed games", null));
        if (side.Score is { } score)
            rows.Add(new TapeRow(holder, "career score", $"{score * 100d:F1}%", null));
        if (side.Unscored > 0)
            rows.Add(new TapeRow(holder, "unscored games", side.Unscored.ToString("N0"), null));
    }

    private async Task<IReadOnlyList<TapeRow>> TapeAsync(byte[] x, byte[] y, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.ContrastAsync(_dataSource, x, y, 60, ct, TranslateSubstrateError);
        return [.. rows.Select(r => new TapeRow(r.Holder, r.Type, r.Fact, r.Mu))];
    }

    /// <summary>
    /// The slow half: relation_summary's path search and verdict. Measured
    /// 6–14s under an active seed — served separately so the tape never waits.
    /// </summary>
    public async Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct)
    {
        var x = await ResolveTopicAsync(xRef, ct);
        var y = await ResolveTopicAsync(yRef, ct);
        if (x is null || y is null) return null;

        var s = await NpgsqlSubstrateReads.RelationSummaryAsync(_dataSource, x.Value.Id, y.Value.Id, ct, TranslateSubstrateError);
        return new MatchupVerdictResponse("matchup.verdict",
            s?.Relation, s?.Plane, s?.Mu, s?.Usage, s?.Geodesic, s?.Verdict);
    }

    /// <summary>
    /// A source's roster: a bounded sample of what it witnessed, fully labeled.
    /// By id (from the catalog row) so the hot path never re-runs the
    /// source_counts aggregate; the per-leaf source_id indexes make the sampled
    /// scan ~1s cold. A sample is the honest bounded read — "top" would demand
    /// an unbounded sort over millions of rows.
    /// </summary>
    public async Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.SourceRosterAsync(_dataSource, sourceId, limit, ct, TranslateSubstrateError);
        return [.. rows.Select(r => new SourceRosterRow(r.SubjectIdHex, r.Subject, r.Relation, r.ObjectIdHex, r.Object, r.Observations))];
    }
}
