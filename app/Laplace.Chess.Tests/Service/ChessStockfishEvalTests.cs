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
