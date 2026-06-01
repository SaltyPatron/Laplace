using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// LanguageReference — the omni-glottal resolution index. Proves the convergence
/// invariant: any ISO code form / BCP-47 tag / reference name for a language
/// resolves to the ONE canonical 639-3 entity, so independent sources co-assert
/// instead of forking into language:en vs language:eng vs language:english. Built
/// from the live attested reference at /vault/Data/ISO639 (the same files the
/// ISODecomposer seeds). Skips cleanly if that reference is not mounted.
/// </summary>
public class LanguageReferenceTests
{
    private const string IsoDir = "/vault/Data/ISO639";
    private static bool RefPresent => File.Exists(Path.Combine(IsoDir, "iso-639-3.tab"));

    // Returns false (graceful no-op) when the reference isn't mounted; on this
    // machine it is, so the asserts run live. No SkippableFact package in the tree.
    private static bool Ensure()
    {
        if (!RefPresent) return false;
        LanguageReference.Load(IsoDir);   // deterministic full (re)build
        return true;
    }

    private static Hash128 Canonical(string code) => LanguageEntityId.FromIso639_3(code);

    [Fact]
    public void English_AllForms_ConvergeToOneEntity()
    {
        if (!Ensure()) return;
        Hash128 eng = Canonical("eng");
        // 639-1, 639-3, 639-2/B+T, English name, French name, BCP-47 region/script tags,
        // case/whitespace variants — every witness for English lands on the same entity.
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
        // and each of their own code forms converge
        Assert.Equal(fra, LanguageReference.Resolve("fr"));
        Assert.Equal(fra, LanguageReference.Resolve("fre"));   // 639-2/B
        Assert.Equal(deu, LanguageReference.Resolve("de"));
        Assert.Equal(deu, LanguageReference.Resolve("ger"));   // 639-2/B
    }

    [Fact]
    public void DeprecatedTag_RoutesToPreferred()
    {
        if (!Ensure()) return;
        // IANA Preferred-Value: "in" (deprecated) -> "id" -> 639-3 "ind" (Indonesian).
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
        // ~7900 languages, each with multiple aliases -> tens of thousands of keys.
        Assert.True(LanguageReference.AliasCount > 9000,
            $"expected a rich alias map, got {LanguageReference.AliasCount}");
    }
}
