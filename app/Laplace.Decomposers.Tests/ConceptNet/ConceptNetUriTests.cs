using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ConceptNet;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.ConceptNet.Tests;

public sealed class ConceptNetUriTests
{
    [Fact]
    public void TryParseConceptUri_Extracts_Pos_From_Suffixed_Uri()
    {
        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/music/n/wn/communication"u8, out var lang, out var term, out var pos));
        Assert.Equal("en", Encoding.UTF8.GetString(lang));
        Assert.Equal("music", Encoding.UTF8.GetString(term));
        Assert.Equal('n', pos);

        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/run/v"u8, out _, out term, out pos));
        Assert.Equal("run", Encoding.UTF8.GetString(term));
        Assert.Equal('v', pos);

        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/dog"u8, out _, out term, out pos));
        Assert.Equal("dog", Encoding.UTF8.GetString(term));
        Assert.Null(pos);
    }

    [Fact]
    public void TryParseConceptUri_Extracts_Wn_Suffix_When_Present()
    {
        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/give/v/wn/30-02244956-v"u8, out _, out var term, out var pos, out var wn));
        Assert.Equal("give", Encoding.UTF8.GetString(term));
        Assert.Equal('v', pos);
        Assert.Equal("30-02244956-v", Encoding.UTF8.GetString(wn));

        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/awake/a/wn"u8, out _, out _, out _, out wn));
        Assert.True(wn.IsEmpty);

        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/cat/n/wn/animal"u8, out _, out _, out _, out wn));
        Assert.Equal("animal", Encoding.UTF8.GetString(wn));
    }

    [Fact]
    public void ResolveSynsetFromWnSuffix_Parses_Mcr_Key_To_Ili_Anchor()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        CodepointPerfcache.LoadDefault();
        Hash128? fromSuffix = ConceptNetUri.ResolveSynsetFromWnSuffix("30-02244956-v"u8);
        Assert.NotNull(fromSuffix);
        Assert.Equal(ConceptAnchor.SynsetId(2244956, 'v'), fromSuffix);
    }

    [Fact]
    public void ResolveSynsetFromWnSuffix_Resolves_Coarse_Topic_Labels_With_Pos()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        CodepointPerfcache.LoadDefault();
        Assert.True(ConceptNetUri.TryParseConceptUri(
            "/c/en/music/n/wn/communication"u8, out _, out _, out var pos, out var wn));
        Assert.Equal('n', pos);
        Hash128? fromUri = ConceptNetUri.ResolveSynsetFromWnSuffix(wn, pos);
        Assert.NotNull(fromUri);
        Assert.Equal(ConceptAnchor.SynsetId(6252138, 'n'), fromUri);

        Hash128? communication = ConceptNetUri.ResolveSynsetFromWnSuffix("communication"u8, 'n');
        Assert.NotNull(communication);
        Assert.Equal(ConceptAnchor.SynsetId(6252138, 'n'), communication);

        Hash128? animal = ConceptNetUri.ResolveSynsetFromWnSuffix("animal"u8, 'n');
        Assert.NotNull(animal);
        Assert.Equal(ConceptAnchor.SynsetId(1313093, 'n'), animal);
    }

    [Fact]
    public void ResolveSynsetFromWnSuffix_Ignores_Unknown_Topic_Labels()
    {
        Assert.Null(ConceptNetUri.ResolveSynsetFromWnSuffix("not-a-lex-topic"u8, 'n'));
    }

    [Fact]
    public void ResolveSynsetFromExternalUrl_Parses_WnRdf_Tail_To_Ili_Anchor()
    {
        string cili = TestInstall.ResolveCiliOrFallback();
        if (!TestInstall.HasFullCiliMap(cili)) return;

        CodepointPerfcache.LoadDefault();
        Hash128? synId = ConceptNetUri.ResolveSynsetFromExternalUrl(
            "http://wordnet-rdf.princeton.edu/wn31/02244956-v"u8);
        Assert.NotNull(synId);
        Assert.Equal(ConceptAnchor.SynsetId(2244956, 'v'), synId);
    }

    [Fact]
    public void IsExternalUrlRelation_Matches_ConceptNet_Relation_Uri()
    {
        Assert.True(ConceptNetUri.IsExternalUrlRelation("/r/ExternalURL"u8));
        Assert.False(ConceptNetUri.IsExternalUrlRelation("/r/RelatedTo"u8));
    }
}
