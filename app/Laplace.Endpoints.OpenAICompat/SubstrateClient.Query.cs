using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The structural read surface. Every dial the native functions accept is a
/// parameter here — the previous surface accepted the same arguments and then
/// pinned them to constants in C#, which is why the app could only ever ask for
/// one shape of read.
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

    /// <summary>Resolve a word or a 32-hex id to a content id, with its label.</summary>
    public async Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct)
    {
        // GH #575: FEN → composed position id (not word_id of the FEN string).
        if (ChessPositionRef.TryComposeId(reference, out var posId))
            return (posId.ToBytes(), Convert.ToHexString(posId.ToBytes()).ToLowerInvariant());

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await NpgsqlSubstrateReads.ResolveRefAsync(_dataSource, reference, ct);
        if (id is null) return null;
        var label = await NpgsqlSubstrateReads.LabelOrHexAsync(conn, id, ct) ?? "";
        return (id, label);
    }

    /// <summary>
    /// A shape-dispatched read. Shapes the responder family covers go through
    /// recall_intent; the walk, path and generation shapes go to their native
    /// entry points with the caller's dials applied.
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
    /// Every edge of a topic inside the selected bands, both directions, ranked
    /// by eff_mu. Selecting bands is how a read narrows without naming a single
    /// relation type and without naming a language.
    /// </summary>
    private async Task<IReadOnlyList<QueryRow>> BandFactsAsync(
        byte[] topic, int[]? bands, int limit, CancellationToken ct)
    {
        var rows = await NpgsqlSubstrateReads.BandFactsAsync(
            _dataSource, topic, bands, limit, ct, TranslateReadError);
        return MapQueryRows(rows);
    }

    /// <summary>
    /// Beam search over the consensus graph. The band selection becomes the
    /// highway intent mask — the same bit surface walk_branches already gates
    /// on, so narrowing the lens narrows the scan rather than filtering after it.
    /// </summary>
    private async Task<IReadOnlyList<QueryRow>> BeamAsync(
        byte[] topic, string? relationType, int[]? bands, QueryDials dials, CancellationToken ct)
    {
        // An unfiltered walk_branches call Append-scans every relation-type
        // partition (~24s, measured — see recall_walk_response). A band lens or
        // a named relation type keeps the scan bounded; with neither, take the
        // greedy single chain instead of the beam.
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

/// <summary>Clamped dials for one read. Bounds live here, not scattered as literals.</summary>
internal readonly record struct QueryDials(
    int Depth, int Breadth, int Limit, int Steps, double Spread, int MaxStride,
    long? Seed, bool Directed, bool UseGeometry)
{
    public static QueryDials From(QueryRequest req) => new(
        Depth: Math.Clamp(req.Depth ?? 4, 1, 16),
        Breadth: Math.Clamp(req.Breadth ?? 5, 1, 32),
        Limit: Math.Clamp(req.Limit ?? 40, 1, 500),
        Steps: Math.Clamp(req.Steps ?? 24, 1, 256),
        Spread: Math.Clamp(req.Spread ?? 0.7, 0.0, 1.0),
        MaxStride: Math.Clamp(req.MaxStride ?? 5, 1, 8),
        Seed: req.Seed,
        Directed: req.Directed ?? false,
        UseGeometry: req.UseGeometry ?? false);
}
