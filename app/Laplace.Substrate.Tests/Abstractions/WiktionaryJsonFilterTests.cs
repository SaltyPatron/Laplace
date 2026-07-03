using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class WiktionaryJsonFilterTests
{
    private static string IsoDir =>
        Environment.GetEnvironmentVariable("LAPLACE_ISO639_DIR") is { Length: > 0 } d ? d
        : OperatingSystem.IsWindows() ? @"D:\Data\Ingest\ISO639" : "/vault/Data/ISO639";

    private static void EnsureLanguageReference()
    {
        if (!File.Exists(Path.Combine(IsoDir, "iso-639-3.tab")))
            throw new InvalidOperationException($"ISO639 data not found at {IsoDir}");
        LanguageReference.Load(IsoDir);
    }




    private const string RowWithNestedTranslationLangFirst = """
        {"senses":[{"glosses":["a reference work"]}],"pos":"noun","translations":[{"lang":"Abaza","lang_code":"abq","word":"x"}],"word":"dictionary","lang":"French","lang_code":"fr"}
        """;

    [Fact]
    public void MatchesLanguageFilter_UsesRowsOwnTopLevelLangCode_NotEarlierNestedTranslationLang()
    {
        EnsureLanguageReference();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(RowWithNestedTranslationLangFirst.Trim());

        var frFilter = LanguageFilter.FromSpec("fr");
        Assert.True(
            WiktionaryJsonFilter.MatchesLanguageFilter(utf8, frFilter),
            "row's true top-level lang_code is fr; a nested translations[].lang_code='abq' " +
            "appearing earlier in the byte stream must not cause it to be dropped");

        var deFilter = LanguageFilter.FromSpec("de");
        Assert.False(
            WiktionaryJsonFilter.MatchesLanguageFilter(utf8, deFilter),
            "row's true top-level lang_code is fr, not de, so a german filter must reject it");
    }

    [Fact]
    public void MatchesLanguageFilter_RejectsNestedAbazaTranslationCode_WhenFilteredForAbaza()
    {



        EnsureLanguageReference();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(RowWithNestedTranslationLangFirst.Trim());

        var abqFilter = LanguageFilter.FromSpec("abq");
        Assert.False(
            WiktionaryJsonFilter.MatchesLanguageFilter(utf8, abqFilter),
            "the row's own top-level language is fr; the nested translations[].lang_code " +
            "must not leak through and falsely match an abq filter");
    }

}
