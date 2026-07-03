using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class LanguageFilterTests
{
    [Fact]
    public void En_Resolves_Eng_And_En_Us()
    {
        var f = LanguageFilter.FromSpec("en");
        Assert.True(f.MatchesRaw("en"));
        Assert.True(f.MatchesRaw("eng"));
        Assert.True(f.MatchesRaw("en-US"));
        Assert.False(f.MatchesRaw("de"));
    }

    [Fact]
    public void Ud_Treebank_Matches_English_Dialects()
    {
        var f = LanguageFilter.FromSpec("en");
        Assert.True(f.MatchesUdTreebankFile("en_ewt-ud-train.conllu"));
        Assert.True(f.MatchesUdTreebankFile("en_gum-ud-dev.conllu"));
        Assert.False(f.MatchesUdTreebankFile("de_gsd-ud-train.conllu"));
    }

    [Fact]
    public void Pair_Matches_When_Either_Side_In_Set()
    {
        var f = LanguageFilter.FromSpec("en");
        Assert.True(f.MatchesLanguagePair("en-es"));
        Assert.True(f.MatchesLanguagePair("de-en"));
        Assert.False(f.MatchesLanguagePair("de-fr"));
    }

    [Fact]
    public void MultiLanguage_Spec_By_FullName_ResolvesAll()
    {
        var f = LanguageFilter.FromSpec("English, Japanese, Mandarin Chinese");
        Assert.True(f.MatchesRaw("eng"));
        Assert.True(f.MatchesRaw("jpn"));
        Assert.True(f.MatchesRaw("cmn"));
        Assert.False(f.MatchesRaw("de"));
    }

    [Fact]
    public void UnresolvableToken_In_MultiLanguage_Spec_ThrowsNamingIt()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LanguageFilter.FromSpec("English, Mandarin"));
        Assert.Contains("Mandarin", ex.Message);
    }
}
