using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCanonicalTests
{
    [Theory]
    [InlineData(300, "0:05:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(0, "0:00:00")]
    public void ClockFromSeconds_Canonical(double sec, string expected)
        => Assert.Equal(expected, ChessCanonical.ClockFromSeconds(sec));

    [Fact]
    public void ClockFromMatch_NormalizesPadding()
    {
        Assert.Equal("0:05:00", ChessCanonical.ClockFromMatch("0", "5", "0"));
        Assert.Equal("1:01:01", ChessCanonical.ClockFromMatch("1", "1", "1"));
    }

    [Fact]
    public void ClockTokens_DedupeViaContentEmitter()
    {
        var idA = ContentEmitter.RootId("0:05:00");
        var idB = ContentEmitter.RootId("0:05:00");
        var idC = ContentEmitter.RootId("0:05:01");
        Assert.NotNull(idA);
        Assert.Equal(idA, idB);
        Assert.NotEqual(idA, idC);
    }

    [Theory]
    [InlineData(" 0.35 ", "0.35")]
    [InlineData("#-3", "#-3")]
    public void EvalToken_Trims(string raw, string expected)
        => Assert.Equal(expected, ChessCanonical.EvalToken(raw));

    [Theory]
    [InlineData(0.5, "rushed")]
    [InlineData(1.0, "normal")]
    [InlineData(1.5, "deep")]
    public void ThinkClass_Buckets(double tf, string expected)
        => Assert.Equal(expected, ChessCanonical.ThinkClass(tf));
}
