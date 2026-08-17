using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ISO;
using Laplace.Decomposers.WordNet;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests;

/// <summary>
/// A deposited fact must be able to be false. These three writes emitted claims that could
/// not: an edge whose object IS its subject, and one gloss cut into fragments that then
/// competed with the whole for the same rating.
///
/// Each test names the substrate rows the defect produced, measured 2026-08-16, so a
/// regression is recognisable as the same thing rather than as a new mystery.
/// </summary>
public sealed class VacuousFactGateTests
{
    // ---- WordNet: one synset, one gloss -------------------------------------------------

    /// <summary>
    /// laplace.consensus held synset f59f0970 (cat) with THREE HAS_DEFINITION facts:
    /// the whole gloss (deposited by OMW/CILI, which do not split), plus
    /// "...domestic cats" and "wildcats" from splitting this one on ';'. The whole and
    /// the fragment "wildcats" both landed at eff_mu 1319.9, so which one a read returned
    /// as the top definition was a tie-break rather than a rating.
    /// </summary>
    [Fact]
    public void WordNetGloss_WithSemicolon_StaysOneDefinition()
    {
        var (defs, examples) = WordNetDecomposer.ParseGloss(
            "feline mammal usually having thick soft fur and no ability to roar: "
            + "domestic cats; wildcats");

        Assert.Single(defs);
        Assert.Equal(
            "feline mammal usually having thick soft fur and no ability to roar: "
            + "domestic cats; wildcats",
            defs[0]);
        Assert.Empty(examples);
    }

    /// <summary>Quoted examples are still lifted out, and the ';' that separated them
    /// from the definition does not survive as a trailing fragment.</summary>
    [Fact]
    public void WordNetGloss_LiftsExamples_AndLeavesNoTrailingSeparator()
    {
        var (defs, examples) = WordNetDecomposer.ParseGloss(
            "a hard sweet made from sugar; \"she bought a bag of sweets\"; \"boiled sweets\"");

        Assert.Single(defs);
        Assert.Equal("a hard sweet made from sugar", defs[0]);
        Assert.Equal(2, examples.Count);
        Assert.Contains("she bought a bag of sweets", examples);
        Assert.Contains("boiled sweets", examples);
    }

    [Fact]
    public void WordNetGloss_Empty_YieldsNoDefinition()
    {
        Assert.Empty(WordNetDecomposer.ParseGloss("").Defs);
        Assert.Empty(WordNetDecomposer.ParseGloss("  ;  ").Defs);
    }

    // ---- ISO 639-3: a name is not a definition ------------------------------------------

    /// <summary>
    /// ISO 639-3 publishes codes and names, no glosses. Both emit sites attested the
    /// language's own name as HAS_DEFINITION, producing rows that render
    /// "Batui HAS_DEFINITION Batui" — 8,336 of them, 19% of that source's 42,931 rows.
    /// The relation is gone from the source's declared vocabulary, which is what this
    /// asserts: a relation nothing emits must not be declared.
    /// </summary>
    /// <summary>
    /// Read from source, not from the loaded type: touching ISOSource.Relations runs a static
    /// initializer that P/Invokes laplace_core, which is not beside the test binary — so a
    /// reflection-based assertion fails on the native load rather than on the declaration it
    /// is meant to check. The declaration is the artifact under test.
    /// </summary>
    [Fact]
    public void IsoSource_NoLongerDeclares_HasDefinition()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "app")))
            root = Directory.GetParent(root)?.FullName;
        Assert.NotNull(root);

        var src = File.ReadAllText(Path.Combine(
            root!, "app", "Laplace.Decomposers", "ISO", "ISOSource.cs"));
        var relations = src[(src.IndexOf("Relations", StringComparison.Ordinal))..];
        relations = relations[..relations.IndexOf("];", StringComparison.Ordinal)];

        Assert.DoesNotContain("\"HAS_DEFINITION\"", relations);
        Assert.Contains("\"HAS_NAME_ALIAS\"", relations);

        var dec = File.ReadAllText(Path.Combine(
            root!, "app", "Laplace.Decomposers", "ISO", "ISODecomposer.cs"));
        Assert.DoesNotContain("\"HAS_DEFINITION\"", dec);
    }
}
