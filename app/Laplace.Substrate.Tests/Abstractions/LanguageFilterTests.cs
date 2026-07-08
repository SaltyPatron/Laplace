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

    // ForSource is the mechanism witness-manifest.json documents as law:
    // LAPLACE_INGEST_LANGS scopes every decomposer; unset = ALL languages.
    // It was once gutted to `=> null`, which turned every scoped CI ingest
    // into a silent whole-corpus run. These tests make that regression loud.

    [Fact]
    public void ForSource_NoEnv_ReturnsNull_MeaningAllLanguages()
    {
        using var _ = new EnvScope(("LAPLACE_INGEST_LANGS", null), ("LAPLACE_UD_LANGS", null));
        Assert.Null(LanguageFilter.ForSource("UDDecomposer"));
    }

    [Fact]
    public void ForSource_ReadsGlobalEnv()
    {
        using var _ = new EnvScope(("LAPLACE_INGEST_LANGS", "en"), ("LAPLACE_UD_LANGS", null));
        var f = LanguageFilter.ForSource("UDDecomposer");
        Assert.NotNull(f);
        Assert.True(f!.MatchesRaw("en"));
        Assert.False(f.MatchesRaw("de"));
    }

    [Fact]
    public void ForSource_PerSourceEnv_WinsOverGlobal()
    {
        using var _ = new EnvScope(("LAPLACE_INGEST_LANGS", "en"), ("LAPLACE_UD_LANGS", "de"));
        var f = LanguageFilter.ForSource("UDDecomposer");
        Assert.NotNull(f);
        Assert.True(f!.MatchesRaw("de"));
        Assert.False(f.MatchesRaw("en"));
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly (string Key, string? Prior)[] _saved;

        public EnvScope(params (string Key, string? Value)[] vars)
        {
            _saved = [.. vars.Select(v => (v.Key, Environment.GetEnvironmentVariable(v.Key)))];
            foreach (var (key, value) in vars)
                Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, prior) in _saved)
                Environment.SetEnvironmentVariable(key, prior);
        }
    }
}
