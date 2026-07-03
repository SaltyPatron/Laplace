using System.Linq;
using Laplace.Decomposers.WordNet;
using Xunit;

namespace Laplace.Decomposers.WordNet.Tests;

public sealed class WordNetDecomposerTests
{
    [Fact]
    public void TryParseDataLine_LexicalPointers_CaptureSourceWord()
    {
        
        
        
        
        const string line =
            "00001740 00 a 01 able 0 005 = 05200169 n 0000 = 05616246 n 0000 "
            + "+ 05616246 n 0101 + 05200169 n 0101 ! 00002098 a 0101 "
            + "| (usually followed by `to') having the necessary means or skill";

        Assert.True(WordNetDecomposer.TryParseDataLine(line, out var syn));
        var antonym = Assert.Single(syn.Pointers, p => p.Symbol == "!");
        Assert.Equal(1, antonym.SrcWord);   
        Assert.Equal(1, antonym.TgtWord);
        Assert.All(syn.Pointers.Where(p => p.Symbol == "="), p => Assert.Equal(0, p.SrcWord)); 
        Assert.Contains(syn.Pointers, p => p.Symbol == "+" && p.SrcWord == 1);                 
    }

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
