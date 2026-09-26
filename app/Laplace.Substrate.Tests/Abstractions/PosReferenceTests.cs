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
    public void UnknownTag_GoesProbationary_NeverSilent_NeverThrows()
    {
        var id = PosReference.Resolve("proverb", PosReference.PosTagset.Wiktionary, out bool probationary);

        // An unmapped tag stays the source's own value: its exact content, never a
        // namespaced key.
        Assert.True(probationary);
        Assert.Equal(Laplace.Decomposers.Abstractions.ContentTierSpine.ResolveRoot("proverb"), id);
        Assert.DoesNotContain(PosReference.Canonical,
            t => PosReference.CanonicalId(t) == id);
    }

    [Fact]
    public void SeedCanonical_EmitsSeventeenContentValues()
    {
        var b = new Laplace.SubstrateCRUD.SubstrateChangeBuilder(
            SubstrateCanonicalIds.Of("test", "pos", "source"), "test/pos-seed", null,
            entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
        PosReference.SeedCanonical(b, SubstrateCanonicalIds.Of("test", "pos", "source"));

        // Each UPOS value is content ("VERB" is the text), staged through the content
        // spine; no POS type row is seeded among the managed entity rows.
        // A one-codepoint value ("X") is its Tier-0 atom and stages no row of its own.
        Assert.Equal(PosReference.Canonical.Count(t => t.Length > 1), b.ContentStage.EntityCount);
        var change = b.Build();
        Assert.DoesNotContain(change.Entities, e => e.Id == PosReference.PosTypeId);
    }
}
