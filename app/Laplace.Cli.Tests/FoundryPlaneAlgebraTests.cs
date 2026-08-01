using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

/// <summary>
/// The first tests this lane has ever had. FoundryExport is ~1,800 lines and
/// FoundryCommands ~2,200, and until now nothing covered either: the only test naming
/// them stubs the export service out entirely and writes four bytes of GGUF magic.
///
/// These pin the plane algebra — the pure functions between a substrate read and the
/// eigensolver. They need no database, which is exactly why their absence was
/// indefensible.
/// </summary>
public sealed class FoundryPlaneAlgebraTests
{
    private static FoundryExport.PlaneCoo Plane(params (int R, int C, double W)[] cells)
        => new([.. cells.Select(c => c.R)], [.. cells.Select(c => c.C)], [.. cells.Select(c => c.W)]);

    // The W-C defect, pinned on the C# side. A refuted edge is not affinity of equal
    // magnitude, so the clamp must DROP it, never take its absolute value. The doc
    // comment on PositivePart has always said "drop, not abs"; nothing enforced it.
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

    // Union must NOT collapse duplicate (r,c) pairs. The native side sums them with a
    // reducer in setFromTriplets, and that summation is the ONLY mechanism weighting one
    // block against another. A dedup here would silently change the spectrum.
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

    // Normalize equalises PEAK magnitude, not total mass. That distinction is the reason
    // block influence is still governed by edge count: a plane with ten million edges and
    // one with five hundred both peak at 1.0 and contribute wildly different degree.
    // Pinning the actual behaviour so the limitation stays visible rather than assumed away.
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

    // PPMI is the only real normalizer in the export, and it is applied to exactly one
    // plane. Its defining property is that NEGATIVE pointwise mutual information is
    // dropped, not kept and not made positive — a pair seen less often than chance is
    // not evidence of association.
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
