using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class SubstructureFoldBiasTests
{
    private sealed class FakeValuer : IStateValuer
    {
        private readonly Func<int, double> _f;
        public FakeValuer(Func<int, double> f) => _f = f;
        public Task<double[]> ValueStatesAsync(IReadOnlyList<string> s, CancellationToken ct = default)
            => Task.FromResult(Enumerable.Range(0, s.Count).Select(_f).ToArray());
    }

    private static (Board board, IReadOnlyList<ChessMove> moves) Start()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        return (board, MoveGen.Legal(board));
    }

    [Fact]
    public void NeutralFold_GivesZeroBonus_TheClassicalFloorStands()
    {
        var (board, moves) = Start();
        var bias = new SubstructureFoldBias(new FakeValuer(_ => GlickoPriors.NeutralMu));
        var bonus = bias.Bonus(board, moves);
        Assert.Equal(moves.Count, bonus.Length);
        Assert.All(bonus, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ChildGoodForOpponent_BecomesNegativeForUs()
    {
        var (board, moves) = Start();
        var bias = new SubstructureFoldBias(
            new FakeValuer(i => i == 0 ? GlickoPriors.NeutralMu + 10d * 1e9 : GlickoPriors.NeutralMu),
            cpPerPoint: 8.0, capCp: 150);
        var bonus = bias.Bonus(board, moves);
        Assert.Equal(-80, bonus[0]);
        for (int i = 1; i < bonus.Length; i++) Assert.Equal(0, bonus[i]);
    }

    [Fact]
    public void ChildBadForOpponent_BecomesPositiveForUs()
    {
        var (board, moves) = Start();
        var bias = new SubstructureFoldBias(
            new FakeValuer(i => i == 1 ? GlickoPriors.NeutralMu - 10d * 1e9 : GlickoPriors.NeutralMu),
            cpPerPoint: 8.0, capCp: 150);
        var bonus = bias.Bonus(board, moves);
        Assert.Equal(+80, bonus[1]);
    }

    [Fact]
    public void LargeDeviation_IsClampedToCap()
    {
        var (board, moves) = Start();
        var bias = new SubstructureFoldBias(
            new FakeValuer(i => i == 0 ? GlickoPriors.NeutralMu + 1000d * 1e9 : GlickoPriors.NeutralMu),
            cpPerPoint: 8.0, capCp: 150);
        var bonus = bias.Bonus(board, moves);
        Assert.Equal(-150, bonus[0]);
    }

    [Fact]
    public void EmptyMoves_ReturnsEmpty()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var bias = new SubstructureFoldBias(new FakeValuer(_ => GlickoPriors.NeutralMu));
        Assert.Empty(bias.Bonus(board, Array.Empty<ChessMove>()));
    }
}
