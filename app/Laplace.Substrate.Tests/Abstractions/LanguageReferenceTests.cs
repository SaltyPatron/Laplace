using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("LanguageReference")]
public class LanguageReferenceTests
{


    private static string IsoDir => TestPathHelpers.Iso639OrFallback();
    private static bool RefPresent => File.Exists(Path.Combine(IsoDir, "iso-639-3.tab"));

    private static bool Ensure()
    {
        if (!RefPresent) return false;
        LanguageReference.Load(IsoDir);
        return true;
    }

    private static Hash128 Canonical(string code) => LanguageEntityId.FromIso639_3(code);

    [Fact]
    public void English_AllForms_ConvergeToOneEntity()
    {
        if (!Ensure()) return;
        Hash128 eng = Canonical("eng");
        var mismatches = new List<string>();
        foreach (var form in new[] { "en", "eng", "English", "english", "EN",
                                     "en-US", "en-GB", "en-Latn", " en ", "anglais" })
            if (LanguageReference.Resolve(form) != eng)
                mismatches.Add($"{form} -> {LanguageReference.ResolveCode(form) ?? "<null>"}");
        Assert.True(mismatches.Count == 0, "forms not converging to eng: " + string.Join(", ", mismatches));
    }

    [Fact]
    public void DistinctLanguages_StayDistinct()
    {
        if (!Ensure()) return;
        Hash128 eng = Canonical("eng"), fra = Canonical("fra"), deu = Canonical("deu");
        Assert.NotEqual(eng, fra);
        Assert.NotEqual(eng, deu);
        Assert.Equal(fra, LanguageReference.Resolve("fr"));
        Assert.Equal(fra, LanguageReference.Resolve("fre"));
        Assert.Equal(deu, LanguageReference.Resolve("de"));
        Assert.Equal(deu, LanguageReference.Resolve("ger"));
    }

    [Fact]
    public void DeprecatedTag_RoutesToPreferred()
    {
        if (!Ensure()) return;
        Assert.Equal(Canonical("ind"), LanguageReference.Resolve("in"));
        Assert.Equal(Canonical("ind"), LanguageReference.Resolve("id"));
        Assert.Equal(Canonical("ind"), LanguageReference.Resolve("Indonesian"));
    }

    [Fact]
    public void Unresolvable_RoutesToUnd_AndCounts()
    {
        if (!Ensure()) return;
        long before = LanguageReference.ResolveMisses;
        Assert.Equal(Canonical("und"), LanguageReference.Resolve("zz-not-a-language"));
        Assert.True(LanguageReference.ResolveMisses > before, "miss must be counted, never silent");
    }

    [Fact]
    public void ReferenceIsSubstantial()
    {
        if (!Ensure()) return;
        Assert.True(LanguageReference.AliasCount > 9000,
            $"expected a rich alias map, got {LanguageReference.AliasCount}");
    }
}
