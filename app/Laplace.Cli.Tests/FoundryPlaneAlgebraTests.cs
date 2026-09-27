using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

/// <summary>
/// Plane algebra of model export: the pure functions between a consensus read and the
/// eigensolver. No database.
/// </summary>
public sealed class FoundryPlaneAlgebraTests
{
    private static FoundryExport.PlaneCoo Plane(params (int R, int C, double W)[] cells)
        => new([.. cells.Select(c => c.R)], [.. cells.Select(c => c.C)], [.. cells.Select(c => c.W)]);

    // A refuted edge is not affinity of equal magnitude: PositivePart drops it rather than
    // taking its absolute value.
    [Fact]
    public void PositivePart_DropsRefutedEdges_RatherThanFlippingThem()
    {
        var clamped = FoundryExport.PositivePart(Plane((0, 1, 0.75), (1, 2, -0.75), (2, 3, 0.5)));

        Assert.Equal(2, clamped.Nnz);
        Assert.All(clamped.Vals, v => Assert.True(v > 0, $"a non-positive weight {v} survived the clamp"));
        Assert.DoesNotContain(0.75, clamped.Vals.Where((_, i) => clamped.Rows[i] == 1));
    }

    [Fact]
    public void PositivePart_DropsExactZero_BecauseZeroAffinityIsNoEdge()
    {
        var clamped = FoundryExport.PositivePart(Plane((0, 1, 0.0), (1, 2, 1.0)));
        Assert.Equal(1, clamped.Nnz);
        Assert.Equal(1.0, clamped.Vals[0]);
    }

    // Union keeps duplicate (r,c) pairs. The native side sums them in setFromTriplets, and
    // that sum is what weights one plane against another.
    [Fact]
    public void Union_ConcatenatesWithoutCollapsingDuplicatePairs()
    {
        var merged = FoundryExport.Union(Plane((0, 1, 0.4)), Plane((0, 1, 0.6)), Plane((2, 3, 1.0)));

        Assert.Equal(3, merged.Nnz);
        var onePair = Enumerable.Range(0, merged.Nnz)
            .Where(i => merged.Rows[i] == 0 && merged.Cols[i] == 1)
            .Select(i => merged.Vals[i])
            .ToArray();
        Assert.Equal(2, onePair.Length);
        Assert.Contains(0.4, onePair);
        Assert.Contains(0.6, onePair);
    }

    [Fact]
    public void Union_OfNothingIsEmpty_NotNull()
    {
        var merged = FoundryExport.Union(Plane(), Plane());
        Assert.Equal(0, merged.Nnz);
    }

    // Normalize scales peak magnitude to 1, not total mass, so a plane's influence on the
    // union still follows its edge count.
    [Fact]
    public void Normalize_ScalesPeakToOne_AndPreservesSign()
    {
        var scaled = FoundryExport.Normalize(Plane((0, 1, -4.0), (1, 2, 2.0)));

        Assert.Equal(-1.0, scaled.Vals[0], 12);
        Assert.Equal(0.5, scaled.Vals[1], 12);
    }

    [Fact]
    public void Normalize_LeavesAnAllZeroPlaneAlone_RatherThanDividingByZero()
    {
        var scaled = FoundryExport.Normalize(Plane((0, 1, 0.0), (1, 2, 0.0)));
        Assert.All(scaled.Vals, v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void TrimRowToTopK_KeepsTheLargestByMagnitude_IncludingNegatives()
    {
        var row = new List<(int Col, double W)> { (1, 0.1), (2, -9.0), (3, 5.0), (4, 0.2) };
        FoundryExport.TrimRowToTopK(row, 2);

        Assert.Equal(2, row.Count);
        Assert.Contains(row, e => e.Col == 2);
        Assert.Contains(row, e => e.Col == 3);
    }

    [Fact]
    public void TrimRowToTopK_BelowTheCap_IsALeaveAlone()
    {
        var row = new List<(int Col, double W)> { (1, 0.1), (2, 0.2) };
        FoundryExport.TrimRowToTopK(row, 8);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public void CooFromAdj_HonoursTheDegreeCapPerRow()
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>
        {
            [0] = [(1, 1.0), (2, 2.0), (3, 3.0)],
            [1] = [(0, 1.0)],
        };

        var coo = FoundryExport.CooFromAdj(adj, degreeCap: 2);

        Assert.Equal(3, coo.Nnz);
        Assert.Equal(2, Enumerable.Range(0, coo.Nnz).Count(i => coo.Rows[i] == 0));
    }

    // Negative pointwise mutual information is dropped, not kept and not made positive: a
    // pair seen less often than chance is not evidence of association.
    [Fact]
    public void ApplyPpmi_DropsNegativePointwiseMutualInformation()
    {
        // Two hubs that co-occur with everything, plus one genuinely tight pair.
        var adj = new Dictionary<int, List<(int Col, double W)>>
        {
            [0] = [(1, 100.0), (2, 100.0), (3, 1.0)],
            [1] = [(0, 100.0), (2, 100.0)],
            [2] = [(0, 100.0), (1, 100.0)],
            [3] = [(0, 1.0)],
        };

        FoundryExport.ApplyPpmi(adj);

        foreach (var (_, row) in adj)
            Assert.All(row, e => Assert.True(e.W > 0,
                $"PPMI left a non-positive weight {e.W}; negative PMI must be dropped"));
    }

    [Fact]
    public void ApplyPpmi_OnAnEmptyGraph_DoesNotThrow()
    {
        var adj = new Dictionary<int, List<(int Col, double W)>>();
        FoundryExport.ApplyPpmi(adj);
        Assert.Empty(adj);
    }
}
