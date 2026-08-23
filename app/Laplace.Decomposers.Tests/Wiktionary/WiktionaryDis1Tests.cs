using System.Text;
using System.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Xunit;

namespace Laplace.Decomposers.Wiktionary.Tests;

/// <summary>
/// wiktextract computes a per-sense association weight, `_dis1`, and ships it on every
/// member of nine of the twelve relation blocks — measured on
/// kaikki.org-dictionary-English.jsonl: 100% of members in translations, synonyms,
/// hypernyms, hyponyms, meronyms, derived, related, coordinate_terms and holonyms.
///
/// ReadWordArray read only `word`, so every Wiktionary edge folded at the categorical
/// constant and all senses of a lemma folded identically — 38.5% of entries have two or
/// more senses. Wiktionary had 3 emit sites and none of them scored.
/// </summary>
public sealed class WiktionaryDis1Tests
{
    private const string Entry = """
{"word":"bank","pos":"noun","lang_code":"en","senses":[{"glosses":["a financial institution"]}],
 "synonyms":[{"word":"depository","_dis1":"0.9"},{"word":"vault","_dis1":0.25},{"word":"unscored"}],
 "translations":[{"word":"banque","_dis1":0.75,"code":"fr"}]}
""";

    [Fact]
    public void MemberWeightIsParsed_AndZeroMeansTheSourceGaveNone()
    {
        var e = WiktionaryEntry.Parse(Encoding.UTF8.GetBytes(Entry),
            DecomposerOptions.Default with { EmitCrossLanguageLinks = true });
        Assert.NotNull(e);

        var syn = e!.Top.Synonyms;
        Assert.NotNull(syn);
        Assert.Equal(3, syn!.Count);

        // Numeric and string forms both carry through: the corpus uses both.
        Assert.Equal(0.9, syn.Single(m => m.Word == "depository").Dis1, 6);
        Assert.Equal(0.25, syn.Single(m => m.Word == "vault").Dis1, 6);

        // A member with no _dis1 must stay 0 — "the source computed no association" is
        // not the same claim as 1.0, and promoting it would manufacture evidence.
        Assert.Equal(0.0, syn.Single(m => m.Word == "unscored").Dis1);

        Assert.Equal(0.75, Assert.Single(e.Translations!).Dis1, 6);
    }
}
