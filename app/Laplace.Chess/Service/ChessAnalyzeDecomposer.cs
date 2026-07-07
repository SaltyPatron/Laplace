using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

// CALCULATED pass: scan witnessed Chess_Game rows in Postgres (HAS_MOVETEXT under ChessPgn),
// hydrate via content roundtrip, derive geometry/consensus, stamp AnalysisMarker.
// Run: `laplace ingest chess-analyze`  (no path — substrate is the source of truth)
public sealed class ChessAnalyzeDecomposer : DecomposerOrchestrator
{
    public override Hash128 SourceId => ChessVocabulary.AnalysisSourceId;
    public override string SourceName => "ChessAnalysis";
    public override int LayerOrder => 21;
    public override Hash128 TrustClassId => ChessVocabulary.AnalysisTrustClass;

    public int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public IngestSourceProfile SizingProfile => IngestSourceProfile.ChessAnalyze;

    private IReadOnlyCollection<string> _canonicalNames = System.Array.Empty<string>();
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessVocabulary.AnalysisSourceId, SourceName, ChessVocabulary.AnalysisTrustClass, ct);

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            throw new InvalidOperationException(
                "ChessAnalysis requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        var profile = IngestSourceProfile.ChessAnalyze;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, options, BatchConfigDefaults.Chess);

        if (WorkingSetMode.Enabled && options.MaxInputUnits <= 0 && !options.DryRun)
        {
            int workers = IngestTopology.Current.ComposeWorkers;
            var config = new IngestBatchConfig
            {
                SourceId = ChessVocabulary.AnalysisSourceId,
                BatchLabelPrefix = "chess/analysis",
                BatchSize = ws.Batch,
                ProbeChunkSize = ws.ProbeChunk,
                ContainmentReader = context.Reader,
                WorkingSet = WorkingSetMode.Enabled,
                WorkingSetProbeInterval = ws.ProbeInterval,
                WorkingSetRecordCap = ws.RecordCap,
                WorkingSetProfile = profile,
            };
            var stream = new ParallelChessWitnessRecordStream(ds, context.Reader, ws.ProbeInterval, workers, ct);
            await foreach (var change in IngestBatchPipeline.RunAsync(
                               stream, new ChessAnalyzeIngestHandler(), config, ct))
                yield return change;
            yield break;
        }

        int batch = System.Math.Clamp(options.BatchSize > 1 ? options.BatchSize : 256, 1, 512);
        await foreach (var change in RunComposePhaseAsync(
            ChessWitnessHydrator.StreamUnanalyzedFromSubstrateAsync(ds, context.Reader, batch, ct),
            (witnessed, b) => ChessAnalyze.DeriveFromWitnessed(b, witnessed),
            "analysis", SourceTrust.StructuredCorpus, batch, context, options, ct))
            yield return change;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return CountRecordedGamesAsync(ds, ct);
    }

    private static async Task<long?> CountRecordedGamesAsync(Npgsql.NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            SELECT count(DISTINCT e.id)
            FROM laplace.entities e
            JOIN laplace.attestations mt
              ON mt.subject_id = e.id
             AND mt.type_id = $2
             AND mt.source_id = $3
            WHERE e.type_id = $1");
        cmd.Parameters.AddWithValue(ChessVocabulary.GameType.ToBytes());
        cmd.Parameters.AddWithValue(RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT").ToBytes());
        cmd.Parameters.AddWithValue(ChessVocabulary.PgnSourceId.ToBytes());
        var total = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return total is long n ? n : 0L;
    }
}
