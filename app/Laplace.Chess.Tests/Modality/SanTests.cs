using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class SanTests
{
    private static ChessState Replay(string sanMoves, out int resolved)
    {
        var m = new ChessModality();
        var s = m.Initial();
        resolved = 0;
        foreach (var san in sanMoves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            Assert.True(mv is not null, $"unresolved SAN '{san}' at ply {resolved}");
            s = m.Apply(s, mv!.Value);
            resolved++;
        }
        return s;
    }

    [Fact]
    public void Replays_RealGame_ToCheckmate()
    {
        const string game =
            "g3 Nf6 Bg2 d5 Nf3 e6 d4 Ne4 O-O Bd6 Bg5 Nxg5 c3 Ne4 Ng5 Nxg5 e3 Ne4 f4 O-O " +
            "Qh5 g6 Qh6 Re8 c4 dxc4 Nc3 Nd2 Rfe1 f5 Rad1 Ne4 d5 exd5 Rxd5 Nc6 Nxe4 fxe4 " +
            "Bxe4 Nb4 Bxg6 Nxd5 Qxh7+ Kf8 Qf7#";
        var end = Replay(game, out int resolved);
        Assert.Equal(45, resolved);
        var m = new ChessModality();
        Assert.Equal(GameOutcome.WonBy(0), m.Terminal(end));
    }

    [Theory]
    [InlineData("e4 e5 Nf3 Nc6 Bb5", 5)]
    [InlineData("e4 c5 Nf3 d6 d4 cxd4 Nxd4 Nf6 Nc3 a6", 10)]
    [InlineData("d4 d5 c4 e6 Nc3 Nf6 Bg5 Nbd7", 8)]
    public void Replays_Openings(string moves, int expected)
    {
        Replay(moves, out int resolved);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolves_Castling_Both_Sides()
    {
        var end = Replay("e4 d5 Nf3 Nc6 Bc4 Bg4 O-O Qd7 Nc3 O-O-O", out _);
        Assert.NotNull(end);
    }

    [Fact]
    public void Resolves_Promotion()
    {
        var m = new ChessModality();
        var s = m.FromFen("8/P6k/8/8/8/8/7K/8 w - - 0 1");
        var mv = San.Resolve(s.Board, m.LegalActions(s), "a8=Q");
        Assert.NotNull(mv);
        Assert.True(mv!.Value.IsPromotion);
        Assert.Equal("a7a8q", mv.Value.ToUci());
    }
}
