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

    public StockfishEvaluationRecipe Recipe { get; }
    private readonly StockfishEvaluatorPool _pool;
    private readonly IPositionEvaluator _evaluator;
    private readonly ConcurrentDictionary<Hash128, int?> _evalMemo;
    private readonly ConcurrentDictionary<Hash128, Lazy<int?>> _evalInflight = new();
    private readonly string _cachePath;
    private Hash128? _recipeMetadataRoot;
    private CancellationTokenRegistration _cancellation;

    public ChessStockfishEvalDecomposer(
        int depth = 10, long nodes = 0, Func<IPositionEvaluator>? evaluatorFactory = null,
        string? evalCachePath = null, StockfishEvaluationRecipe? evaluatorRecipe = null)
    {
        _cachePath = evalCachePath ?? StockfishEvalCache.DefaultPath();
        if (evaluatorFactory is not null)
        {
            Recipe = evaluatorRecipe ?? throw new ArgumentException(
                "An injected evaluator requires an explicit evaluation recipe.", nameof(evaluatorRecipe));
            _pool = new StockfishEvaluatorPool(evaluatorFactory, Recipe.Resources.Processes);
        }
        else
        {
            var sf = ChessLabPaths.Stockfish;
            if (!sf.Found)
                throw new InvalidOperationException(
                    "stockfish binary not found (LAPLACE_STOCKFISH, Stockfish source build, install dir, or PATH) — "
                    + "the chess-eval pass needs it");
            var settings = StockfishEvaluationOptions.FromEnvironment(depth, nodes);
            var initial = new StockfishProcessEvaluator(sf.Path!, settings);
            Recipe = initial.Recipe;
            _pool = new StockfishEvaluatorPool(
                () => new StockfishProcessEvaluator(sf.Path!, settings, Recipe), Recipe.Resources.Processes, initial);
        }
        _evaluator = new PooledEvaluator(_pool);
        _evalMemo = StockfishEvalCache.Load(_cachePath, Recipe);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public override Hash128 SourceId => ChessStockfishEval.SourceId;
    public override string SourceName => ChessStockfishEval.SourceName;
    public override int LayerOrder => 22;
    public override Hash128 TrustClassId => ChessStockfishEval.TrustClassId;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override string BatchLabelPrefix => "chess/stockfish-eval";

    public override int EstimatedBytesPerRecord => IngestSourceProfile.ChessAnalyze.EstBytesPerRecord;
    public override int EstimatedComposeUnitsPerRecord => IngestSourceProfile.ChessAnalyze.EstComposeUnitsPerRecord;
    public override IngestSourceProfile SizingProfile => IngestSourceProfile.ChessAnalyze;

    private IReadOnlyCollection<string> _canonicalNames = Array.Empty<string>();
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        _cancellation.Dispose();
        _cancellation = ct.Register(static state => ((StockfishEvaluatorPool)state!).Dispose(), _pool);
        _canonicalNames = await ChessVocabulary.BootstrapAsync(
            context.Writer, ChessStockfishEval.SourceId, SourceName, ChessStockfishEval.TrustClassId, ct);
        // The full artifact manifest is one source-level input, not work repeated for every
        // line/position. Persist it once through the shared writer before emitting references.
        var metadata = new SubstrateChangeBuilder(SourceId, $"{BatchLabelPrefix}/recipe/{Recipe.Id}");
        _recipeMetadataRoot = ContentEmitter.Emit(metadata, Recipe.CanonicalManifest, SourceId)
            ?? throw new InvalidDataException("Stockfish evaluation recipe could not be admitted as content.");
        await context.Writer.ApplyAsync(await metadata.BuildAsync(ct), ct);
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        var profile = IngestSourceProfile.ChessAnalyze;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, options);
        return new IngestBatchConfig
        {
            SourceId = SourceId,
            BatchLabelPrefix = BatchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            ContainmentReader = context.Reader,
            MaxInputUnits = options.MaxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
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

        int wave = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.ChessAnalyze, options);
        _candidatesStreamed = 0;
        await foreach (var witnessed in ChessWitnessHydrator.StreamUnanalyzedLinesAsync(
                           ds, ContainmentReader!, wave,
                           lineId => ChessStockfishEval.MarkerId(lineId, Recipe), ct))
        {
            _candidatesStreamed++;
            yield return new ChessStockfishEvalRecord(witnessed, Recipe);
        }
    }

    public (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits)
        => _candidatesStreamed == 0
            ? ("already-complete",
               $"ChessStockfishEval: every one of {declaredInputUnits} recorded line(s) already "
               + $"carries the v{ChessStockfishEval.Version} recipe {Recipe.Id} eval marker — nothing left to evaluate.")
            : null;

    protected override IIngestRecordHandler<ChessStockfishEvalRecord> CreateHandler()
        => CreateEvalHandlerForTests();

    internal IIngestRecordHandler<ChessStockfishEvalRecord> CreateEvalHandlerForTests()
        => new EvalHandler(this);

    protected override void Compose(ChessStockfishEvalRecord record, SubstrateChangeBuilder b)
    {
        var prepared = Prepare(record.Game);
        if (prepared is not null)
            ChessStockfishEval.DepositPrepared(b, prepared, _recipeMetadataRoot);
    }

    private ChessStockfishEval.PreparedLine? Prepare(ChessWitnessedGame game)
    {
        var prepared = ChessStockfishEval.PrepareGame(game, _evaluator, Recipe, _evalMemo, _evalInflight);
        if (prepared is not null)
            StockfishEvalCache.Append(_cachePath, Recipe, prepared.FreshEvaluations);
        return prepared;
    }

    private sealed class PooledEvaluator(StockfishEvaluatorPool pool) : IPositionEvaluator
    {
        public int? EvaluateCp(string fen)
        {
            // PrepareGame calls this only for a real memo miss whose single-flight
            // callback wins. Cached games and waiters never acquire a worker.
            // Return after each search: holding a lease across positions could wait
            // on another unit's callback while that callback waits for this worker.
            var evaluator = pool.Rent();
            try { return evaluator.EvaluateCp(fen); }
            finally { pool.Return(evaluator); }
        }
    }

    private void SaveCache()
        => StockfishEvalCache.Save(
            _cachePath, Recipe, _evalMemo);

    private void OnProcessExit(object? sender, EventArgs args) => SaveCache();

    public override ValueTask DisposeAsync()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _cancellation.Dispose();
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
            var prepared = owner.Prepare(record.Game);
            return new Unit(record, prepared, owner._recipeMetadataRoot);
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
            ChessStockfishEval.PreparedLine? prepared,
            Hash128? recipeMetadataRoot) : IIngestDeferredUnit
        {
            public TierTree? TreeForBatchProbe => null;

            public Task<byte[]?> ProbeDescentAsync(
                ISubstrateReader reader, CancellationToken ct = default)
                => Task.FromResult<byte[]?>(null);

            public Hash128 DrainInto(
                SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
            {
                if (prepared is null || !prepared.Complete) return default;
                ChessStockfishEval.DepositPrepared(builder, prepared, recipeMetadataRoot);
                return record.TrunkRootId;
            }

            public void Dispose()
            {
            }
        }
    }
}

public sealed record ChessStockfishEvalRecord(ChessWitnessedGame Game, StockfishEvaluationRecipe Recipe) : ITrunkRootRecord
{
    public Hash128 TrunkRootId => ChessStockfishEval.MarkerId(Game.LineId, Recipe);
}
