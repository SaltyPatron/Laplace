using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public class ModalityTests
{
    private static readonly ChessModality M = new();

    private static ChessState Play(ChessState s, string uci)
    {
        var move = M.LegalActions(s).Single(m => m.ToUci() == uci);
        return M.Apply(s, move);
    }

    [Fact]
    public void Name_Is_Chess() => Assert.Equal("chess", M.Name);

    [Fact]
    public void SideToMove_StartIsWhite() => Assert.Equal(0, M.SideToMove(M.Initial()));

    [Fact]
    public void StateKey_OmitsCounters()
    {

        var a = M.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var b = M.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 7 42");
        Assert.Equal(M.StateKey(a), M.StateKey(b));

        Assert.DoesNotContain(" 7 42", M.StateKey(b));
        Assert.Contains("stm:w", M.StateKey(a));
    }

    [Fact]
    public void StateKey_Transposition_SameKey()
    {

        var s1 = M.Initial();
        s1 = Play(s1, "e2e4");
        s1 = Play(s1, "e7e5");
        s1 = Play(s1, "g1f3");

        var s2 = M.Initial();
        s2 = Play(s2, "g1f3");
        s2 = Play(s2, "e7e5");
        s2 = Play(s2, "e2e4");

        Assert.Equal(M.StateKey(s1), M.StateKey(s2));
    }

    [Fact]
    public void ActionKey_PromotionUci()
    {

        var s = M.FromFen("k7/4P3/8/8/8/8/8/4K3 w - - 0 1");
        var promo = M.LegalActions(s).Single(m => m.ToUci() == "e7e8q");
        Assert.Equal("e7e8q", M.ActionKey(s, promo));
    }

    [Fact]
    public void Checkmate_BackRank_WhiteWins()
    {



        var s = M.FromFen("R5k1/5ppp/8/8/8/8/8/6K1 b - - 0 1");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.Equal(GameOutcome.WonBy(0), outcome!.Value);
    }

    [Fact]
    public void Checkmate_FoolsMate_BlackWins()
    {

        var s = M.FromFen("rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.Equal(GameOutcome.WonBy(1), outcome!.Value);
    }

    [Fact]
    public void Stalemate_IsDraw()
    {



        var s = M.FromFen("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsDraw);
    }

    [Fact]
    public void FiftyMove_IsDraw()
    {
        var s = M.FromFen("8/8/8/4k3/8/4K3/8/4R3 w - - 100 80");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsDraw);
    }

    [Fact]
    public void InsufficientMaterial_KvK_IsDraw()
    {
        var s = M.FromFen("8/8/4k3/8/8/4K3/8/8 w - - 0 1");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsDraw);
    }

    [Fact]
    public void InsufficientMaterial_KBvK_IsDraw()
    {
        var s = M.FromFen("8/8/4k3/8/8/4KB2/8/8 w - - 0 1");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsDraw);
    }

    [Fact]
    public void Threefold_KnightShuffle_IsDraw()
    {
        var s = M.FromFen("4k1n1/8/8/8/8/8/8/4K1N1 w - - 0 1");
        s = Play(s, "g1f3");
        s = Play(s, "g8f6");
        s = Play(s, "f3g1");
        s = Play(s, "f6g8");
        s = Play(s, "g1f3");
        s = Play(s, "g8f6");
        s = Play(s, "f3g1");
        s = Play(s, "f6g8");
        var outcome = M.Terminal(s);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsDraw);
    }

    [Fact]
    public void Threefold_FromFenAlone_DoesNotSeeHistory()
    {
        // HTTP play used to rebuild from FEN each ply — that cannot detect threefold.
        var s = M.FromFen("4k1n1/8/8/8/8/8/8/4K1N1 w - - 0 1");
        s = Play(s, "g1f3");
        s = Play(s, "g8f6");
        s = Play(s, "f3g1");
        s = Play(s, "f6g8");
        s = Play(s, "g1f3");
        s = Play(s, "g8f6");
        s = Play(s, "f3g1");
        s = Play(s, "f6g8");
        Assert.NotNull(M.Terminal(s));

        var fenOnly = M.FromFen(s.Board.ToFen());
        Assert.Null(M.Terminal(fenOnly));
    }


    [Fact]
    public void Initial_FenRoundTrip()
    {
        var s = M.Initial();
        Assert.Equal(ChessModality.StartFen, s.Board.ToFen());
    }
}
