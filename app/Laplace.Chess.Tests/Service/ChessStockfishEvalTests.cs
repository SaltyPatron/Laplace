using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessStockfishEvalTests
{
    // Scholar's mate: 7 plies, ends in checkmate (terminal final position → no eval there).
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    /// <summary>Returns scripted side-to-move cps in call order; records every FEN asked.</summary>
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
        // 8 positions in a 7-ply game; the last is checkmate (terminal) and never asked.
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
        Assert.NotEmpty(evalRows);
        Assert.All(evalRows, a =>
        {
            Assert.Equal(ChessStockfishEval.SourceId, a.SourceId);
            Assert.NotNull(a.ContextId);
        });
    }

    [Fact]
    public void DeriveGame_ConvictsTheBlunder_ByEvalDelta()
    {
        // Only ply 5 (Nf6??) loses ≥50cp: before = -120 (Black to move), after = +500
        // (White to move) → loss = 380 → blunder. Every other ply's |before + after| < 50.
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
        var marker = ChessStockfishEval.MarkerId(parsed.GameId, ChessStockfishEval.Version);
        Assert.Contains(change.Entities, e => e.Id == marker);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalyzedAtType && a.SubjectId == parsed.GameId
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
        // Two games sharing the first four plies (Italian vs Two Knights). With a shared
        // memo, the second game must only ask the engine about positions the first game
        // never reached — the shared opening rides the cache.
        const string g1 =
            "[Event \"A\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 1-0\n";
        const string g2 =
            "[Event \"B\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.02\"]\n[Result \"0-1\"]\n\n"
            + "1. e4 e5 2. Nf3 Nc6 3. Bc4 Nf6 0-1\n";

        var memo = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, int?>();
        var eval = new ScriptedEvaluator(Enumerable.Repeat((int?)10, 32).ToArray());

        var w1 = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(g1)!);
        var b1 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/memo");
        ChessStockfishEval.DeriveGame(b1, w1, eval, memo);
        int afterFirst = eval.Fens.Count;
        Assert.Equal(7, afterFirst); // 7 positions in a 6-ply game (none terminal)

        var w2 = ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(g2)!);
        var b2 = new SubstrateChangeBuilder(ChessStockfishEval.SourceId, "test/memo");
        ChessStockfishEval.DeriveGame(b2, w2, eval, memo);

        // Game 2 shares positions 0..5 (through 3. Bc4); only its 6th (after Nf6) is new.
        Assert.Equal(afterFirst + 1, eval.Fens.Count);
        // Cached evals still deposit per game: both games carry HAS_EVAL rows.
        Assert.Contains(b2.SetInputUnitsConsumed(1).Build().Attestations,
            a => a.TypeId == ChessVocabulary.HasEvalType);
    }

    [Fact]
    public void EvalCache_RoundTrips_AndRejectsBudgetMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpsf-test-{Guid.NewGuid():N}.bin");
        try
        {
            var memo = new System.Collections.Concurrent.ConcurrentDictionary<Hash128, int?>();
            memo[Hash128.OfCanonical("p1")] = 42;
            memo[Hash128.OfCanonical("p2")] = -310;
            memo[Hash128.OfCanonical("p3")] = null; // engine-failed positions persist as null
            StockfishEvalCache.Save(path, censusVersion: 1, depth: 10, nodes: 0, memo);

            var back = StockfishEvalCache.Load(path, 1, 10, 0);
            Assert.Equal(3, back.Count);
            Assert.Equal(42, back[Hash128.OfCanonical("p1")]);
            Assert.Equal(-310, back[Hash128.OfCanonical("p2")]);
            Assert.Null(back[Hash128.OfCanonical("p3")]);

            // Different budget or census version = different testimony = cold cache.
            Assert.Empty(StockfishEvalCache.Load(path, 1, 12, 0));
            Assert.Empty(StockfishEvalCache.Load(path, 1, 10, 80_000));
            Assert.Empty(StockfishEvalCache.Load(path, 2, 10, 0));
        }
        finally { File.Delete(path); }
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
        finally { File.Delete(path); }
    }

    [Fact]
    public void DeriveGame_EmitsOnlyDeclaredRelations()
    {
        // Same gate as ChessRelationGateTests, over the stockfish lane's emissions.
        var declared = ChessSeedManifest.Relations
            .Select(RelationTypeRegistry.RelationTypeId).ToHashSet();
        var change = Derive(new ScriptedEvaluator(new int?[] { 20, -15, 25, -30, 90, -120, 500 }));
        var undeclared = change.Attestations
            .Select(a => a.TypeId).Distinct().Where(t => !declared.Contains(t)).ToList();
        Assert.Empty(undeclared);
    }
}
