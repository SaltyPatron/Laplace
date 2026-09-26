using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Shape-dispatched structural reads. Every dial the native functions accept is passed
/// through from the caller's <see cref="QueryDials"/>.
/// </summary>
internal sealed partial class SubstrateClient
{
    /// <summary>Read shapes, straight from the substrate's own catalog.</summary>
    public async Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.QueryShapesAsync(_dataSource, ct, TranslateReadError);
        return rows.Select(static r => new QueryShape(
            r.Shape, r.Summary, r.NeedsTopic2, r.NeedsType, r.AcceptsLang)).ToList();
    }

    /// <summary>Salience bands with live consensus counts.</summary>
    public async Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.RelationBandsAsync(_dataSource, ct, TranslateReadError);
        return rows.Select(static r => new RelationBand(
            r.Band, r.Name, r.Rank, r.RelationTypes, r.ConsensusRows)).ToList();
    }

    public async Task<HighwayPopulationStatus> HighwayPopulationAsync(CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.HighwayPopulationAsync(
            _dataSource, ct, TranslateReadError);
        var row = rows.Single();
        return new HighwayPopulationStatus(
            row.RegistryReady, row.HistoricalPopulationComplete, row.CompletedAt,
            row.PendingPairs, row.PendingRefreshes);
    }

    /// <summary>Resolve a word or a 32-hex id to a content id, with its label.</summary>
    public async Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct)
    {
        // A FEN resolves as the id of its composed position, not as the id of its text.
        if (ChessPositionRef.TryComposeId(reference, out var posId))
            return (posId.ToBytes(), Convert.ToHexString(posId.ToBytes()).ToLowerInvariant());

        // Resolution and display label come back from one server call on one connection.
        var resolved = await NpgsqlSubstrateReads.ResolveRefWithLabelAsync(
            _dataSource, reference, ct, TranslateReadError).ConfigureAwait(false);
        return resolved is null ? null : (resolved.Id, resolved.Label);
    }

    /// <summary>
    /// A shape-dispatched read. band_facts, beam, path, neighbors, and generate call their
    /// native entry points with the caller's dials; every other shape goes to recall_intent.
    /// </summary>
    public async Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct)
    {
        switch (shape)
        {
            case "band_facts":
                return await BandFactsAsync(topic, bands, dials.Limit, ct);
            case "beam":
                return await BeamAsync(topic, relationType, bands, dials, ct);
            case "path":
                return await PathAsync(topic, topic2, dials, ct);
            case "neighbors":
                return await GeometricNeighborsAsync(topic, dials.Limit, ct);
            case "generate":
                return await GenerateAsync(topic, dials, ct);
            default:
                return await RecallIntentAsync(shape, topic, topic2, relationType, lang, contextIds, ct);
        }
    }

    private async Task<IReadOnlyList<QueryRow>> RecallIntentAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.RecallIntentAsync(
            _dataSource, shape, topic, topic2, relationType, lang, contextIds, ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    /// <summary>
    /// Every edge of a topic inside the selected bands, both directions, ranked by
    /// eff_mu. Bands narrow the read without naming a relation type or a language.
    /// </summary>
    private async Task<IReadOnlyList<QueryRow>> BandFactsAsync(
        byte[] topic, int[]? bands, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.BandFactsAsync(
            _dataSource, topic, bands, limit, ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    /// <summary>
    /// Beam search over consensus. The band selection is the intent mask walk_branches
    /// gates its scan on, so bands narrow the scan itself rather than filter its output.
    /// </summary>
    private async Task<IReadOnlyList<QueryRow>> BeamAsync(
        byte[] topic, string? relationType, int[]? bands, QueryDials dials, CancellationToken ct)
    {
        // Without a band or relation type, walk_branches would scan every relation-type
        // partition; the read takes the greedy strongest chain instead.
        var haveLens = !string.IsNullOrWhiteSpace(relationType) || (bands is { Length: > 0 });
        if (!haveLens)
        {
            var greedy = await NpgsqlSubstrateReads.WalkStrongestAsync(
                _dataSource, topic, dials.Depth, ct, TranslateReadError);
            return MapQueryRows(greedy);
        }

        var rows = await NpgsqlSubstrateReads.WalkBranchesBeamAsync(
            _dataSource, topic, relationType, bands, dials.Depth, dials.Breadth, dials.Limit,
            ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    /// <summary>Admissible geometric A* between two topics; Dijkstra by default.</summary>
    private async Task<IReadOnlyList<QueryRow>> PathAsync(
        byte[] topic, byte[]? topic2, QueryDials dials, CancellationToken ct)
    {
        if (topic2 is null)
            return [new QueryRow("path needs a second topic.", null, null)];

        var rows = await NpgsqlSubstrateReads.AstarPathAsync(
            _dataSource, topic, topic2, dials.Depth, dials.Directed, dials.UseGeometry,
            ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    /// <summary>Nearest content by position on S³ and by trajectory shape.</summary>
    private async Task<IReadOnlyList<QueryRow>> GeometricNeighborsAsync(
        byte[] topic, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.StructuralNeighborsAsync(
            _dataSource, topic, limit, ct, TranslateReadError);
        return rows.OrderBy(static n => n.Geodesic).Select(static n =>
        {
            var label = n.Label ?? "";
            var frechet = n.Frechet is null
                ? ""
                : Math.Round((decimal)n.Frechet.Value, 4).ToString();
            return new QueryRow(
                $"{label}  (geodesic {Math.Round((decimal)n.Geodesic, 4)}, frechet {frechet})",
                null, null);
        }).ToList();
    }

    /// <summary>Trajectory descent. Seeded, so a generation is reproducible.</summary>
    private async Task<IReadOnlyList<QueryRow>> GenerateAsync(
        byte[] topic, QueryDials dials, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.WalkContinuationsAsync(
            _dataSource, topic, dials.Steps, dials.MaxStride, dials.Spread, dials.Breadth,
            dials.Seed, ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    private static IReadOnlyList<QueryRow> MapQueryRows(
        IReadOnlyList<NpgsqlSubstrateReads.ConverseReplyRow> rows)
        => rows.Select(static r => new QueryRow(r.Reply, r.EffMu, r.Witnesses)).ToList();

    private static Exception TranslateReadError(Exception failure, string label) => failure switch
    {
        PostgresException pg => new SubstrateQueryException(
            $"{label} query failed [{pg.SqlState}] {pg.MessageText}"
            + (pg.Where is null ? "" : $" @ {pg.Where}"), pg),
        _ => new SubstrateUnavailableException("Substrate is unreachable.", failure),
    };
}

/// <summary>Normalized dials for one read. Caller work budgets pass through unchanged.</summary>
internal readonly record struct QueryDials(
    int Depth, int Breadth, int Limit, int Steps, double Spread, int MaxStride,
    long? Seed, bool Directed, bool UseGeometry)
{
    public static QueryDials From(QueryRequest req) => new(
        Depth: Math.Max(0, req.Depth ?? 4),
        Breadth: Math.Max(0, req.Breadth ?? 5),
        Limit: Math.Max(0, req.Limit ?? 40),
        Steps: Math.Max(0, req.Steps ?? 24),
        Spread: Math.Clamp(req.Spread ?? 0.7, 0.0, 1.0),
        MaxStride: Math.Max(0, req.MaxStride ?? 5),
        Seed: req.Seed,
        Directed: req.Directed ?? false,
        UseGeometry: req.UseGeometry ?? false);
}
