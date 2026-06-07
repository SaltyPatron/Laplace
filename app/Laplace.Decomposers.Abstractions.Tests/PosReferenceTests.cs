using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

public class PosReferenceTests
{
    [Fact]
    public void ThreeTagsets_OneCanonicalValue_TheCoAssertionProof()
    {
        var canon = PosReference.CanonicalId("NOUN");
        Assert.Equal(canon, PosReference.Resolve("NOUN", PosReference.PosTagset.Upos));
        Assert.Equal(canon, PosReference.Resolve("n", PosReference.PosTagset.WordNet));
        Assert.Equal(canon, PosReference.Resolve("noun", PosReference.PosTagset.Wiktionary));
    }

    [Theory]
    [InlineData('n', "NOUN")]
    [InlineData('v', "VERB")]
    [InlineData('a', "ADJ")]
    [InlineData('s', "ADJ")]
    [InlineData('r', "ADV")]
    public void WordNet_SsTypes_MapToCanon(char ss, string expected)
        => Assert.Equal(PosReference.CanonicalId(expected),
                        PosReference.Resolve(ss.ToString(), PosReference.PosTagset.WordNet));

    [Theory]
    [InlineData("name", "PROPN")]
    [InlineData("prep", "ADP")]
    [InlineData("article", "DET")]
    [InlineData("intj", "INTJ")]
    public void Wiktionary_KnownStrings_MapToCanon(string pos, string expected)
        => Assert.Equal(PosReference.CanonicalId(expected),
                        PosReference.Resolve(pos, PosReference.PosTagset.Wiktionary));

    [Fact]
    public void UnknownTag_GoesProbationary_Logged_NeverSilent_NeverThrows()
    {
        long before = PosReference.ResolveMisses;
        var id = PosReference.Resolve("proverb", PosReference.PosTagset.Wiktionary);

        Assert.Equal(Hash128.OfCanonical("substrate/pos/probationary/wiktionary/proverb/v1"), id);
        Assert.DoesNotContain(PosReference.Canonical,
            t => PosReference.CanonicalId(t) == id);
        Assert.True(PosReference.ResolveMisses > before);
        Assert.Contains("wiktionary:proverb", (IDictionary<string, long>)PosReference.MissedTags);
    }

    [Fact]
    public void SeedCanonical_EmitsTypePlusSeventeenValues()
    {
        var b = new Laplace.SubstrateCRUD.SubstrateChangeBuilder(
            Hash128.OfCanonical("substrate/test/pos/source"), "test/pos-seed", null,
            entityCapacity: 18, physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(b, Hash128.OfCanonical("substrate/test/pos/source"));
        var change = b.Build();
        Assert.Equal(PosReference.Canonical.Length + 1, change.Entities.Length);
        Assert.Contains(change.Entities, e => e.Id == PosReference.PosTypeId);
        Assert.Contains(change.Entities, e => e.Id == PosReference.CanonicalId("VERB"));
    }
}
