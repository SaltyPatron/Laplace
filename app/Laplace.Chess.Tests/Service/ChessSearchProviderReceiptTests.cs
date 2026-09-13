using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessSearchProviderReceiptTests
{
    private sealed class RootBias : IRootBias
    {
        public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
        {
            var result = new int[moves.Count];
            if (result.Length > 0) result[0] = 25;
            return result;
        }
    }

    private sealed class PositionPlanes : IChessSearchPositionPlanes
    {
        public long Version => 7;
        public int Evaluate(Board board) => EvaluatePlanes(board).TotalCp;
        public ChessPositionPlaneScore EvaluatePlanes(Board board)
            => new(TotalCp: 20, AtomOutcomeCp: 5, LearnedPstCp: 12, TacticOutcomeCp: 3);
    }

    [Fact]
    public void ReceiptCountsProvidersOnlyWhenSearchConsumesThem()
    {
        var configured = new ChessSearchConfiguration(
            substrate: true,
            rootBias: new RootBias(),
            positionEvaluator: new PositionPlanes(),
            learnedSelected: true,
            learnedContributes: true,
            learnedNonZeroCells: 42);
        var search = configured.BuildSearch(ttBits: 10);

        var result = search.Think(
            Board.FromFen(ChessModality.StartFen),
            new Search.Limits(MaxDepth: 1, MaxNodes: 100_000, MaxTimeMs: 5_000));
        var receipt = configured.Receipt();

        Assert.NotNull(result.BestMove);
        Assert.True(receipt.RootSteerReads > 0);
        Assert.True(receipt.RootMovesInfluenced > 0);
        Assert.True(receipt.PositionEvidenceReads > 0);
        Assert.True(receipt.PositionEvidenceContributions > 0);
        Assert.True(receipt.LearnedPstReads > 0);
        Assert.True(receipt.LearnedPstContributions > 0);
        Assert.Equal(42, receipt.LearnedPstNonZeroCells);
        Assert.True(receipt.SyzygyProbes > 0);
        Assert.Contains("learned-pst=", receipt.Summary);
    }

    [Fact]
    public void ClassicalReceiptDoesNotClaimProviders()
    {
        var receipt = ChessSearchProviderReceipt.Classical;
        Assert.False(receipt.SubstrateSelected);
        Assert.False(receipt.LearnedPstSelected);
        Assert.Equal(0, receipt.RootSteerReads);
        Assert.Equal(0, receipt.PositionEvidenceReads);
        Assert.Equal(0, receipt.SyzygyProbes);
        Assert.Equal("classical-only", receipt.Summary);
    }
}
