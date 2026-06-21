using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class CrossSourceLinkingTests
{
    [Fact]
    public void SenseAnchor_And_CategoryAnchor_Agree_On_Normalized_SenseKey()
    {
        const string raw = "?lend%2:40:00";
        string? norm = SourceEntityIdConventions.NormalizeSenseKey(raw);
        Assert.NotNull(norm);
        Assert.Equal(SenseAnchor.Id(raw), SenseAnchor.IdNormalized(norm!));
        Assert.Equal(CategoryAnchor.Id(norm!), SenseAnchor.Id(raw));
    }

    [Fact]
    public void NumericVerbNetClassId_Agrees_Across_Decomposer_EntryPoints()
    {
        const string vnKey = "give-13.1-1";
        Assert.Equal(
            SourceEntityIdConventions.NumericVerbNetClassId(vnKey),
            SourceEntityIdConventions.NumericVerbNetClassId(
                SourceEntityIdConventions.VerbNetClassFromSemLinkKey("13.1-1-give")));
    }

    [Fact]
    public void CategoryAnchor_Ids_Converge_Across_Roleset_VerbNet_Frame_Names()
    {
        Assert.Equal(CategoryAnchor.Id("give.01"), CategoryAnchor.Id("give.01"));
        Assert.Equal(CategoryAnchor.Id("13.1-1"), CategoryAnchor.Id("13.1-1"));
        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id("Giving"));
    }

    [Fact]
    public void ConceptAnchor_SynsetId_Requires_Cili_Map()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        CodepointPerfcache.LoadDefault();
        Hash128? iliAnchor = ConceptAnchor.SynsetId(10676319, 'n');
        Assert.NotNull(iliAnchor);
        Assert.Equal(iliAnchor, ConceptAnchor.SynsetId(10676319, 'n'));
    }

    [Fact]
    public void OMW_And_WordNet_Share_Synset_Anchor_When_Cili_Present()
    {
        string cili = Environment.GetEnvironmentVariable("LAPLACE_CILI_DIR") ?? @"D:\Data\Ingest\CILI";
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        CodepointPerfcache.LoadDefault();
        var source = Hash128.OfCanonical("substrate/source/test/omw-wn-bridge/v1");
        var b = new SubstrateChangeBuilder(source, "test", null);

        Hash128? wnId = ConceptAnchor.EmitAnchor(b, 10676319, 'n', source);
        Hash128? omwId = ConceptAnchor.EmitAnchor(b, 10676319, 'n', source);
        Assert.NotNull(wnId);
        Assert.Equal(wnId, omwId);
    }
}
