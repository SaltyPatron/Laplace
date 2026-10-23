using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class UnicodeSourceSemanticsTests
{
    [Fact]
    public async Task LinkFormattingPreservesPairsAndUrlFragments()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkFormattingTest.txt",
            "# comment\nhttps://example.com/#%CE%B1\n\n# between pair\nhttps://example.com/#α\n");
        Assert.Equal(new[] { "claim\t0\t1\thttps://example.com/#%CE%B1\tnormalizes to\thttps://example.com/#α" },
            (await fixture.Claims("unicode", path)).Claims());
    }

    [Fact]
    public async Task LinkFormattingRejectsAnIncompletePair()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkFormattingTest.txt", "https://example.com/%CE%B1\n");
        var result = await fixture.Claims("unicode", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkAnnotationsRetainRepeatedSpansAndUnicodePositions()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkDetectionTest.txt", "é ⸠a.com#x⸡ ⸠a.com#x⸡\nno link here\n");
        var result = await fixture.Claims("unicode", path);
        string[] claims=result.Claims();
        Assert.Equal(2,claims.Length);
        string records = string.Join('\n', claims.Concat(result.Lines("held")).Concat(result.Lines("record")));
        Assert.Contains("é a.com#x a.com#x", records, StringComparison.Ordinal);
        Assert.Contains(claims,line => line.Split('\t')[3]=="[é a.com#x a.com#x, 2, 8]");
        Assert.Contains(claims,line => line.Split('\t')[3]=="[é a.com#x a.com#x, 10, 16]");
        Assert.Contains("no link here", records, StringComparison.Ordinal);
        Assert.DoesNotContain("⸠", records, StringComparison.Ordinal);
        Assert.DoesNotContain("⸡", records, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("⸠unterminated")]
    [InlineData("unopened⸡")]
    [InlineData("⸠nested ⸠link⸡⸡")]
    public async Task MalformedLinkAnnotationsAreRejected(string source)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkDetectionTest.txt", source + "\n");
        var result = await fixture.Claims("unicode", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LinkBracket.txt", "0029 ; 0028 # closing to opening\n", "claim\t0\t1\t)\tLink_Bracket\t(")]
    [InlineData("LinkEmail.txt", "# @missing: 0000..10FFFF; No\n0041..0042 # range\n", "claim\t0\t1\t[A, B]\tLink_Email")]
    [InlineData("LinkTerm.txt", "# @missing: 0000..10FFFF; Include\n0021 ; Soft\n", "claim\t0\t1\t!\tLink_Term\tSoft")]
    [InlineData("LinkTerm.txt", "0041..0042 ; Soft\n", "claim\t0\t1\t[A, B]\tLink_Term\tSoft")]
    public async Task LinkPropertiesRetainTheirScalarsRangeBoundsAndSourcePropertyNames(
        string name, string content, string expected)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write(name, content);
        Assert.Equal(new[] { expected }, (await fixture.Claims("unicode", path)).Claims());
    }
}
