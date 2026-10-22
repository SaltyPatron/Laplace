using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public sealed class TrailingNewlineRoundtripTests
{
    public TrailingNewlineRoundtripTests(PerfcacheTestFixture _) { }

    [Theory]
    [InlineData("foo.\r\n\r\n")]
    [InlineData("a\r\n\r\n")]
    [InlineData("line one\r\nline two\r\n\r\n")]
    public void Decompose_CoversAllBytes(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        using var tree = TextDecomposer.Run(utf8);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        Assert.Equal(Hash128.Text(text), root.Id);
        Assert.Equal(0u, root.TextRangeOff);
        Assert.Equal((uint)utf8.Length, root.TextRangeLen);
    }

    [Fact]
    public void TrailingBlankLine_RetainsAllScalarContentWithoutEmptyBranches()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("foo.\r\n\r\n");
        using var tree = TextDecomposer.Run(utf8);
        using var reconstructed = new MemoryStream();
        for (uint i = 0; i < (uint)tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            if (n.Tier == 0)
                reconstructed.Write(Encoding.UTF8.GetBytes(new Rune((int)n.Atom).ToString()));
            else
                Assert.True(n.ChildCount > 0);
        }
        Assert.Equal(utf8, reconstructed.ToArray());
    }
}
