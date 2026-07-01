using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class EvaluationTests
{
    private static Board Mirror(Board b)
    {
        var m = new Board { WhiteToMove = !b.WhiteToMove };
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            int file = Board.FileOf(sq), rank = Board.RankOf(sq);
            m.Squares[Board.Sq(file, 7 - rank)] = (Piece)(-(sbyte)p);
        }
        return m;
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3")]
    [InlineData("rnbq1rk1/pp2bppp/2p2n2/3p4/2PP4/2N1PN2/PP2BPPP/R1BQ1RK1 w - - 0 1")]
    public void MirrorSymmetry_EvalIsColorIndependent(string fen)
    {
        var b = Board.FromFen(fen);
        Assert.Equal(Evaluation.Evaluate(b), Evaluation.Evaluate(Mirror(b)));
    }

    [Fact]
    public void Startpos_IsBalanced_MoverHasTempo()
    {
        int e = Evaluation.Evaluate(Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"));
        Assert.InRange(e, 1, 20);
    }

    [Fact]
    public void SideToMove_TempoInvariant()
    {
        const string pieces = "r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R";
        int w = Evaluation.Evaluate(Board.FromFen($"{pieces} w - - 0 1"));
        int bl = Evaluation.Evaluate(Board.FromFen($"{pieces} b - - 0 1"));
        Assert.Equal(20, w + bl);
    }

    [Fact]
    public void Material_QueenUp_IsDecisive_FromMoverPerspective()
    {
        int whiteToMove = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1"));
        int blackToMove = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 b - - 0 1"));
        Assert.True(whiteToMove > 800, $"white up a queen, white to move → {whiteToMove}");
        Assert.True(blackToMove < -800, $"black down a queen, black to move → {blackToMove}");
    }

    [Fact]
    public void Pst_CentralKnight_BeatsCornerKnight()
    {
        int center = Evaluation.Evaluate(Board.FromFen("4k3/8/8/3N4/8/8/8/4K3 w - - 0 1"));
        int corner = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/N3K3 w - - 0 1"));
        Assert.True(center > corner + 40, $"central knight {center} should dominate cornered knight {corner}");
    }

    [Theory]
    [InlineData(EvalTerm.Material)]
    [InlineData(EvalTerm.Pst)]
    [InlineData(EvalTerm.BishopPair)]
    [InlineData(EvalTerm.RookFiles)]
    [InlineData(EvalTerm.PawnStructure)]
    [InlineData(EvalTerm.Tempo)]
    public void EveryOverlay_IsIndividuallyMirrorSymmetric(EvalTerm term)
    {
        var b = Board.FromFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        Assert.Equal(Evaluation.Evaluate(b, term), Evaluation.Evaluate(Mirror(b), term));
    }

    [Fact]
    public void Toggle_DisablingMaterial_RemovesTheQueenAdvantage()
    {
        var b = Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        int withMat = Evaluation.Evaluate(b, EvalTerm.All);
        int noMat   = Evaluation.Evaluate(b, EvalTerm.All & ~EvalTerm.Material);
        Assert.True(withMat > 800, $"with material the queen shows: {withMat}");
        Assert.True(noMat < withMat - 800, $"without material the queen value is gone: {noMat}");
    }

    [Fact]
    public void Toggle_PstAlone_StillPrefersTheCenter()
    {
        int center = Evaluation.Evaluate(Board.FromFen("4k3/8/8/3N4/8/8/8/4K3 w - - 0 1"), EvalTerm.Pst);
        int corner = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/N3K3 w - - 0 1"), EvalTerm.Pst);
        Assert.True(center > corner, $"PST-only: central knight {center} > cornered {corner}");
    }

    [Fact]
    public void BishopPair_IsRewarded()
    {
        int pair = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/2B1KB2 w - - 0 1"));
        int mixed = Evaluation.Evaluate(Board.FromFen("4k3/8/8/8/8/8/8/2B1KN2 w - - 0 1"));
        Assert.True(pair > mixed, $"bishop pair {pair} should beat bishop+knight {mixed}");
    }
}
