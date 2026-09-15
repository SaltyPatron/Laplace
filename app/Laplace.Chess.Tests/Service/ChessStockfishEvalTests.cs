using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessStockfishEvalTests
{
    private static readonly StockfishEvaluationRecipe Recipe = StockfishEvaluationRecipe.ForTests("scripted-corpus-evaluator/v1");
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private sealed class ScriptedEvaluator(params int?[] scores) : IPositionEvaluator
    {
        private int _i;
        public List<string> Fens { get; } = [];
        public int? EvaluateCp(string fen)
        {
            Fens.Add(fen);
            return _i < scores.Length ? scores[_i++] : 0;
        }
    }

    private sealed class CountingEvaluator(ConcurrentDictionary<string, int> calls) : IPositionEvaluator
    {
        public int? EvaluateCp(string fen)
        {
            calls.AddOrUpdate(fen, 1, static (_, count) => count + 1);
            Thread.Sleep(10);
            return 10;
        }
    }

    private static SubstrateChange Derive(IPositionEvaluator eval, string pgn = Game)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var witnessed = ChessAnalyze.WitnessedFromParsed(parsed);
        var b = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/sf-eval");
        ChessStockfishEval.DeriveGame(b, witnessed, eval, Recipe);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Theory]
    [InlineData(300, "blunder")]
    [InlineData(100, "mistake")]
    [InlineData(50, "inaccuracy")]
    [InlineData(49, null)]
    [InlineData(-20, null)]
    public void ClassifyLoss_Thresholds(int loss, string? expected)
        => Assert.Equal(expected, ChessStockfishEval.ClassifyLoss(loss));

    [Fact]
    public void DeriveGame_EvaluatesEveryNonTerminalPosition_Once()
    {
        var eval = new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 90, -120, 350 });
        Derive(eval);
        Assert.Equal(7, eval.Fens.Count);
        Assert.Equal(eval.Fens.Count, eval.Fens.Distinct().Count());
        Assert.StartsWith("rnbqkbnr/pppppppp", eval.Fens[0]);
    }

    [Fact]
    public void DeriveGame_AttestsEvals_UnderStockfishSource_WithExactRecipeContext()
    {
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 90, -120, 350 }));
        var evalRows = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.HasEvalType).ToList();
        Assert.Equal(7, evalRows.Count);
        var positions = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var context = ChessStockfishEval.MarkerId(parsed.LineId, Recipe);
        Assert.All(evalRows, a =>
        {
            Assert.Equal(ChessStockfishEval.SourceId, a.SourceId);
            Assert.Equal(context, a.ContextId);
            Assert.Contains(a.SubjectId, positions);
        });
    }

    [Fact]
    public void DeriveGame_ConvictsTheBlunder_ByEvalDelta()
    {
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 70, -120, 500 }));
        var quality = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.MoveQualityType).ToList();
        Assert.Single(quality);
        Assert.Equal(ChessStockfishEval.SourceId, quality[0].SourceId);
        Assert.Equal(ContentEmitter.RootId("blunder"), quality[0].ObjectId);
    }

    [Fact]
    public void DeriveGame_CleanGame_DepositsNoQualityRows()
    {
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -18, 22, -20, 25, -22, 30 }));
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.MoveQualityType);
    }

    [Fact]
    public void DeriveGame_StampsVersionedMarker()
    {
        var change = Derive(new ScriptedEvaluator());
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var marker = ChessStockfishEval.MarkerId(parsed.LineId, Recipe);
        Assert.Contains(change.Entities, e => e.Id == marker);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId && a.SubjectId == parsed.LineId
            && a.SourceId == ChessStockfishEval.SourceId && a.ContextId == marker
            && a.ObjectId == ContentEmitter.RootId(Recipe.CanonicalManifest));
    }

    [Fact]
    public void DifferentRecipeCannotReuseSharedMemoOrCalculatedContext()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var witnessed = ChessAnalyze.WitnessedFromParsed(parsed);
        var memo = new ConcurrentDictionary<Hash128, int?>();
        var original = new ScriptedEvaluator();
        var first = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/original-recipe");
        ChessStockfishEval.DeriveGame(first, witnessed, original, Recipe, memo);

        var changedRecipe = StockfishEvaluationRecipe.ForTests("different-engine-or-options/v1");
        var changed = new ScriptedEvaluator();
        var second = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/changed-recipe");
        ChessStockfishEval.DeriveGame(second, witnessed, changed, changedRecipe, memo);
        Assert.Equal(7, original.Fens.Count);
        Assert.Equal(7, changed.Fens.Count);
        Assert.Equal(14, memo.Count);
        var oldContext = ChessStockfishEval.MarkerId(parsed.LineId, Recipe);
        var newContext = ChessStockfishEval.MarkerId(parsed.LineId, changedRecipe);
        Assert.NotEqual(oldContext, newContext);
        Assert.All(second.Build().Attestations.Where(a => a.TypeId == ChessVocabulary.HasEvalType),
            a => Assert.Equal(newContext, a.ContextId));
    }

    [Fact]
    public void DeriveGame_NullEvals_ProduceNoRows()
    {
        var change = Derive(new ScriptedEvaluator(new int?[] { null, null, null, null, null, null, null }));
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasEvalType);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.MoveQualityType);
    }

    [Fact]
    public void DeriveGame_EvalMemo_SearchesSharedPositionsOnce()
    {
        const string g1 =
            "[Event \"A\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 1-0\n";
        const string g2 =
            "[Event \"B\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.02\"]\n[Result \"0-1\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bc4 Nf6 0-1\n";

        var memo = new ConcurrentDictionary<Hash128, int?>();
        var eval = new ScriptedEvaluator(Enumerable.Repeat((int?)10, 32).ToArray());

        var w1 = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(g1)!);
        var b1 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/memo");
        ChessStockfishEval.DeriveGame(b1, w1, eval, Recipe, memo);
        int afterFirst = eval.Fens.Count;
        Assert.Equal(7, afterFirst);

        var w2 = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(g2)!);
        var b2 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/memo");
        ChessStockfishEval.DeriveGame(b2, w2, eval, Recipe, memo);

        Assert.Equal(afterFirst + 1, eval.Fens.Count);
        Assert.Contains(b2.SetInputUnitsConsumed(1).Build().Attestations,
            a => a.TypeId == ChessVocabulary.HasEvalType);
    }

    [Fact]
    public void FailedEvaluations_AreAbsenceAndDoNotPoisonMemo()
    {
        var witnessed = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(Game)!);
        var memo = new ConcurrentDictionary<Hash128, int?>();
        var first = new ScriptedEvaluator(new int?[] { null, null, null, null, null, null, null });
        var b = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/null-memo");
        ChessStockfishEval.DeriveGame(b, witnessed, first, Recipe, memo);
        Assert.Empty(memo);

        var retry = new ScriptedEvaluator(Enumerable.Repeat((int?)17, 7).ToArray());
        var b2 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/null-retry");
        ChessStockfishEval.DeriveGame(b2, witnessed, retry, Recipe, memo);
        Assert.Equal(7, retry.Fens.Count);
        Assert.Equal(7, memo.Count);
    }

    [Fact]
    public async Task PrepareGame_ParallelWorkersSingleFlightSharedPositions()
    {
        var witnessed = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(Game)!);
        var memo = new ConcurrentDictionary<Hash128, int?>();
        var inflight = new ConcurrentDictionary<Hash128, Lazy<int?>>();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        var first = Task.Run(() => ChessStockfishEval.PrepareGame(
            witnessed, new CountingEvaluator(calls), Recipe, memo, inflight));
        var second = Task.Run(() => ChessStockfishEval.PrepareGame(
            witnessed, new CountingEvaluator(calls), Recipe, memo, inflight));

        var prepared = await Task.WhenAll(first, second);
        Assert.All(prepared, item => Assert.NotNull(item));
        Assert.Equal(7, calls.Count);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
        Assert.Equal(7, memo.Count);
    }

    [Fact]
    public async Task Decomposer_HandlerMarksEnginePreparationParallel()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lpsf-handler-{Guid.NewGuid():N}.bin");
        var decomposer = new ChessStockfishEvalDecomposer(
            evaluatorFactory: () => new ScriptedEvaluator(), evalCachePath: path, evaluatorRecipe: Recipe);
        try
        {
            Assert.True(decomposer.CreateEvalHandlerForTests().ParallelizeDeferredUnitCreation);
        }
        finally
        {
            await decomposer.DisposeAsync();
            File.Delete(StockfishEvalCache.RecipePath(path, Recipe));
            File.Delete(StockfishEvalCache.RecipePath(path, Recipe) + ".journal");
        }
    }

    [Fact]
    public async Task Decomposer_FullyCachedGameDoesNotAcquireAnEvaluator()
    {
        await using var fixture = new CachedDecomposerFixture(() =>
            throw new InvalidOperationException("A cached game must not acquire an evaluator."));
        using var unit = fixture.Decomposer.CreateEvalHandlerForTests().CreateDeferredUnit(fixture.Record);
        Assert.NotNull(unit);
        fixture.AssertCompleteMemo();
    }

    [Fact]
    public async Task Decomposer_MixedGameSearchesOnlyTheMissAndReusesItsCompletedMemo()
    {
        int created = 0;
        var engine = new TrackedEvaluator(_ => 23);
        await using (var fixture = new CachedDecomposerFixture(() =>
        {
            Interlocked.Increment(ref created);
            return engine;
        }, missingPosition: 6))
        {
            var handler = fixture.Decomposer.CreateEvalHandlerForTests();
            using var first = handler.CreateDeferredUnit(fixture.Record);
            Assert.NotNull(first);
            using var repeat = handler.CreateDeferredUnit(fixture.Record);
            Assert.NotNull(repeat);
            fixture.AssertCompleteMemo();
            Assert.Equal(1, created);
            Assert.Equal(new[] { fixture.MissingFen! }, engine.Fens.ToArray());
            Assert.Equal(0, engine.DisposeCalls);
        }
        Assert.Equal(1, engine.DisposeCalls);
    }

    [Fact]
    public async Task Decomposer_CachedGameCompletesWhileTheOnlyWorkerSearchesAnotherGame()
    {
        using var searching = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new TrackedEvaluator(_ =>
        {
            searching.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Search was not released.");
            return 23;
        });
        await using var fixture = new CachedDecomposerFixture(() => engine, missingPosition: 6);
        var handler = fixture.Decomposer.CreateEvalHandlerForTests();
        var pending = Task.Run(() => handler.CreateDeferredUnit(fixture.Record));
        Task<IIngestDeferredUnit>? cached = null;
        try
        {
            Assert.True(searching.Wait(TimeSpan.FromSeconds(5)), "The uncached position must reach the worker.");
            var shortGame = WitnessedEvalFixture(["e4", "e5"]);
            var shortRecord = new ChessStockfishEvalRecord(shortGame, fixture.Recipe);
            cached = Task.Run(() => handler.CreateDeferredUnit(shortRecord));
            using var cachedUnit = await cached.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(cachedUnit);
            Assert.False(pending.IsCompleted);
            Assert.Single(engine.Fens);
        }
        finally
        {
            release.Set();
            using var pendingUnit = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (cached is not null) (await cached.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
    }

    [Fact]
    public async Task Decomposer_ConcurrentRequestsForOneMissAcquireOnlyTheSearchingWorker()
    {
        using var searching = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int created = 0;
        var engines = new ConcurrentBag<TrackedEvaluator>();
        await using var fixture = new CachedDecomposerFixture(() =>
        {
            Interlocked.Increment(ref created);
            var engine = new TrackedEvaluator(_ =>
            {
                searching.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Search was not released.");
                return 23;
            });
            engines.Add(engine);
            return engine;
        }, missingPosition: 6, processes: 2);
        var handler = fixture.Decomposer.CreateEvalHandlerForTests();
        var first = Task.Run(() => handler.CreateDeferredUnit(fixture.Record));
        var second = new TaskCompletionSource<IIngestDeferredUnit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondThread = new Thread(() =>
        {
            try { second.SetResult(handler.CreateDeferredUnit(fixture.Record)); }
            catch (Exception error) { second.SetException(error); }
        }) { IsBackground = true };
        try
        {
            Assert.True(searching.Wait(TimeSpan.FromSeconds(5)));
            secondThread.Start();
            Assert.True(SpinWait.SpinUntil(() => second.Task.IsCompleted
                || (secondThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)),
                "The second request must reach the pending shared evaluation.");
            Assert.False(second.Task.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        using var firstUnit = await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondUnit = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(firstUnit);
        Assert.NotNull(secondUnit);
        fixture.AssertCompleteMemo();
        Assert.Equal(1, created);
        Assert.Equal(new[] { fixture.MissingFen! }, engines.SelectMany(e => e.Fens).ToArray());
    }

    [Fact]
    public async Task Decomposer_FailedSearchReturnsTheWorkerAndDoesNotCacheTheFailure()
    {
        int attempts = 0;
        int created = 0;
        var engine = new TrackedEvaluator(_ => Interlocked.Increment(ref attempts) == 1
            ? throw new InvalidDataException("controlled search failure") : 23);
        await using var fixture = new CachedDecomposerFixture(() =>
        {
            Interlocked.Increment(ref created);
            return engine;
        }, missingPosition: 6);
        var handler = fixture.Decomposer.CreateEvalHandlerForTests();
        var failure = Assert.Throws<InvalidDataException>(() => handler.CreateDeferredUnit(fixture.Record));
        Assert.Equal("controlled search failure", failure.Message);
        using var retry = await Task.Run(() => handler.CreateDeferredUnit(fixture.Record))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(retry);
        fixture.AssertCompleteMemo();
        Assert.Equal(1, created);
        Assert.Equal(new[] { fixture.MissingFen!, fixture.MissingFen! }, engine.Fens.ToArray());
    }

    // This handler consumes already witnessed moves; PGN parsing and deposition have
    // their own tests. Keep the acquisition controls at that actual input boundary.
    private static ChessWitnessedGame WitnessedEvalFixture(IReadOnlyList<string> moves)
        => new(Hash128.OfCanonical("test/evaluator/line/" + string.Join(' ', moves)),
            Hash128.OfCanonical("test/evaluator/playing/" + string.Join(' ', moves)),
            moves, GameOutcome.WonBy(0), null, null, null, null, null, null);

    private sealed class TrackedEvaluator(Func<string, int?> evaluate) : IPositionEvaluator, IDisposable
    {
        public ConcurrentQueue<string> Fens { get; } = new();
        public int DisposeCalls;
        public int? EvaluateCp(string fen)
        {
            Fens.Enqueue(fen);
            return evaluate(fen);
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class CachedDecomposerFixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"lpsf-cached-handler-{Guid.NewGuid():N}.bin");
        public StockfishEvaluationRecipe Recipe { get; }
        public ChessStockfishEvalDecomposer Decomposer { get; }
        public ChessStockfishEvalRecord Record { get; }
        public string? MissingFen { get; }

        public CachedDecomposerFixture(Func<IPositionEvaluator> factory, int? missingPosition = null, int processes = 1)
        {
            Recipe = StockfishEvaluationRecipe.ForTests("cached-handler/v1", new() { Processes = processes });
            var witnessed = WitnessedEvalFixture(["e4", "e5", "Qh5", "Nc6", "Bc4", "Nf6", "Qxf7#"]);
            Record = new(witnessed, Recipe);
            var memo = new ConcurrentDictionary<Hash128, int?>();
            var seed = new ScriptedEvaluator();
            var prepared = ChessStockfishEval.PrepareGame(witnessed, seed, Recipe, memo, null);
            Assert.True(prepared?.Complete);
            Assert.Equal(7, memo.Count);
            if (missingPosition.HasValue)
            {
                MissingFen = seed.Fens[missingPosition.Value];
                Assert.True(memo.TryRemove(Hash128.OfCanonical(Recipe.InputKey(MissingFen)), out _));
            }
            StockfishEvalCache.Save(_path, Recipe, memo);
            Decomposer = new(evaluatorFactory: factory, evalCachePath: _path, evaluatorRecipe: Recipe);
        }

        public void AssertCompleteMemo()
        {
            var memo = StockfishEvalCache.Load(_path, Recipe);
            Assert.Equal(7, memo.Count);
            if (MissingFen is not null)
                Assert.Equal(23, memo[Hash128.OfCanonical(Recipe.InputKey(MissingFen))]);
        }

        public async ValueTask DisposeAsync()
        {
            await Decomposer.DisposeAsync();
            File.Delete(StockfishEvalCache.RecipePath(_path, Recipe));
            File.Delete(StockfishEvalCache.RecipePath(_path, Recipe) + ".journal");
        }
    }

    [Fact]
    public void EvalCache_RoundTrips_AndRejectsBudgetMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpsf-test-{Guid.NewGuid():N}.bin");
        var p1 = Hash128.OfCanonical("p1");
        var p2 = Hash128.OfCanonical("p2");
        var failed = Hash128.OfCanonical("failed");
        try
        {
            var memo = new ConcurrentDictionary<Hash128, int?>();
            memo[p1] = 42;
            memo[p2] = -310;
            memo[failed] = null; // legacy/transient absence is compacted away
            StockfishEvalCache.Save(path, censusVersion: 1, depth: 10, nodes: 0, memo);

            var back = StockfishEvalCache.Load(path, 1, 10, 0);
            Assert.Equal(2, back.Count);
            Assert.Equal(42, back[p1]);
            Assert.Equal(-310, back[p2]);
            Assert.False(back.ContainsKey(failed));

            Assert.Empty(StockfishEvalCache.Load(path, 1, 12, 0));
            Assert.Empty(StockfishEvalCache.Load(path, 1, 10, 80_000));
            Assert.Empty(StockfishEvalCache.Load(path, 2, 10, 0));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".journal");
        }
    }

    [Fact]
    public void EvalCache_AppendJournalSurvivesCancellationAndCompacts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lpsf-journal-{Guid.NewGuid():N}.bin");
        string journal = path + ".journal";
        var p1 = Hash128.OfCanonical("journal/p1");
        var p2 = Hash128.OfCanonical("journal/p2");
        var failed = Hash128.OfCanonical("journal/failed");
        try
        {
            StockfishEvalCache.Append(path, 1, 10, 0,
            [
                new KeyValuePair<Hash128, int?>(p1, 88),
                new KeyValuePair<Hash128, int?>(p2, -7),
                new KeyValuePair<Hash128, int?>(failed, null),
            ]);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(journal));
            var recovered = StockfishEvalCache.Load(path, 1, 10, 0);
            Assert.Equal(2, recovered.Count);
            Assert.Equal(88, recovered[p1]);
            Assert.Equal(-7, recovered[p2]);
            Assert.False(recovered.ContainsKey(failed));
            Assert.Empty(StockfishEvalCache.Load(path, 1, 11, 0));

            using (var append = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read))
                append.Write([1, 2, 3, 4, 5]);
            var afterTornTail = StockfishEvalCache.Load(path, 1, 10, 0);
            Assert.Equal(2, afterTornTail.Count);

            StockfishEvalCache.Save(path, 1, 10, 0, afterTornTail);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(journal));
            Assert.Equal(2, StockfishEvalCache.Load(path, 1, 10, 0).Count);
        }
        finally
        {
            File.Delete(path);
            File.Delete(journal);
        }
    }

    [Fact]
    public void EvalCache_MissingOrCorrupt_YieldsEmpty_NeverThrows()
    {
        Assert.Empty(StockfishEvalCache.Load("/nonexistent/dir/nope.bin", 1, 10, 0));
        var path = Path.Combine(Path.GetTempPath(), $"lpsf-corrupt-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Empty(StockfishEvalCache.Load(path, 1, 10, 0));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".journal");
        }
    }

    [Fact]
    public void DeriveGame_EmitsOnlyDeclaredRelations()
    {
        var declared = ChessSeedManifest.Relations
            .Select(RelationTypeRegistry.RelationTypeId).ToHashSet();
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 90, -120, 500 }));
        var metaTypes = change.Entities
            .Where(e => e.TypeId == BootstrapIntentBuilder.RelationTypeMetaTypeId)
            .Select(e => e.Id).ToHashSet();
        var undeclared = change.Attestations
            .Select(a => a.TypeId).Distinct()
            .Where(t => !declared.Contains(t) && !metaTypes.Contains(t)).ToList();
        Assert.Empty(undeclared);
    }
}
