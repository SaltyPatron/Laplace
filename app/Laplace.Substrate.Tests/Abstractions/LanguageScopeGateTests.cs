using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// A source that DECLARES a language scope must EMIT it.
///
/// The defect this exists to prevent, measured 2026-08-05: nine monolingual English
/// sources — WordNet, FrameNet, PropBank, VerbNet, SemLink, WordFrameNet,
/// PredicateMatrix among them — deposited no HAS_LANGUAGE edge at all. WordNet is the
/// one that mattered: it supplies senses(), so every English sense in the substrate read
/// back as language-UNATTESTED, and word_language() existed to infer at read time a fact
/// the source states for free.
///
/// Nothing caught it. A missing attestation is invisible by construction — there is no
/// row to be wrong, and an unattested id is not an id attested false, so no read can tell
/// "this source is silent about language" from "this word has no language." That is
/// exactly why it needs a static gate rather than a runtime check.
///
/// SHRINK-ONLY. Sources that declare a scope but do not yet emit are listed below with
/// their issue. The list may lose entries; a gain fails the build.
/// </summary>
public sealed class LanguageScopeGateTests
{
    /// <summary>
    /// Declared-but-not-yet-emitting. Each entry is unfinished work, not an exemption.
    /// Removing one means that decomposer now emits HAS_LANGUAGE for its lexical surfaces.
    /// </summary>
    private static readonly HashSet<string> NotYetEmitting = new(StringComparer.Ordinal)
    {
        "FrameNetDecomposer",
        "PropBankDecomposer",
        "VerbNetDecomposer",
        "SemLinkDecomposer",
        "WordFrameNetDecomposer",
        "PredicateMatrixDecomposer",
    };

    private static string RepoRoot() => TypeIdLawTests.FindRepoRootPublic();

    private static IEnumerable<EtlSource> ScopedSources() =>
        EtlManifest.Names
            .Select(n => EtlManifest.Get(n))
            .Where(s => s.LanguageScope is not null)
            .DistinctBy(s => s.Name);

    [Fact]
    public void ScopeIsDeclaredForAtLeastTheKnownMonolingualSources()
    {
        var scoped = ScopedSources().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // WordNet is the load-bearing one: it is what senses() reads.
        Assert.Contains("WordNetDecomposer", scoped);

        // Guard against the regex/manifest silently matching nothing, which would turn
        // every assertion below into a green test measuring an empty set.
        Assert.True(scoped.Count >= 7,
            $"expected at least 7 language-scoped sources, found {scoped.Count}");
    }

    [Fact]
    public void LanguageNeutralSourcesAreNotScoped()
    {
        var scoped = ScopedSources().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // An ILI concept is shared across every wordnet and a MapNet row is a mapping
        // between resources. Neither asserts a language, and stamping one English would
        // attest something the source does not say.
        Assert.DoesNotContain("CILIDecomposer", scoped);
        Assert.DoesNotContain("MapNetDecomposer", scoped);

        // Multilingual sources attest language per row; a blanket scope would overwrite a
        // per-row truth with a source-wide guess.
        foreach (var multilingual in new[]
                 {
                     "OMWDecomposer", "TatoebaDecomposer", "UDDecomposer",
                     "OpenSubtitlesDecomposer", "WiktionaryDecomposer",
                 })
            Assert.DoesNotContain(multilingual, scoped);
    }

    [Fact]
    public void ScopedSourceDeclaresHasLanguageOrIsGrandfathered()
    {
        var newcomers = new List<string>();

        foreach (var src in ScopedSources())
        {
            if (NotYetEmitting.Contains(src.Name)) continue;
            if (!EmitsHasLanguage(src.Name))
                newcomers.Add(src.Name);
        }

        Assert.True(newcomers.Count == 0,
            "source declares LanguageScope but never emits HAS_LANGUAGE: "
            + string.Join(", ", newcomers)
            + ". Declaring a scope without emitting leaves the substrate silent about a "
            + "fact the source asserts — the exact hole this gate exists to close. Emit "
            + "it on the source's lexical surfaces, or drop the scope.");
    }

    [Fact]
    public void GrandfatheredListShrinksOnly()
    {
        var stale = NotYetEmitting
            .Where(EmitsHasLanguage)
            .ToList();

        Assert.True(stale.Count == 0,
            "these now emit HAS_LANGUAGE and must be removed from NotYetEmitting: "
            + string.Join(", ", stale));

        var unknown = NotYetEmitting
            .Where(n => !ScopedSources().Any(s => s.Name == n))
            .ToList();

        Assert.True(unknown.Count == 0,
            "NotYetEmitting names a source that declares no scope: "
            + string.Join(", ", unknown));
    }

    /// <summary>
    /// True when the decomposer emits a HAS_LANGUAGE attestation. Comment-stripped: the
    /// header comments added alongside this work name the relation while emitting nothing,
    /// and counting those would make the gate pass on prose.
    /// </summary>
    private static bool EmitsHasLanguage(string decomposerName)
    {
        string dir = Path.Combine(RepoRoot(), "app", "Laplace.Decomposers");
        if (!Directory.Exists(dir)) return false;

        string stem = decomposerName.Replace("Decomposer", string.Empty, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            // Restrict to the source's own folder/files so one source's emit cannot
            // satisfy another's obligation.
            if (!file.Contains(Path.DirectorySeparatorChar + stem + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                continue;

            string text = StripComments(File.ReadAllText(file));
            // Either spelling counts: the quoted literal, or the shared constant.
            // G3 counts governed relation names literal-by-literal in C#, so the
            // right way to emit from a new site is EtlSource.LanguageScopeRelation
            // rather than a fresh literal — and a gate that only recognised the
            // literal would punish exactly that. It did, on this very change.
            if (Regex.IsMatch(text, @"""" + EtlSource.LanguageScopeRelation + @"""")) return true;
            if (text.Contains(nameof(EtlSource.LanguageScopeRelation), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\n]*", string.Empty);
        return src;
    }
}
