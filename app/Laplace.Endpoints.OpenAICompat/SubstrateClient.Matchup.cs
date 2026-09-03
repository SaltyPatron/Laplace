using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The league surface: per-band leaderboards, entity verdict records, and the
/// head-to-head matchup. The rating math is a literal sports rating — Glicko-2 —
/// so this presents it as one: leaders per arena, games played, win/loss record.
/// Split into fast reads (leaders, record, tape) and the slow path/verdict read,
/// which the UI fetches lazily; path search competes with active seeds for the
/// box, so it must never block the parts that return in a second. The SQL
/// itself lives in the sanctioned Npgsql read layer; consumers do not maintain
/// private SQL or realization policy.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>
    /// Top consensus edges per salience band with the universal realization receipt
    /// and all three Glicko display coordinates kept distinct.
    /// </summary>
    public async Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct)
    {
        var rows = await NpgsqlLeaderboardReads.BandLeadersAsync(
            _dataSource,
            bands,
            perBand,
            languageId: null,
            ct: ct,
            onError: TranslateSubstrateError);

        var catalog = await RelationBandsAsync(ct);
        var names = catalog.ToDictionary(b => b.Band, b => b.Name);
        return rows.GroupBy(r => r.Band)
            .OrderBy(g => g.Key)
            .Select(g => new BandLeaders(g.Key, names.GetValueOrDefault(g.Key, $"band {g.Key}"),
                [.. g.Select(r => new LeaderRow(
                    r.SubjectIdHex,
                    r.Subject,
                    r.Relation,
                    r.ObjectIdHex,
                    r.Object,
                    r.EffMu,
                    r.Witnesses)
                {
                    SubjectRealization = r.SubjectRealization,
                    SubjectTechnicalName = r.SubjectTechnicalName,
                    SubjectTypeId = r.SubjectTypeIdHex,
                    SubjectTypeName = r.SubjectTypeName,
                    RelationId = r.RelationIdHex,
                    RelationRealization = r.RelationRealization,
                    RelationTechnicalName = r.RelationTechnicalName,
                    ObjectRealization = r.ObjectRealization,
                    ObjectTechnicalName = r.ObjectTechnicalName,
                    ObjectTypeId = r.ObjectTypeIdHex,
                    ObjectTypeName = r.ObjectTypeName,
                    Rating = r.Rating,
                    Rd = r.Rd,
                })]))
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

        // The three cheap reads are independent — run them together.
        var tapeTask = TapeAsync(x.Value.Id, y.Value.Id, ct);
        var xSideTask = SideAsync(xHex, x.Value.Id, x.Value.Label, ct);
        var ySideTask = SideAsync(yHex, y.Value.Id, y.Value.Label, ct);
        await Task.WhenAll(tapeTask, xSideTask, ySideTask);

        return new MatchupResponse("matchup", xSideTask.Result, ySideTask.Result, tapeTask.Result);
    }

    private async Task<MatchupSide> SideAsync(string hex, byte[] id, string label, CancellationToken ct)
    {
        var recordTask = EntityRecordAsync(hex, ct);
        var factsTask = NpgsqlSubstrateReads.SalientFactsAsync(_dataSource, id, 6, ct, TranslateSubstrateError);
        await Task.WhenAll(recordTask, factsTask);
        return new MatchupSide(hex, label,
            recordTask.Result ?? new EntityRecordResponse("entity.record", hex, 0, 0, 0, 0),
            [.. factsTask.Result.Select(f => new SalientFactRow(f.Type, f.Fact, f.EffMu, f.Witnesses))]);
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
