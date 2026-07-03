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
        Assert.Equal(4, root.Tier);
        Assert.Equal(0u, root.TextRangeOff);
        Assert.Equal((uint)utf8.Length, root.TextRangeLen);
    }

    [Fact]
    public void TrailingBlankLine_SentencesHaveWordChildren()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("foo.\r\n\r\n");
        using var tree = TextDecomposer.Run(utf8);
        int sentences = 0, emptySentences = 0;
        for (uint i = 0; i < (uint)tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            if (n.Tier != 3) continue;
            sentences++;
            if (n.ChildCount == 0) emptySentences++;
        }
        Assert.Equal(2, sentences);
        Assert.Equal(0, emptySentences);
    }
}
