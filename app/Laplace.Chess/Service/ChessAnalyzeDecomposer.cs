using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// CALCULATED pass, BACKFILL role: scan witnessed playings in Postgres (Chess_Event rows
// carrying a PLAYS_LINE edge under a witness source, GH #736) that carry no current-version
// per-event ANALYSIS marker, hydrate via content roundtrip,
// derive geometry/consensus, stamp AnalysisMarker. Since GH #600, `laplace ingest chess` derives
// inline in the recording pass (ChessPgnDecomposer.Compose -> DeriveFromParsed), so a fresh
// ingest never needs this pass; it exists to (a) analyze games recorded before the fusion landed
// and (b) re-derive at a bumped ChessAnalyze.Version without re-recording.
// Run: `laplace ingest chess-analyze`  (no path — substrate is the source of truth)
public sealed class ChessAnalyzeDecomposer
    : ComposeDecomposer<ChessAnalyzeRecord>, IIngestNoOpExplainer
{
    // Marker-gated backfill over playings the fused ingest pass (GH #600) already derived.
    // On a substrate seeded through that fused path there is nothing left to backfill, and
    // the declared denominator is still every recorded playing — so a correct, complete
    // run applied zero and the silent-no-op guard failed it. See IIngestNoOpExplainer.
    private long _candidatesStreamed;

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

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

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
               + $"carries the v{ChessAnalyze.Version} ANALYZED_AT marker — nothing to backfill "
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
/// Analysis pipeline record whose trunk root is the versioned per-EVENT analysis marker,
/// not the playing itself (GH #736: the analyzer's unit is the playing).
/// </summary>
public sealed record ChessAnalyzeRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessVocabulary.AnalysisMarkerId(Game.PlayingId, ChessAnalyze.Version);
}
