using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// Runs the ChessAnalyze calculation over playings already recorded in the substrate: streams
// playings (PLAYS_LINE under a witness source) that lack the current-version analysis marker,
// hydrates their witnessed inputs, and derives through the shared compose path. The recording
// pass (ChessPgnDecomposer) derives inline, so this covers playings recorded without analysis
// and re-derivation at a new ChessAnalyze.Version. Input is the substrate itself; no path.
public sealed class ChessAnalyzeDecomposer
    : ComposeDecomposer<ChessAnalyzeRecord>, IIngestNoOpExplainer
{
    // Count of unanalyzed playings streamed. The declared denominator is every recorded
    // playing, so a run that streams none is complete, not a silent no-op (IIngestNoOpExplainer).
    private long _candidatesStreamed;

    private readonly int _engineDepth;
    /// <summary><paramref name="engineDepth"/> is forwarded to
    /// <c>ChessAnalyze.DeriveFromWitnessed</c>.</summary>
    public ChessAnalyzeDecomposer(int engineDepth = 0) => _engineDepth = engineDepth;

    public override Hash128 SourceId => ChessVocabulary.AnalysisSourceId;
    public override string SourceName => "ChessAnalysis";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/analysis";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => IngestSourceProfile.ChessAnalyze;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

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

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedEventsAsync(
                           ds, ContainmentReader!, ws.Batch, ct))
        {
            _candidatesStreamed++;
            yield return new ChessAnalyzeRecord(witnessed);
        }
    }

    /// <summary>Nothing streamed means every playing already carries ANALYZED_AT.</summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessAnalysis: every one of {declaredInputUnits} recorded playing(s) already "
               + $"carries the v{ChessAnalyze.Version} analysis completion receipt — nothing to backfill "
               + "(the fused ingest pass derives inline, GH #600).")
            : null;

    protected override void Compose(ChessAnalyzeRecord record, SubstrateChangeBuilder b)
        => ChessAnalyze.DeriveFromWitnessed(b, record.Game, _engineDepth);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedEventsAsync(ds, ct);
    }
}

/// <summary>
/// One playing to analyze. Its trunk root and completion key are the playing's versioned
/// analysis marker, not the playing itself.
/// </summary>
public sealed record ChessAnalyzeRecord(ChessWitnessedGame Game) : ITrunkRootRecord, IIngestCompletionRecord
{
    public IngestUnitCompletionKey? Completion =>
        IngestUnitCompletion.Key(TrunkRootId, ChessAnalyze.SourceId, 21);
    public Hash128 TrunkRootId => ChessVocabulary.AnalysisMarkerId(Game.PlayingId, ChessAnalyze.Version);
}
