using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessStockfishEvalTests
{
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
        ChessStockfishEval.DeriveGame(b, witnessed, eval);
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
    public void DeriveGame_AttestsEvals_UnderStockfishSource_WithGameContext()
    {
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 90, -120, 350 }));
        var evalRows = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.HasEvalType).ToList();
        Assert.Equal(7, evalRows.Count);
        var positions = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();
        Assert.All(evalRows, a =>
        {
            Assert.Equal(ChessStockfishEval.SourceId, a.SourceId);
            Assert.NotNull(a.ContextId);
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
        var marker = ChessStockfishEval.MarkerId(parsed.LineId, ChessStockfishEval.Version);
        Assert.Contains(change.Entities, e => e.Id == marker);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId && a.SubjectId == parsed.LineId
            && a.SourceId == ChessStockfishEval.SourceId);
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
        ChessStockfishEval.DeriveGame(b1, w1, eval, memo);
        int afterFirst = eval.Fens.Count;
        Assert.Equal(7, afterFirst);

        var w2 = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(g2)!);
        var b2 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/memo");
        ChessStockfishEval.DeriveGame(b2, w2, eval, memo);

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
        ChessStockfishEval.DeriveGame(b, witnessed, first, memo);
        Assert.Empty(memo);

        var retry = new ScriptedEvaluator(Enumerable.Repeat((int?)17, 7).ToArray());
        var b2 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/null-retry");
        ChessStockfishEval.DeriveGame(b2, witnessed, retry, memo);
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
            witnessed, new CountingEvaluator(calls), memo, inflight));
        var second = Task.Run(() => ChessStockfishEval.PrepareGame(
            witnessed, new CountingEvaluator(calls), memo, inflight));

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
            evaluatorFactory: () => new ScriptedEvaluator(), evalCachePath: path);
        try
        {
            Assert.True(decomposer.CreateEvalHandlerForTests().ParallelizeDeferredUnitCreation);
        }
        finally
        {
            await decomposer.DisposeAsync();
            File.Delete(path);
            File.Delete(path + ".journal");
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
