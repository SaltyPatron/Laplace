using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Chess.Service;

// CALCULATED stockfish pass (GH #573): scan witnessed LINES (GH #736 — distinct
// PLAYS_LINE objects) lacking the ChessStockfishEval marker, hydrate via content
// roundtrip, evaluate every position with stockfish, attest HAS_EVAL + eval-delta
// MOVE_QUALITY under the ChessStockfish source.
// Run: `laplace ingest chess-eval [--depth N | --nodes N]`  (no path — substrate is the source)
public sealed class ChessStockfishEvalDecomposer
    : ComposeDecomposer<ChessStockfishEvalRecord>, IIngestNoOpExplainer
{
    // Marker-gated backfill: the denominator is every RECORDED line, the stream is only
    // the lines still missing this pass's marker. A caught-up pass applies zero, which the
    // runner's silent-no-op guard turned into a hard failure. See IIngestNoOpExplainer.
    private long _candidatesStreamed;

    private readonly int _depth;
    private readonly long _nodes;
    private readonly StockfishEvaluatorPool _pool;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, int?> _evalMemo;
    private readonly string _cachePath;
    private int _memoAtLastSave;

    /// <summary>depth = stockfish search depth per position (default 10 — the budget the v1
    /// census testimony was recorded at). A budget change rides a version bump lawfully via
    /// the #508 eviction verb: bump <see cref="ChessStockfishEval.Version"/>, then
    /// `laplace evict ChessStockfish --rederive` (the evict_source procedure retracts the
    /// old testimony and refolds every touched cell before the lane re-derives — no double
    /// count). nodes &gt; 0 switches to a node-capped search instead
    /// (bounded worst case; opt-in via --nodes). evaluatorFactory overrides for tests.</summary>
    public ChessStockfishEvalDecomposer(
        int depth = 10, long nodes = 0, Func<IPositionEvaluator>? evaluatorFactory = null,
        string? evalCachePath = null)
    {
        _depth = depth;
        _nodes = nodes;
        // Persistent memo (spec-33 derived blob): survives db-reset so the reseed
        // re-census pays engine time only for positions never searched before.
        _cachePath = evalCachePath ?? StockfishEvalCache.DefaultPath();
        _evalMemo = StockfishEvalCache.Load(_cachePath, ChessStockfishEval.Version, _depth, _nodes);
        _memoAtLastSave = _evalMemo.Count;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveCache();
        _pool = new StockfishEvaluatorPool(evaluatorFactory ?? (() =>
        {
            var sf = ChessLabPaths.Catalog["stockfish"];
            if (!sf.Found)
                throw new InvalidOperationException(
                    "stockfish binary not found (env LAPLACE_STOCKFISH, build dir, or PATH) — "
                    + "the chess-eval pass needs it");
            return new StockfishProcessEvaluator(sf.Path!, _depth, _nodes);
        }));
    }

    public override Hash128 SourceId => ChessStockfishEval.SourceId;
    public override string SourceName => ChessStockfishEval.SourceName;
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessStockfishEval.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/stockfish-eval";

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

        var ws = IngestPipelineDefaults.ResolveWorkingSet(PipelineProfile, options);
        // LINE-grain stream (GH #736): a line shared by many playings is evaluated ONCE.
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, ws.Batch,
                           lineId => ChessStockfishEval.MarkerId(lineId, ChessStockfishEval.Version), ct))
        {
            _candidatesStreamed++;
            yield return new ChessStockfishEvalRecord(witnessed);
        }
    }

    /// <summary>Nothing streamed means every line already carries this version's eval.</summary>
    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessStockfishEval: every one of {declaredInputUnits} recorded line(s) already "
               + $"carries the v{ChessStockfishEval.Version} eval marker — nothing left to evaluate.")
            : null;

    protected override void Compose(ChessStockfishEvalRecord record, SubstrateChangeBuilder b)
    {
        var evaluator = _pool.Rent();
        try { ChessStockfishEval.DeriveGame(b, record.Game, evaluator, _evalMemo); }
        finally { _pool.Return(evaluator); }

        // Checkpoint the cache every ~50k fresh searches: a killed run keeps its paid-for
        // engine time. ProcessExit covers the normal end.
        int grown = _evalMemo.Count - System.Threading.Volatile.Read(ref _memoAtLastSave);
        if (grown >= 50_000
            && System.Threading.Interlocked.Exchange(ref _memoAtLastSave, _evalMemo.Count) != _evalMemo.Count)
            SaveCache();
    }

    private void SaveCache()
        => StockfishEvalCache.Save(_cachePath, ChessStockfishEval.Version, _depth, _nodes, _evalMemo);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
    }
}

/// <summary>
/// Stockfish-eval pipeline record; trunk root is the versioned per-LINE stockfish marker
/// so re-runs dedup against the marker, never against the line.
/// </summary>
public sealed record ChessStockfishEvalRecord(ChessWitnessedGame Game) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessStockfishEval.MarkerId(Game.LineId, ChessStockfishEval.Version);
}
