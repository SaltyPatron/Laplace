using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// CALCULATED pass: scan witnessed Chess_Game rows in Postgres (HAS_MOVETEXT under ChessPgn),
// hydrate via content roundtrip, derive geometry/consensus, stamp AnalysisMarker.
// Run: `laplace ingest chess-analyze`  (no path — substrate is the source of truth)
public sealed class ChessAnalyzeDecomposer : ComposeDecomposer<ChessAnalyzeRecord>
{
    private readonly int _engineDepth;
    /// <summary>engineDepth &gt; 0 runs the Laplace search per position for a calculated
    /// eval/quality signal; 0 (default) records only witnessed structure (fast ingest).</summary>
    public ChessAnalyzeDecomposer(int engineDepth = 0) => _engineDepth = engineDepth;

    public override Hash128 SourceId => ChessVocabulary.AnalysisSourceId;
    public override string SourceName => "ChessAnalysis";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/analysis";
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.AnalysisSourceId, SourceName, ChessVocabulary.AnalysisTrustClass, ct);

    protected override async IAsyncEnumerable<ChessAnalyzeRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessAnalysis requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedFromSubstrateAsync(
                           ds, ContainmentReader!, ws.Batch, ct))
            yield return new ChessAnalyzeRecord(witnessed);
    }

    protected override void Compose(ChessAnalyzeRecord record, SubstrateChangeBuilder b)
        => ChessAnalyze.DeriveFromWitnessed(b, record.Game, _engineDepth);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedGamesAsync(ds, ct);
    }
}

/// <summary>
/// Analysis pipeline record whose trunk root is the versioned analysis marker, not the game id.
/// </summary>
public sealed record ChessAnalyzeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessVocabulary.AnalysisMarkerId(Game.GameId, ChessAnalyze.Version);
}
