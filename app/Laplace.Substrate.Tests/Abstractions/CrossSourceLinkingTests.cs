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
    public void CategoryAnchor_Trims_StraySurfaceWhitespace_FromIndependentSources()
    {





        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id("Giving "));
        Assert.Equal(CategoryAnchor.Id("Giving"), CategoryAnchor.Id(" Giving"));
        Assert.Equal(CategoryAnchor.Id("13.1-1"), CategoryAnchor.Id(" 13.1-1 "));
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

    [Fact]
    public void ConceptAnchor_SynsetId_Requires_Cili_Map()
    {
        string cili = TestPathHelpers.CiliOrFallback();
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName))) return;

        CodepointPerfcache.LoadDefault();
        Hash128? iliAnchor = ConceptAnchor.SynsetId(10676319, 'n');
        Assert.NotNull(iliAnchor);
    }

    [Fact]
    public void OMWRowParser_And_WordNetDataLine_Extract_The_Same_Offset_From_Their_Native_Formats()
    {





        const string wordNetNativeLine = "10676319 05 n 02 dog 0 domestic_dog 0 001 @ 10675588 n 0000 | a member of the genus Canis";
        var wnParts = wordNetNativeLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(long.TryParse(wnParts[0], out long wnOffset));
        char wnSsType = wnParts[2][0];

        byte[] omwLine = System.Text.Encoding.UTF8.GetBytes("10676319-n\teng:lemma\tdog");
        Assert.True(Laplace.Decomposers.OMW.OMWRowParser.TryParseRow(omwLine, "eng", out var omwRow, out _));

        Assert.Equal(wnOffset, omwRow.Offset);
        Assert.Equal(wnSsType, omwRow.SsType);
        Assert.Equal(ConceptAnchor.SynsetId(wnOffset, wnSsType), ConceptAnchor.SynsetId(omwRow.Offset, omwRow.SsType));
    }
}
