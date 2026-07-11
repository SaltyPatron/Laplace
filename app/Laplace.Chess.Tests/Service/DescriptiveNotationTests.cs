using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class DescriptiveNotationTests
{
    private static ChessState Play(params string[] sans)
    {
        var m = new ChessModality();
        var s = m.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            Assert.True(mv is not null, $"setup SAN '{san}' unresolved");
            s = m.Apply(s, mv!.Value);
        }
        return s;
    }

    private static string? Resolve(ChessState s, string token)
    {
        var m = new ChessModality();
        return DescriptiveNotation.Resolve(s.Board, m.LegalActions(s), token)?.ToUci();
    }

    [Theory]
    [InlineData("P-K4", "e2e4")]
    [InlineData("P-Q4", "d2d4")]
    [InlineData("Kt-KB3", "g1f3")]
    [InlineData("N-KB3", "g1f3")]
    [InlineData("P-QR3", "a2a3")]
    [InlineData("P - K 4", "e2e4")] // Capablanca's spaced style
    public void Start_White(string token, string uci)
        => Assert.Equal(uci, Resolve(Play(), token));

    [Fact]
    public void Start_KtB3_IsAmbiguous()
        => Assert.Null(Resolve(Play(), "Kt-B3")); // QB3 (Nc3) and KB3 (Nf3) both legal

    [Fact]
    public void Black_RanksCountFromMoverSide()
        => Assert.Equal("e7e5", Resolve(Play("e4"), "P-K4"));

    [Fact]
    public void Black_KnightToQB3()
        => Assert.Equal("b8c6", Resolve(Play("e4", "e5", "Nf3"), "Kt-QB3"));

    [Fact]
    public void RuyLopez_Bishop()
        => Assert.Equal("f1b5", Resolve(Play("e4", "e5", "Nf3", "Nc6"), "B-Kt5"));

    [Fact]
    public void PawnCapture()
        => Assert.Equal("e4d5", Resolve(Play("e4", "d5"), "PxP"));

    [Fact]
    public void KnightTakesPawn()
        => Assert.Equal("f3e5", Resolve(Play("e4", "e5", "Nf3", "d6"), "KtxP"));

    [Fact]
    public void CheckSuffixIsIgnored()
        => Assert.Equal("f1b5", Resolve(Play("e4", "e5", "Nf3", "Nc6"), "B-Kt5ch"));

    [Fact]
    public void Castles_Kingside()
    {
        var s = Play("e4", "e5", "Nf3", "Nc6", "Bb5", "a6", "Ba4", "Nf6");
        Assert.Equal("e1g1", Resolve(s, "Castles"));
        Assert.Equal("e1g1", Resolve(s, "O-O"));
        Assert.Equal("e1g1", Resolve(s, "0-0"));
    }

    [Fact]
    public void Promotion_Parenthesized()
    {
        var m = new ChessModality();
        var s = m.FromFen("8/4P3/8/8/8/8/8/K1k5 w - - 0 1");
        var mv = DescriptiveNotation.Resolve(s.Board, m.LegalActions(s), "P-K8(Q)");
        Assert.Equal("e7e8q", mv?.ToUci());
    }

    [Fact]
    public void Promotion_DefaultsToQueen()
    {
        var m = new ChessModality();
        var s = m.FromFen("8/4P3/8/8/8/8/8/K1k5 w - - 0 1");
        var mv = DescriptiveNotation.Resolve(s.Board, m.LegalActions(s), "P-K8");
        Assert.Equal("e7e8q", mv?.ToUci());
    }

    [Fact]
    public void WingHint_PicksTheRook()
    {
        // Both rooks can reach d1/f1 (king off the back rank); the wing prefix must
        // disambiguate, its absence must not.
        var m = new ChessModality();
        var s = m.FromFen("4k3/8/8/8/8/4K3/8/R6R w - - 0 1");
        Assert.Equal("a1d1", Resolve(s, "QR-Q1"));
        Assert.Equal("h1f1", Resolve(s, "KR-KB1"));
        Assert.Null(Resolve(s, "R-Q1")); // ambiguous
    }

    [Fact]
    public void Garbage_ReturnsNull()
    {
        Assert.Null(Resolve(Play(), "White"));
        Assert.Null(Resolve(Play(), "and"));
        Assert.Null(Resolve(Play(), "P-K9"));
        Assert.Null(Resolve(Play(), ""));
    }
}
