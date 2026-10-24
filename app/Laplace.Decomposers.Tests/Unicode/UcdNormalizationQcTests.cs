using Laplace.Decomposers.Tests.Structured;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

public sealed class UcdNormalizationQcTests
{
    [Fact]
    public async Task OmittedDefaultQuickCheckValuesDoNotBecomeSyntheticVotes()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("ucd.all.flat.xml", """
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0"><repertoire>
              <char cp="0041" NFC_QC="Y" NFD_QC="Y" NFKC_QC="Y" NFKD_QC="Y"/>
              <char cp="0042"/>
            </repertoire></ucd>
            """);
        Assert.Empty((await fixture.Claims("unicode", path)).Claims());
    }
}