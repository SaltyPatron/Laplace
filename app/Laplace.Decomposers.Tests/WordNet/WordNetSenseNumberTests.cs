using System.Reflection;
using Xunit;

namespace Laplace.Decomposers.WordNet.Tests;

/// <summary>
/// index.sense ships TWO frequency signals and the parser read only one. 82% of
/// WordNet-3.0 senses (171,463 / 206,941) carry tag_cnt = 0, so their HAS_SENSE
/// magnitude was 0 — score exactly 0.5, a draw — and every sense of such a lemma
/// folded to an identical rating. Any reader that ranks on the fold then sees zero
/// information for those tokens, which is measurable on the live substrate: `what`
/// and `the` have eff_mu spread 0.0 across their senses while chess/pawn/dog measure
/// 95.9/80.4/77.5. sense_number is WordNet's own ordering (1 = most common) and
/// separates them.
/// </summary>
public sealed class WordNetSenseNumberTests
{
    // WnSense is private; reach it the way the decomposer builds it.
    private static object NewSense(int tagCount, int senseNumber)
    {
        var t = typeof(WordNetDecomposer).Assembly
            .GetType("Laplace.Decomposers.WordNet.WordNetDecomposer+WnSense", throwOnError: true)!;
        return Activator.CreateInstance(
            t, ["glacier%1:17:00::", 9289331L, 'n', "glacier", tagCount, senseNumber])!;
    }

    private static double Magnitude(object sense) =>
        (double)sense.GetType().GetProperty("WitnessedMagnitude",
            BindingFlags.Public | BindingFlags.Instance)!.GetValue(sense)!;

    [Fact]
    public void Senses_With_No_TagCount_Are_Separated_By_WordNets_Own_Ordering()
    {
        // pawn's four senses all ship tag_cnt = 0 and sense_number 1..4.
        double s1 = Magnitude(NewSense(0, 1));
        double s2 = Magnitude(NewSense(0, 2));
        double s4 = Magnitude(NewSense(0, 4));
        Assert.True(s1 > s2 && s2 > s4, $"expected ordering, got {s1} {s2} {s4}");
        Assert.True(s4 > 0, "a ranked sense must not fold to a draw");
    }

    [Fact]
    public void TagCount_Stays_Dominant_Where_The_Corpus_Reports_It()
    {
        // A sense witnessed once in the semantic concordance outranks an unwitnessed
        // sense that merely sorts first — the 1/n term is bounded by 1 and cannot
        // overturn real occurrence evidence.
        Assert.True(Magnitude(NewSense(1, 9)) > Magnitude(NewSense(0, 1)));
    }

    [Fact]
    public void Absent_Sense_Number_Degrades_To_The_Old_Behaviour()
    {
        Assert.Equal(3.0, Magnitude(NewSense(3, 0)));
    }
}
