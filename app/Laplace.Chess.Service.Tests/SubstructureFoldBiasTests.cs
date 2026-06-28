using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Proves the substructure-fold root bias's MATH without a DB (the fold's DB read is exercised by the
/// substrate-test CLI): a fake <see cref="IStateValuer"/> returns controlled per-successor values and we
/// assert the bias negates correctly (child is opponent-to-move), scales rating-points → centipawns,
/// clamps, and falls to exactly 0 where the fold is neutral (so the classical floor stands).
/// </summary>
public sealed class SubstructureFoldBiasTests
{
    // A valuer that returns a value per successor position, by the order ValueStatesAsync receives them
    // (which is the candidate-move order the bias passes in).
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
        // Successor 0 is +10 rating-points for the side to move there (the OPPONENT) → bad for us.
        var bias = new SubstructureFoldBias(
            new FakeValuer(i => i == 0 ? GlickoPriors.NeutralMu + 10d * 1e9 : GlickoPriors.NeutralMu),
            cpPerPoint: 8.0, capCp: 150);
        var bonus = bias.Bonus(board, moves);
        Assert.Equal(-80, bonus[0]);                 // 10 pts × 8 cp, negated
        for (int i = 1; i < bonus.Length; i++) Assert.Equal(0, bonus[i]);
    }

    [Fact]
    public void ChildBadForOpponent_BecomesPositiveForUs()
    {
        var (board, moves) = Start();
        // Successor 1 is −10 rating-points for the opponent → good for us.
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
        // +1000 pts for the opponent → would be −8000 cp, clamped to −cap.
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
