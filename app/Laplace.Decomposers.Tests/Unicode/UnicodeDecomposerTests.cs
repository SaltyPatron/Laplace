using Laplace.Decomposers.Tests.Structured;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

public sealed class UnicodeDecomposerTests
{
    [Fact]
    public void CompleteScalarFloorRemainsAClientCache()
    {
        CodepointPerfcache.LoadDefault();
        Assert.Equal(1_112_064, CodepointPerfcache.Count);
    }

    [Fact]
    public async Task NativeUcdRecordsRetainNamesAndMappingsWithoutDefaultOrEqualScriptClaims()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("ucd.all.flat.xml", """
            <?xml version="1.0"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <char cp="0041" na="LATIN CAPITAL LETTER A" lc="0061"
                      sc="Latn" scx="Latn" NFC_QC="Y" nv="NaN"/>
                <char cp="0061" na="LATIN SMALL LETTER A" uc="0041"/>
              </repertoire>
            </ucd>
            """);
        string[] claims = (await fixture.Claims("unicode", path)).Claims();
        Assert.Contains("claim\t0\t1\tA\tName\tLATIN CAPITAL LETTER A", claims);
        Assert.Contains("claim\t0\t1\tA\tLowercase_Mapping\ta", claims);
        Assert.Contains("claim\t0\t1\ta\tUppercase_Mapping\tA", claims);
        Assert.DoesNotContain(claims, line => line.Contains("\tNaN", StringComparison.Ordinal)
            || line.Contains("\tNFC_QC\t", StringComparison.Ordinal)
            || line.Contains("\tScript_Extensions\t", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NamedSequences.txt", "Name of Sequence")]
    [InlineData("NamedSequencesProv.txt", "Name of Sequence")]
    public async Task NativeNamedSequencesKeepCanonicalCharactersAndSourceName(string file, string relation)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write(file, "# Name of Sequence; Code Point Sequence\nLATIN PAIR;0041 0042\n");
        Assert.Contains("claim\t0\t1\tAB\t" + relation + "\tLATIN PAIR",
            (await fixture.Claims("unicode", path)).Claims());
    }

    [Fact]
    public async Task NativeNormalizationKeepsEveryChangedFormAndSkipsIdentityResults()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("NormalizationTest.txt",
            "@Part0\n00C5;00C5;0041 030A;00C5;0041 030A; # A ring\n");
        Assert.Equal(new[]
        {
            "claim\t0\t1\tÅ\tnormalizes to NFD\tÅ",
            "claim\t0\t1\tÅ\tnormalizes to NFKD\tÅ"
        }, (await fixture.Claims("unicode", path)).Claims());
    }
}