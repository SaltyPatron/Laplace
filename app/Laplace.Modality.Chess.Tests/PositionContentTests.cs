using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// The position's content surface (its substructure decomposition) and the bit-banged pawn features.
/// Pure C# — no native, no DB.
/// </summary>
public sealed class PositionContentTests
{
    private static string Surface(string fen)
    {
        var b = Board.FromFen(fen);
        // mirror ChessModality's ep canonicalization indirectly by going through the modality
        var m = new ChessModality();
        return m.StateKey(m.FromFen(fen));
    }

    [Fact]
    public void Surface_IsDeterministic()
        => Assert.Equal(Surface(ChessModality.StartFen), Surface(ChessModality.StartFen));

    [Fact]
    public void Surface_ComposesFromSubstructures()
    {
        var s = Surface(ChessModality.StartFen);
        Assert.Contains("stm:w", s);
        Assert.Contains("cr:KQkq", s);
        Assert.Contains("Pe2", s);              // exact placement token
        Assert.Contains("ke8", s);              // black king placement token
        Assert.Contains("wpawns:", s);          // pawn-skeleton shared node
        Assert.Contains("mat:P8N2B2R2Q1", s);   // material signature
        Assert.Contains("wpf:d0i0p0", s);       // start: no doubled/isolated/passed white pawns
    }

    [Fact]
    public void Surface_DistinctPositions_DistinctSurface()
    {
        var m = new ChessModality();
        var start = m.Initial();
        var e4 = m.LegalActions(start).Single(x => x.ToUci() == "e2e4");
        Assert.NotEqual(m.StateKey(start), m.StateKey(m.Apply(start, e4)));
    }

    [Fact]
    public void Surface_TranspositionsCollapse()
    {
        var m = new ChessModality();
        // 1.e4 e5 2.Nf3  vs  1.Nf3 e5 2.e4 — same position, different move order.
        var a = Play(m, "e2e4", "e7e5", "g1f3");
        var b = Play(m, "g1f3", "e7e5", "e2e4");
        Assert.Equal(m.StateKey(a), m.StateKey(b));
    }

    private static ChessState Play(ChessModality m, params string[] ucis)
    {
        var s = m.Initial();
        foreach (var u in ucis)
            s = m.Apply(s, m.LegalActions(s).Single(x => x.ToUci() == u));
        return s;
    }

    [Theory]
    // white pawn d5 alone: isolated (no friendly adjacent) and passed (no enemy ahead), not doubled.
    [InlineData("7k/8/8/3P4/8/8/8/7K w - - 0 1", 0, 1, 1)]
    // doubled white pawns d4,d3: one extra on the d-file; both isolated; only the front (d4) is passed.
    [InlineData("7k/8/8/8/3P4/3P4/8/7K w - - 0 1", 1, 2, 1)]
    public void Bitboards_PawnFeatures(string fen, int doubled, int isolated, int passed)
    {
        var bb = Bitboards.FromBoard(Board.FromFen(fen));
        var wp = bb.Of(Piece.WPawn);
        var bp = bb.Of(Piece.BPawn);
        Assert.Equal(doubled, Bitboards.Doubled(wp));
        Assert.Equal(isolated, Bitboards.Isolated(wp));
        Assert.Equal(passed, Bitboards.Passed(wp, bp, white: true));
    }

    [Fact]
    public void Bitboards_StartCounts()
    {
        var bb = Bitboards.FromBoard(Board.FromFen(ChessModality.StartFen));
        Assert.Equal(16, Bitboards.Count(bb.White));
        Assert.Equal(16, Bitboards.Count(bb.Black));
        Assert.Equal(8, Bitboards.Count(bb.Of(Piece.WPawn)));
        Assert.Equal(8, Bitboards.Count(bb.Of(Piece.BPawn)));
    }
}
