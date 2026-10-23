using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class NativeRangeOperandTests
{
    [Theory]
    [InlineData("PropList.txt", "2010..2011 ; Hyphen\n", "claim\t0\t1\t[‐, ‑]\tHyphen")]
    [InlineData("IdentifierStatus.txt", "0041..005A ; Allowed\n", "claim\t0\t1\t[A, Z]\tAllowed")]
    public async Task TypedRangesRetainTheirBoundaryTupleInsideAThing(
        string name, string content, string expected)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write(name, content);
        Assert.Equal(new[] { expected }, (await fixture.Claims("unicode", path)).Claims());
    }
}
