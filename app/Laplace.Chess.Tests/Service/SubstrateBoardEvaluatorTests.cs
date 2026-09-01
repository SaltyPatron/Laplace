using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class SubstrateBoardEvaluatorTests
{
    [Fact]
    public void ConstituentEvidence_IsEvaluatedAtSearchLeavesInSideToMovePerspective()
    {
        var white = Board.FromFen(ChessModality.StartFen);
        var black = Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1");
        Hash128 pawnOnE2 = ChessCompose.Position(white).Substructures
            .Select(static n => n.Id)
            .Intersect(ChessCompose.Position(black).Substructures.Select(static n => n.Id))
            .First(id => id != ChessPositionIdentity.AtomId(
                ChessPositionIdentity.Atom.Scalar(ChessPositionIdentity.CastlingDomain, 15)));
        var evaluator = new SubstrateBoardEvaluator(
            new Dictionary<Hash128, (double, double, double)>
            {
                [pawnOnE2] = (GlickoPriors.NeutralMu + 1_000_000_000d, 0d, 16d),
            }, cpPerPoint: 20d, capCp: 200);

        Assert.Equal(20, evaluator.Evaluate(white));
        Assert.Equal(-20, evaluator.Evaluate(black));
    }

    [Fact]
    public void Search_UsesConventionalAndSubstrateEvaluationAtConfiguredDepth()
    {
        var evaluator = new CountingEvaluator();
        var search = new Search(EvalTerm.All, positionEvaluator: evaluator, ttBits: 10);
        var result = search.Think(Board.FromFen(ChessModality.StartFen),
            new Search.Limits(MaxDepth: 2, MaxNodes: 200_000, MaxTimeMs: 10_000));

        Assert.Equal(2, result.Depth);
        Assert.NotNull(result.BestMove);
        Assert.True(evaluator.Calls > 20, $"expected leaf evaluation across the tree, got {evaluator.Calls}");
    }

    [Fact]
    public void CompletedGame_AdvancesTheImmutableEvidenceUsedByTheNextSearch()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        Hash128 pawnOnE2 = ChessPositionIdentity.AtomId(
            ChessPositionIdentity.Atom.Scalar(
                ChessPositionIdentity.PieceSquareDomain,
                checked((ushort)(ChessPositionIdentity.PieceOrdinal(Piece.WPawn) * 64 + 12))));
        long epoch = 0;
        double outcome = 1_000_000_000d;
        var evaluator = new SubstrateBoardEvaluator(
            () => new Dictionary<Hash128, (double, double, double)>
            {
                [pawnOnE2] = (GlickoPriors.NeutralMu + outcome, 0d, 16d),
            },
            () => epoch,
            cpPerPoint: 20d);

        var before = evaluator.PrepareSearch();
        Assert.Equal(20, before.Evaluate(board));

        outcome = -1_000_000_000d;
        epoch++;
        var after = evaluator.PrepareSearch();

        Assert.True(after.Version > before.Version);
        Assert.Equal(20, before.Evaluate(board)); // an in-flight tree stays on its generation
        Assert.Equal(-20, after.Evaluate(board));
    }

    [Fact]
    public void Search_InvalidatesTranspositionsWhenEvidenceGenerationChanges()
    {
        var board = Board.FromFen("4k3/8/8/8/8/8/8/R3K3 w - - 0 1");
        var evaluator = new VersionedRookEvaluator();
        var reused = new Search(EvalTerm.None, positionEvaluator: evaluator, ttBits: 10);
        _ = reused.Think(board, new Search.Limits(MaxDepth: 2, MaxTimeMs: 10_000));

        evaluator.PreferHighFile = true;
        evaluator.Generation++;
        var actual = reused.Think(board, new Search.Limits(MaxDepth: 2, MaxTimeMs: 10_000));
        var expected = new Search(EvalTerm.None, positionEvaluator: evaluator, ttBits: 10)
            .Think(board, new Search.Limits(MaxDepth: 2, MaxTimeMs: 10_000));

        Assert.Equal(expected.BestMove, actual.BestMove);
        Assert.Equal(expected.Score, actual.Score);
    }

    private sealed class CountingEvaluator : ISearchPositionEvaluator
    {
        public int Calls { get; private set; }
        public int Evaluate(Board board)
        {
            Calls++;
            return 0;
        }
    }

    private sealed class VersionedRookEvaluator : ISearchPositionEvaluator
    {
        public bool PreferHighFile { get; set; }
        public long Generation { get; set; }
        public long Version => Generation;

        public int Evaluate(Board board)
        {
            int file = 0;
            for (int square = 0; square < 128; square++)
            {
                if ((square & 0x88) != 0) { square += 7; continue; }
                if (board.Squares[square] == Piece.WRook) { file = Board.FileOf(square); break; }
            }
            int whiteScore = (PreferHighFile ? file : 7 - file) * 1_000;
            return board.WhiteToMove ? whiteScore : -whiteScore;
        }
    }
}
