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

    private sealed class CountingEvaluator : ISearchPositionEvaluator
    {
        public int Calls { get; private set; }
        public int Evaluate(Board board)
        {
            Calls++;
            return 0;
        }
    }
}
