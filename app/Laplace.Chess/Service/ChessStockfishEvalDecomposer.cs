using System.Collections.Concurrent;
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
    private long _candidatesStreamed;

    private readonly int _depth;
    private readonly long _nodes;
    private readonly StockfishEvaluatorPool _pool;
    private readonly ConcurrentDictionary<Hash128, int?> _evalMemo;
    private readonly ConcurrentDictionary<Hash128, Lazy<int?>> _evalInflight = new();
    private readonly string _cachePath;

    /// <summary>depth = stockfish search depth per position (default 10 — the budget the v1
    /// census testimony was recorded at). A budget change rides a version bump lawfully via
    /// the #508 eviction verb: bump <see cref="ChessStockfishEval.Version"/>, then
    /// `laplace evict ChessStockfish --rederive`. nodes &gt; 0 switches to a node-capped
    /// search. evaluatorFactory overrides the process evaluator for tests.</summary>
    public ChessStockfishEvalDecomposer(
        int depth = 10, long nodes = 0, Func<IPositionEvaluator>? evaluatorFactory = null,
        string? evalCachePath = null)
    {
        _depth = depth;
        _nodes = nodes;
        _cachePath = evalCachePath ?? StockfishEvalCache.DefaultPath();
        _evalMemo = StockfishEvalCache.Load(_cachePath, ChessStockfishEval.Version, _depth, _nodes);
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

    /// <summary>
    /// Stockfish is CPU-bound external work, not a cheap builder callback. One uncapped wave is
    /// exactly the machine compose width, which makes MonolithSegmenter dispatch one line to
    /// each segment instead of buffering the entire 9k-line census into segment zero. An explicit
    /// operator --batch remains the one override.
    /// </summary>
    private static int ResolveEngineWave(DecomposerOptions options) =>
        options.BatchSize > 1
            ? options.BatchSize
            : Math.Max(1, IngestTopology.Current.ComposeWorkers);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        int wave = ResolveEngineWave(options);
        // Use the complete profile, including its measured resident-byte field. The generic
        // ComposeDecomposer profile projection carries only the first two scalar estimates.
        var profile = IngestSourceProfile.ChessAnalyze;
        var sized = IngestSizing.ResolveForSource(profile, wave);
        return new IngestBatchConfig
        {
            SourceId = SourceId,
            BatchLabelPrefix = BatchLabelPrefix,
            BatchSize = wave,
            ProbeChunkSize = sized.ProbeChunkSize,
            ContainmentReader = context.Reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = wave,
            WorkingSetRecordCap = wave,
            WorkingSetProfile = profile,
        };
    }

    protected override async IAsyncEnumerable<ChessStockfishEvalRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (ContainmentReader is null
            || ChessWitnessHydrator.TryResolveDataSource(ContainmentReader) is not { } ds)
            throw new InvalidOperationException(
                "ChessStockfishEval requires a live Postgres substrate (NpgsqlSubstrateReader). "
                + "Record games first: laplace ingest chess <pgn>");

        int wave = ResolveEngineWave(options);
        // Hydrate one engine wave, not a RAM-sized generic chess batch. This gets the first
        // useful line onto an engine immediately and keeps the dispatcher work-conserving.
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, wave,
                           lineId => ChessStockfishEval.MarkerId(lineId, ChessStockfishEval.Version), ct))
        {
            _candidatesStreamed++;
            yield return new ChessStockfishEvalRecord(witnessed);
        }
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessStockfishEval: every one of {declaredInputUnits} recorded line(s) already "
               + $"carries the v{ChessStockfishEval.Version} eval marker — nothing left to evaluate.")
            : null;

    /// <summary>
    /// Opt out of ComposeDecomposer's DirectComposeHandler. That handler explicitly declares
    /// ParallelizeDeferredUnitCreation=false and calls Compose during the later serial builder
    /// drain — exactly the wrong boundary for minutes of independent Stockfish work.
    /// </summary>
    protected override IIngestRecordHandler<ChessStockfishEvalRecord> CreateHandler()
        => CreateEvalHandlerForTests();

    internal IIngestRecordHandler<ChessStockfishEvalRecord> CreateEvalHandlerForTests()
        => new EvalHandler(this);

    // Compatibility path for direct ComposeDecomposer callers. Production uses EvalHandler.
    protected override void Compose(ChessStockfishEvalRecord record, SubstrateChangeBuilder b)
    {
        var evaluator = _pool.Rent();
        try
        {
            var prepared = ChessStockfishEval.PrepareGame(
                record.Game, evaluator, _evalMemo, _evalInflight);
            if (prepared is null) return;
            Checkpoint(prepared);
            ChessStockfishEval.DepositPrepared(b, prepared);
        }
        finally
        {
            _pool.Return(evaluator);
        }
    }

    private void Checkpoint(ChessStockfishEval.PreparedLine prepared)
    {
        // Append only the values this line caused the shared memo to admit. The journal is
        // fixed-record and cancellation-safe; normal completion compacts it into the snapshot.
        StockfishEvalCache.Append(
            _cachePath, ChessStockfishEval.Version, _depth, _nodes,
            prepared.FreshEvaluations);
    }

    private void SaveCache()
        => StockfishEvalCache.Save(
            _cachePath, ChessStockfishEval.Version, _depth, _nodes, _evalMemo);

    public override ValueTask DisposeAsync()
    {
        SaveCache();
        _pool.Dispose();
        return ValueTask.CompletedTask;
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        if (ChessWitnessHydrator.TryResolveDataSource(context.Reader) is not { } ds)
            return Task.FromResult<long?>(null);
        return ChessWitnessHydrator.CountRecordedLinesAsync(ds, ct);
    }

    private sealed class EvalHandler(ChessStockfishEvalDecomposer owner)
        : IIngestRecordHandler<ChessStockfishEvalRecord>
    {
        public bool ParallelizeDeferredUnitCreation => true;

        public IIngestDeferredUnit CreateDeferredUnit(ChessStockfishEvalRecord record)
        {
            var evaluator = owner._pool.Rent();
            try
            {
                var prepared = ChessStockfishEval.PrepareGame(
                    record.Game, evaluator, owner._evalMemo, owner._evalInflight);
                if (prepared is not null) owner.Checkpoint(prepared);
                return new Unit(record, prepared);
            }
            finally
            {
                owner._pool.Return(evaluator);
            }
        }

        public void WalkWitness(
            ChessStockfishEvalRecord record,
            Hash128 root,
            SubstrateChangeBuilder builder,
            IIngestDeferredUnit unit)
        {
        }

        public long UnitsPerRecord(ChessStockfishEvalRecord record) => 1;

        private sealed class Unit(
            ChessStockfishEvalRecord record,
            ChessStockfishEval.PreparedLine? prepared) : IIngestDeferredUnit
        {
            public TierTree? TreeForBatchProbe => null;

            public Task<byte[]?> ProbeDescentAsync(
                ISubstrateReader reader, CancellationToken ct = default)
                => Task.FromResult<byte[]?>(null);

            public Hash128 DrainInto(
                SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
            {
                if (prepared is null) return default;
                ChessStockfishEval.DepositPrepared(builder, prepared);
                return record.TrunkRootId;
            }

            public void Dispose()
            {
            }
        }
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
