using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// CALCULATED stockfish pass (GH #573): scan witnessed Chess_Game rows lacking the
// ChessStockfishEval marker, hydrate via content roundtrip, evaluate every position with
// stockfish, attest HAS_EVAL + eval-delta MOVE_QUALITY under the ChessStockfish source.
// Run: `laplace ingest chess-eval [--analyze-depth N]`  (no path — substrate is the source)
public sealed class ChessStockfishEvalDecomposer : ComposeDecomposer<ChessStockfishEvalRecord>
{
    private readonly int _depth;
    private readonly StockfishEvaluatorPool _pool;

    /// <summary>depth = stockfish search depth per position (default 12: fast enough for a
    /// corpus pass, strong enough to convict blitz blunders). evaluatorFactory overrides the
    /// engine for tests.</summary>
    public ChessStockfishEvalDecomposer(int depth = 12, Func<IPositionEvaluator>? evaluatorFactory = null)
    {
        _depth = depth;
        _pool = new StockfishEvaluatorPool(evaluatorFactory ?? (() =>
        {
            var sf = ChessLabPaths.Catalog["stockfish"];
            if (!sf.Found)
                throw new InvalidOperationException(
                    "stockfish binary not found (env LAPLACE_STOCKFISH, build dir, or PATH) — "
                    + "the chess-eval pass needs it");
            return new StockfishProcessEvaluator(sf.Path!, _depth);
        }));
    }

    public override Hash128 SourceId => ChessStockfishEval.SourceId;
    public override string SourceName => ChessStockfishEval.SourceName;
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessStockfishEval.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/stockfish-eval";
    protected override int DefaultBatchSize => BatchConfigDefaults.Chess;

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessStockfishEval.SourceId, SourceName, ChessStockfishEval.TrustClassId, ct);

    protected override async IAsyncEnumerable<ChessStockfishEvalRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessStockfishEval requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options, DefaultBatchSize);
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedFromSubstrateAsync(
                           ds, ContainmentReader!, ws.Batch,
                           gid => ChessStockfishEval.MarkerId(gid, ChessStockfishEval.Version), ct))
            yield return new ChessStockfishEvalRecord(witnessed);
    }

    protected override void Compose(ChessStockfishEvalRecord record, SubstrateChangeBuilder b)
    {
        var evaluator = _pool.Rent();
        try { ChessStockfishEval.DeriveGame(b, record.Game, evaluator); }
        finally { _pool.Return(evaluator); }
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedGamesAsync(ds, ct);
    }
}

/// <summary>
/// Stockfish-eval pipeline record; trunk root is the versioned stockfish marker so re-runs
/// dedup against the marker, never against the game.
/// </summary>
public sealed record ChessStockfishEvalRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessStockfishEval.MarkerId(Game.GameId, ChessStockfishEval.Version);
}
