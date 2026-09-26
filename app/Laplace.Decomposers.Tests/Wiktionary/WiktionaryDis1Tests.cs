using System.Text;
using System.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Xunit;

namespace Laplace.Decomposers.Wiktionary.Tests;

/// <summary>
/// wiktextract ships a per-sense association weight, <c>_dis1</c>, on members of the
/// translations, synonyms, hypernyms, hyponyms, meronyms, derived, related,
/// coordinate_terms and holonyms blocks. It is read per member so senses of one lemma
/// fold with their own weights.
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

        // The corpus writes _dis1 both as a number and as a string; both are read.
        Assert.Equal(0.9, syn.Single(m => m.Word == "depository").Dis1, 6);
        Assert.Equal(0.25, syn.Single(m => m.Word == "vault").Dis1, 6);

        // A member with no _dis1 stays 0: "no association computed" is not a weight of 1.0.
        Assert.Equal(0.0, syn.Single(m => m.Word == "unscored").Dis1);

        Assert.Equal(0.75, Assert.Single(e.Translations!).Dis1, 6);
    }
}
