using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// Rule sets are content, detected from the evidence of a game rather than declared from a
/// list. The first group pins that standard chess is identity-neutral: its positions keep the
/// same ids whether or not variant rules are known.
/// </summary>
public class ChessVariantTests
{
    private const string Startpos = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // ---- identity ---------------------------------------------------------------------

    [Fact]
    public void StandardRules_AddNothingToTheSurface()
    {
        Assert.True(ChessVariantRules.Standard.IsStandard);
        Assert.Equal("", ChessVariantRules.Standard.Surface());

        var b = Board.FromFen(Startpos);
        Assert.Equal(PositionContent.Surface(b, "-"),
                     PositionContent.Surface(b, "-", ChessVariantRules.Standard));
    }

    /// <summary>Chess960 is standard rules with a different starting array, so it resolves to
    /// the standard rule set and its positions can share ids with standard ones.</summary>
    [Fact]
    public void Chess960_IsStandardRules_NotAVariantRuleSet()
    {
        Assert.True(ChessVariants.Chess960.IsStandard);
        Assert.True(ChessVariants.ByName("Chess960")!.IsStandard);
        Assert.True(ChessVariants.ByName("Freestyle")!.IsStandard);
        Assert.True(ChessVariants.ByName("dfrc")!.IsStandard);
    }

    /// <summary>A rule variant changes identity: same placement and side to move, different
    /// futures, different id.</summary>
    [Fact]
    public void RuleVariant_ProducesADifferentPositionThanStandard()
    {
        var b = Board.FromFen(Startpos);
        string std = PositionContent.Surface(b, "-", ChessVariantRules.Standard);
        string koth = PositionContent.Surface(b, "-", ChessVariants.KingOfTheHill);

        Assert.NotEqual(std, koth);
        Assert.StartsWith("rules:winsq:d4,d5,e4,e5 ", koth);
        Assert.DoesNotContain("rules:", std);
    }

    /// <summary>Distinct rule sets are distinct surfaces; no two variants collide.</summary>
    [Fact]
    public void EveryConventionalRuleSet_HasItsOwnSurface()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, rules) in ChessVariants.Conventional)
        {
            string surface = rules.Surface();
            if (seen.TryGetValue(surface, out var prior))
                Assert.Fail($"{name} and {prior} share the rule surface '{surface}'");
            seen[surface] = name;
        }
    }

    // ---- detection --------------------------------------------------------------------

    // ---- unseen rule sets --------------------------------------------------------------

}
