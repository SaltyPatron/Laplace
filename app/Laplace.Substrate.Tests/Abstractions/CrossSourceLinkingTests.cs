using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class CrossSourceLinkingTests
{
    [Fact]
    public void SenseAnchor_ConvergesAcrossNormalizedSourceKeys_AsCanonicalContent()
    {
        const string raw = "?lend%2:40:00";
        string? norm = SourceEntityIdConventions.NormalizeSenseKey(raw);
        Assert.NotNull(norm);
        Assert.Null(SenseAnchor.Id(raw));
        Assert.Null(SenseAnchor.IdNormalized(norm!));
        Assert.Null(ReferenceAnchor.Id(ReferenceIdentityKind.WordNetSenseKey, norm!));
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
    public void CategoryAnchor_Trims_StraySurfaceWhitespace_FromIndependentSources()
    {





        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id("Giving "));
        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id(" Giving"));
        Assert.NotEqual(CategoryAnchor.Id("Giving"), CategoryAnchor.Id("giving"));
    }

    [Fact]
    public void Real_FrameNetFrameName_Matches_Real_PredicateMatrixAndMapNet_Surface()
    {




        string fromFrameNetXml = "Change_position_on_a_scale";
        string fromPredicateMatrixColumn =
            SourceEntityIdConventions.StripPredicateMatrixNamespace("fn:Change_position_on_a_scale");
        Assert.Equal(CategoryAnchor.Id(fromFrameNetXml), CategoryAnchor.Id(fromPredicateMatrixColumn));

        string fromMapNetField = "Giving".Trim();
        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id(fromMapNetField));
    }

    [SkippableFact]
    public void ConceptAnchor_SynsetId_Requires_Cili_Map()
    {
        string cili = TestPathHelpers.CiliOrFallback();
        Skip.IfNot(File.Exists(Path.Combine(cili, IliMap.MapFileName)), "CILI map not present");

        CodepointPerfcache.LoadDefault();
        Hash128? iliAnchor = ConceptAnchor.SynsetId(10676319, 'n', "pwn30");
        Assert.NotNull(iliAnchor);
    }

}
