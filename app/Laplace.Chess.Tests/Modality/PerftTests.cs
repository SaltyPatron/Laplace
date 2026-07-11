using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public class PerftTests
{
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Pos3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Pos4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Pos5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    private const string Pos6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    private static long Perft(string fen, int depth) => Laplace.Modality.Chess.Perft.Run(Board.FromFen(fen), depth);

    // Depth ≤4 stays in the default suite. Deeper nodes are Tier=perf (excluded by test-app).

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    [InlineData(4, 197281)]
    public void Startpos_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Startpos, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Startpos_d5() => Assert.Equal(4865609L, Perft(Startpos, 5));

    [Fact]
    [Trait("Tier", "perf")]
    public void Startpos_d6() => Assert.Equal(119060324L, Perft(Startpos, 6));

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, 2039)]
    [InlineData(3, 97862)]
    public void Kiwipete_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Kiwipete, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Kiwipete_d4() => Assert.Equal(4085603L, Perft(Kiwipete, 4));

    [Fact]
    [Trait("Tier", "perf")]
    public void Kiwipete_d5() => Assert.Equal(193690690L, Perft(Kiwipete, 5));

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 191)]
    [InlineData(3, 2812)]
    [InlineData(4, 43238)]
    public void Pos3_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Pos3, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos3_d5() => Assert.Equal(674624L, Perft(Pos3, 5));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos3_d6() => Assert.Equal(11030083L, Perft(Pos3, 6));

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 264)]
    [InlineData(3, 9467)]
    public void Pos4_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Pos4, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos4_d4() => Assert.Equal(422333L, Perft(Pos4, 4));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos4_d5() => Assert.Equal(15833292L, Perft(Pos4, 5));

    [Theory]
    [InlineData(1, 44)]
    [InlineData(2, 1486)]
    [InlineData(3, 62379)]
    public void Pos5_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Pos5, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos5_d4() => Assert.Equal(2103487L, Perft(Pos5, 4));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos5_d5() => Assert.Equal(89941194L, Perft(Pos5, 5));

    [Theory]
    [InlineData(1, 46)]
    [InlineData(2, 2079)]
    [InlineData(3, 89890)]
    public void Pos6_Fast(int depth, long expected) => Assert.Equal(expected, Perft(Pos6, depth));

    [Fact]
    [Trait("Tier", "perf")]
    public void Pos6_d4() => Assert.Equal(3894594L, Perft(Pos6, 4));
}
