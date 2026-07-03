using System.Linq;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessProvenanceTests
{
    [Fact]
    public void PlayerId_IsStable_Distinct_AndCanonicalized()
    {
        Assert.Equal(ChessVocabulary.PlayerId("Carlsen, Magnus"), ChessVocabulary.PlayerId("Magnus Carlsen"));
        Assert.NotEqual(ChessVocabulary.PlayerId("Carlsen, Magnus"), ChessVocabulary.PlayerId("Nakamura, Hikaru"));
        Assert.Equal("magnus carlsen", PlayerAlias.Canonical("Carlsen, Magnus"));
    }

    [Fact]
    public void Laplace_IsTheSelfPlayPlayer()
        => Assert.Equal(ChessVocabulary.PlayerId("Laplace"), ChessVocabulary.LaplacePlayerId);

    [Fact]
    public void Sources_AreDistinct_PerProvenance()
    {
        var srcs = new[]
        {
            ChessVocabulary.SourceId, ChessVocabulary.PgnSourceId,
            ChessVocabulary.UserPromptSourceId, ChessVocabulary.OpeningsSourceId,
        };
        Assert.Equal(srcs.Length, srcs.Distinct().Count());
    }

    [Fact]
    public void PgnAndOpenings_PointAt_AcademicCurated()
    {
        var id = Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");
        Assert.Equal(id, ChessVocabulary.PgnTrustClass);
        Assert.Equal(id, ChessVocabulary.OpeningsTrustClass);
    }

    [Fact]
    public void SelfPlayAndUserPrompt_PointAt_SeededTrustClasses()
    {
        Assert.Equal(Hash128.OfCanonical("substrate/trust_class/ResponseContent/v1"),
            ChessVocabulary.SelfPlayTrustClass);
        Assert.Equal(Hash128.OfCanonical("substrate/trust_class/UserPromptContent/v1"),
            ChessVocabulary.UserPromptTrustClass);
    }
}
