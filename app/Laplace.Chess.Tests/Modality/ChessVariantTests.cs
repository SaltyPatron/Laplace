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

    /// <summary>
    /// An ordinary game exercises no distinguishing rule, so it stays consistent with several
    /// rule sets and no single one is resolved.
    /// </summary>
    [Fact]
    public void OrdinaryGame_NarrowsToSeveral_AndResolvesToNone()
    {
        var e = new ChessVariantEvidence { CastlingObserved = true, PiecesSeen = "KQRBNP" };
        Assert.Contains(e.Candidates(), c => c.Name == "Standard");
        Assert.Contains(e.Candidates(), c => c.Name == "KingOfTheHill");  // never exercised
        Assert.Null(e.Resolved());                                        // so: no verdict
    }

    /// <summary>Evidence eliminates: castling was played, so rule sets without castling are
    /// excluded whatever the tag says.</summary>
    [Fact]
    public void PlayedCastle_EliminatesCastlelessRuleSets()
    {
        var e = new ChessVariantEvidence { CastlingObserved = true };
        Assert.DoesNotContain(e.Candidates(), c => c.Name == "Antichess");
        Assert.DoesNotContain(e.Candidates(), c => c.Name == "RacingKings");
    }

    [Fact]
    public void MaterialFromNowhere_ResolvesToCrazyhouse()
    {
        var e = new ChessVariantEvidence { MaterialAppeared = true, CastlingObserved = true };
        Assert.Equal("Crazyhouse", Assert.Single(e.Candidates()).Name);
        Assert.True(e.Resolved()!.Drops);
    }

    [Fact]
    public void CollateralCapture_ResolvesToAtomic()
    {
        var e = new ChessVariantEvidence { CollateralCapture = true, CastlingObserved = true };
        Assert.Equal("Atomic", Assert.Single(e.Candidates()).Name);
    }

    /// <summary>The moves outrank the tag: a Standard tag on a game where material appears
    /// from nowhere does not keep Standard as a candidate.</summary>
    [Fact]
    public void MovesOutrankTheClaimedTag()
    {
        var e = new ChessVariantEvidence
        {
            ClaimedVariant = "Standard", MaterialAppeared = true, CastlingObserved = true,
        };
        Assert.Equal("Crazyhouse", Assert.Single(e.Candidates()).Name);
    }

    /// <summary>Among rule sets the moves leave standing, the tag ranks first: the source's
    /// statement is testimony where nothing contradicts it.</summary>
    [Fact]
    public void ClaimedTag_RanksAmongSurvivors()
    {
        var e = new ChessVariantEvidence { ClaimedVariant = "King of the Hill", CastlingObserved = true };
        Assert.Equal("KingOfTheHill", e.Candidates()[0].Name);
    }

    // ---- unseen rule sets --------------------------------------------------------------

    /// <summary>
    /// A rule set with no pre-seeded candidate is not an error: the evidence composes its own
    /// rule surface, content-addressed like any other. A 10x8 board with an Archbishop
    /// matches no listed variant and is still a fully identified rule set.
    /// </summary>
    [Fact]
    public void UnknownRuleSet_MintsItsOwnIdentity_RatherThanFailing()
    {
        var e = new ChessVariantEvidence { Files = 12, Ranks = 8, PiecesSeen = "KQRBNPZ" };

        Assert.Empty(e.Candidates());          // no listed variant fits
        Assert.Null(e.Resolved());

        var observed = e.Observed();           // the observed rule set is still identified
        Assert.False(observed.IsStandard);
        Assert.Contains("dim:12x8", observed.Surface());
        Assert.Contains("pc:KQRBNPZ", observed.Surface());

        // It enters position identity like any other rule set.
        var b = Board.FromFen(Startpos);
        Assert.NotEqual(PositionContent.Surface(b, "-", ChessVariantRules.Standard),
                        PositionContent.Surface(b, "-", observed));
    }

    /// <summary>Two sources describing the same rules reach the same rule-set id, with no
    /// registration.</summary>
    [Fact]
    public void SameRulesFromTwoSources_Collide()
    {
        var a = new ChessVariantEvidence { CollateralCapture = true }.Observed();
        var b = ChessVariants.Atomic;
        Assert.Equal(a.Surface(), b.Surface());
    }
}
