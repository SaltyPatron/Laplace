using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// The generic chess modality: rule sets as content, detected from evidence rather than
/// declared from a list.
///
/// The first group is the safety property everything else depends on — standard chess must
/// be identity-neutral, or adding variant support silently reseeds a 214 GB substrate.
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

    /// <summary>Chess960 is STANDARD RULES with a different array — so it must resolve to
    /// the standard rule set, and its positions must be able to collide with standard ones.
    /// This is the surprising-but-correct answer the whole taxonomy turns on.</summary>
    [Fact]
    public void Chess960_IsStandardRules_NotAVariantRuleSet()
    {
        Assert.True(ChessVariants.Chess960.IsStandard);
        Assert.True(ChessVariants.ByName("Chess960")!.IsStandard);
        Assert.True(ChessVariants.ByName("Freestyle")!.IsStandard);
        Assert.True(ChessVariants.ByName("dfrc")!.IsStandard);
    }

    /// <summary>A rule variant DOES move identity — that is the point. Same placement, same
    /// side to move, different futures, different id.</summary>
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

    /// <summary>Distinct rule sets are distinct surfaces — no two variants collide.</summary>
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
    /// An ordinary game exercises no distinguishing rule, so it is consistent with several
    /// rule sets and the honest answer is "several" — not a guess.
    /// </summary>
    [Fact]
    public void OrdinaryGame_NarrowsToSeveral_AndResolvesToNone()
    {
        var e = new ChessVariantEvidence { CastlingObserved = true, PiecesSeen = "KQRBNP" };
        Assert.Contains(e.Candidates(), c => c.Name == "Standard");
        Assert.Contains(e.Candidates(), c => c.Name == "KingOfTheHill");  // never exercised
        Assert.Null(e.Resolved());                                        // so: no verdict
    }

    /// <summary>Evidence eliminates. Castling was played, so rule sets without castling are
    /// out — however a tag might be spelled.</summary>
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

    /// <summary>The moves outrank the tag. A source claiming Standard while material appears
    /// from nowhere is a source that is wrong.</summary>
    [Fact]
    public void MovesOutrankTheClaimedTag()
    {
        var e = new ChessVariantEvidence
        {
            ClaimedVariant = "Standard", MaterialAppeared = true, CastlingObserved = true,
        };
        Assert.Equal("Crazyhouse", Assert.Single(e.Candidates()).Name);
    }

    /// <summary>Among survivors the tag ranks, because the source's statement is evidence
    /// where nothing has contradicted it.</summary>
    [Fact]
    public void ClaimedTag_RanksAmongSurvivors()
    {
        var e = new ChessVariantEvidence { ClaimedVariant = "King of the Hill", CastlingObserved = true };
        Assert.Equal("KingOfTheHill", e.Candidates()[0].Name);
    }

    // ---- the hidden feature -----------------------------------------------------------

    /// <summary>
    /// A rule set nobody pre-seeded is NOT an error. The evidence mints its own rule surface
    /// and the substrate has learned a variant by being shown one — the same way it learns a
    /// word. A 10x8 board with an Archbishop matches nothing in the conventional list and is
    /// still a perfectly well-identified rule set.
    /// </summary>
    [Fact]
    public void UnknownRuleSet_MintsItsOwnIdentity_RatherThanFailing()
    {
        var e = new ChessVariantEvidence { Files = 12, Ranks = 8, PiecesSeen = "KQRBNPZ" };

        Assert.Empty(e.Candidates());          // nothing pre-seeded fits...
        Assert.Null(e.Resolved());

        var observed = e.Observed();           // ...and it is still fully identified
        Assert.False(observed.IsStandard);
        Assert.Contains("dim:12x8", observed.Surface());
        Assert.Contains("pc:KQRBNPZ", observed.Surface());

        // And it is a position-identity-bearing rule set like any other.
        var b = Board.FromFen(Startpos);
        Assert.NotEqual(PositionContent.Surface(b, "-", ChessVariantRules.Standard),
                        PositionContent.Surface(b, "-", observed));
    }

    /// <summary>Two sources describing the same rules collide, with nobody registering
    /// anything — which is the entire mechanism.</summary>
    [Fact]
    public void SameRulesFromTwoSources_Collide()
    {
        var a = new ChessVariantEvidence { CollateralCapture = true }.Observed();
        var b = ChessVariants.Atomic;
        Assert.Equal(a.Surface(), b.Surface());
    }
}
