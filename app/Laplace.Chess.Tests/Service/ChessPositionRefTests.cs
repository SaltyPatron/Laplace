using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>GH #575: FEN-shaped references compose to the position entity id.</summary>
public sealed class ChessPositionRefTests
{
    private const string StartFen = ChessModality.StartFen;
    private const string AfterE4 =
        "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";

    [Theory]
    [InlineData("pawn")]
    [InlineData("e4")]
    [InlineData("")]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR")] // missing fields
    public void LooksLikeFen_RejectsOrdinaryText(string text)
        => Assert.False(ChessPositionRef.LooksLikeFen(text));

    [Fact]
    public void LooksLikeFen_AcceptsStandardStart()
        => Assert.True(ChessPositionRef.LooksLikeFen(StartFen));

    [Fact]
    public void TryComposeId_MatchesExploreComposePath()
    {
        Assert.True(ChessPositionRef.TryComposeId(StartFen, out var id));

        var m = new ChessModality();
        Hash128 expected;
        lock (ChessCompose.Gate)
            expected = ChessCompose.PositionId(m.StateKey(m.Initial()));

        Assert.Equal(expected, id);
    }

    [Fact]
    public void TryComposeId_DistinctPositions_DistinctIds()
    {
        Assert.True(ChessPositionRef.TryComposeId(StartFen, out var a));
        Assert.True(ChessPositionRef.TryComposeId(AfterE4, out var b));
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// A Chess960 position composes an id now, and — the part that matters — it is a
    /// DIFFERENT id from the same board with its castling rights stripped. That was the
    /// hazard the old refusal existed to prevent; modelling the rights removes the hazard
    /// rather than dodging it.
    /// </summary>
    [Fact]
    public void TryComposeId_Chess960XFen_ComposesAndIsNotTheStrippedPosition()
    {
        Assert.True(ChessPositionRef.TryComposeId(
            "qrbbnkrn/pppppppp/8/8/8/8/PPPPPPPP/QRBBNKRN w GBgb - 0 1", out var withRights));
        Assert.True(ChessPositionRef.TryComposeId(
            "qrbbnkrn/pppppppp/8/8/8/8/PPPPPPPP/QRBBNKRN w - - 0 1", out var stripped));
        Assert.NotEqual(withRights, stripped);
    }

    [Fact]
    public void RewriteFenToHex_LeavesWordsAlone()
        => Assert.Equal("castle", ChessPositionRef.RewriteFenToHex("castle"));

    [Fact]
    public void RewriteFenToHex_Yields32Hex()
    {
        string? hex = ChessPositionRef.RewriteFenToHex(StartFen);
        Assert.False(string.IsNullOrEmpty(hex));
        Assert.Equal(32, hex!.Length);
        Assert.Matches("^[0-9a-f]{32}$", hex);
    }

    [Fact]
    public void RewriteFenToHex_IsIdempotentOnHex()
    {
        var hex = ChessPositionRef.TryComposeHex(StartFen)!;
        Assert.Equal(hex, ChessPositionRef.RewriteFenToHex(hex));
    }
}
