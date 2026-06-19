using Laplace.Decomposers.WordNet;
using Xunit;

namespace Laplace.Decomposers.WordNet.Tests;

public sealed class WordNetDecomposerTests
{
    [Fact]
    public void TryParseDataLine_VerbSynset_ParsesVerbFrames()
    {
        const string line =
            "00002325 29 v 01 respire 1 005 $ 00001740 v 0000 @ 02108377 v 0000 "
            + "+ 03110322 a 0101 + 00831191 n 0103 + 00830811 n 0101 01 + 02 00 "
            + "| undergo the biomedical and metabolic processes of respiration";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        Assert.Equal(2325L, syn.Offset);
        Assert.Equal('v', syn.SsType);
        Assert.Single(syn.Frames);
        Assert.Equal((2, 0), syn.Frames[0]);
    }

    [Fact]
    public void TryParseDataLine_VerbWithoutFrameBlock_HasNoFrames()
    {
        const string line =
            "00001740 29 v 04 breathe 0 take_a_breath 0 respire 0 suspire 3 021 "
            + "* 00005041 v 0000 | draw air into, and expel out of, the lungs";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        Assert.Equal('v', syn.SsType);
        Assert.Empty(syn.Frames);
    }
}
