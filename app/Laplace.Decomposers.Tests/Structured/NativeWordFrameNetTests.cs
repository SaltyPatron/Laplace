using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class NativeWordFrameNetTests
{
    private const string Wfn = "Frame: Abounding_with\n"
        + "brushed a 01105125-a having soft nap produced by brushing\n"
        + "cloaked a 01644908-a having its true character concealed with the intent of misleading\n\n"
        + "gilded a 00357392-a having the deep slightly brownish color of gold\n\n"
        + "Frame: Abusing\nabusive|a 01114176-a 0 by physical or psychological maltreatment\n";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SourceRowsRetainTheirFieldsAndFrameAcrossBlankLines(string newline)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("WFN/WordFrameNet", Wfn.Replace("\n", newline));
        Assert.Equal(new[]
        {
            "claim\t0\t1\tAbounding_with\t[brushed, eng, [WordNet, 1.6, 01105125, a]]\ta\thaving soft nap produced by brushing",
            "claim\t0\t1\tAbounding_with\t[cloaked, eng, [WordNet, 1.6, 01644908, a]]\ta\thaving its true character concealed with the intent of misleading",
            "claim\t0\t1\tAbounding_with\t[gilded, eng, [WordNet, 1.6, 00357392, a]]\ta\thaving the deep slightly brownish color of gold",
            "claim\t0\t1\tAbusing\t[abusive, eng, [WordNet, 1.6, 01114176, a]]\ta\t0\tby physical or psychological maltreatment"
        }, (await fixture.Claims("wordframenet", path)).Claims());
    }

    [Fact]
    public async Task ExtendedRowsDoNotInventDefinitionsOrLoseTheFinalLine()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("XWFN/eXtendedWFN",
            "Frame: Abounding_with\nfleecy a 01105125-a\n\nnapped a 01105125-a");
        Assert.Equal(new[]
        {
            "claim\t0\t1\tAbounding_with\t[fleecy, eng, [WordNet, 1.6, 01105125, a]]\ta",
            "claim\t0\t1\tAbounding_with\t[napped, eng, [WordNet, 1.6, 01105125, a]]\ta"
        }, (await fixture.Claims("wordframenet", path)).Claims());
    }

    [Fact]
    public async Task MultiwordSpellingsShareContentAndRetainTheirSourceOccurrences()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("XWFN/eXtendedWFN",
            "Frame: Motion\nback_and_forth r 00114809-r\nback and forth r 00114809-r\n");
        string[][] claims = (await fixture.Claims("wordframenet", path)).Claims()
            .Select(line => line.Split('\t')).ToArray();
        Assert.NotEmpty(claims);
        foreach (string[] claim in claims)
            Assert.Equal(new[] { "Motion", "[back and forth, eng, [WordNet, 1.6, 00114809, r]]", "r" }, claim.Skip(3));
        Assert.Equal(2, claims.Sum(claim => int.Parse(claim[2], System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task SourcePosAndReferencedSynsetCategoryRemainDistinct()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("WFN/WordFrameNet",
            "Frame: Activity\nprepared|a 01771525-s 0 ready beforehand\n");
        Assert.Equal(new[] { "claim\t0\t1\tActivity\t[prepared, eng, [WordNet, 1.6, 01771525, s]]\ta\t0\tready beforehand" },
            (await fixture.Claims("wordframenet", path)).Claims());
    }

    [Theory]
    [InlineData("brushed a 01105125-a definition\n")]
    [InlineData("Frame:\nbrushed a 01105125-a definition\n")]
    [InlineData("Frame: Abounding_with\nbrushed q 01105125-a definition\n")]
    [InlineData("Frame: Abounding_with\nbrushed a not-a-reference definition\n")]
    public async Task MalformedPackagingIsRejected(string source)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("WFN/WordFrameNet", source);
        var result = await fixture.Claims("wordframenet", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WFN/WordFrameNet", Wfn)]
    [InlineData("XWFN/eXtendedWFN", "Frame: Abounding_with\nfleecy a 01105125-a")]
    public async Task BothSourceFormatsUseTheNormalNoLoadPipeline(string name, string source)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write(name, source);
        var result = await NativeCorpus.Run(fixture.Execution, NativeRecipeMode.NoLoad, "wordframenet", path);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Contains("mismatched 0", result.Output, StringComparison.Ordinal);
        Assert.Contains("unresolved obligations: 0", result.Output, StringComparison.Ordinal);
    }
}
