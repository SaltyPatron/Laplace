using Xunit;
using Laplace.Cli;

namespace Laplace.Cli.Tests;

/// <summary>
/// GH #521 / docs/specs/18 §2. Pins the two properties that make typed strata worth having:
/// the layout is EXACT (every dim owned by exactly one stratum, no slack) and the widths are
/// COUNTED (they follow the census, and structural strata are never squeezed to make room).
/// </summary>
public class ResidualStrataTests
{
    private static ResidualStrata.Census Census(
        int dModel = 2048, int pe = 8, int wordRank = 256, int senseRank = 512,
        int frames = 1221, int bands = 13) =>
        new(dModel, pe, wordRank, senseRank, frames, bands);

    [Fact]
    public void Allocation_CoversEveryDimExactlyOnce()
    {
        var layout = ResidualStrata.Allocate(Census());
        var owners = new int[layout.DModel];
        foreach (var b in layout.Blocks)
            for (int i = b.Offset; i < b.End; i++) owners[i]++;

        Assert.All(owners, o => Assert.Equal(1, o));
    }

    [Fact]
    public void Allocation_BlocksAreContiguousAndDisjoint()
    {
        var layout = ResidualStrata.Allocate(Census());
        int expected = 0;
        foreach (var b in layout.Blocks)
        {
            Assert.Equal(expected, b.Offset);
            expected = b.End;
        }
        Assert.Equal(layout.DModel, expected);
    }

    [Fact]
    public void StructuralWidths_FollowTheCensusExactly()
    {
        // S, F and G are counts of things that exist. If these drift from the census the
        // layout has started choosing instead of counting, which is the defect.
        var layout = ResidualStrata.Allocate(Census(pe: 8, frames: 1221, bands: 13));
        Assert.Equal(8, layout.S.Width);
        Assert.Equal(1221, layout.F.Width);
        Assert.Equal(13, layout.G.Width);
    }

    [Fact]
    public void SpectralStrata_AbsorbTheSlackWhenCountedRankFits()
    {
        var c = Census(dModel: 2048, pe: 8, wordRank: 256, senseRank: 512, frames: 100, bands: 13);
        var layout = ResidualStrata.Allocate(c);

        Assert.False(layout.Truncated);
        Assert.Equal(256, layout.W.Width);                       // counted rank, unchanged
        Assert.Equal(2048 - 8 - 100 - 13 - 256, layout.C.Width);  // C takes the remainder
    }

    [Fact]
    public void SpectralStrata_AreCutProportionally_StructuralAreNot()
    {
        // d_model too small for the counted spectral rank. The cut must land on W and C,
        // never on frames or band gates — a frame with no dim cannot be represented at all,
        // where a rank cut is bounded error.
        var c = Census(dModel: 200, pe: 8, wordRank: 400, senseRank: 800, frames: 50, bands: 13);
        var layout = ResidualStrata.Allocate(c);

        Assert.True(layout.Truncated);
        Assert.Equal(8, layout.S.Width);
        Assert.Equal(50, layout.F.Width);
        Assert.Equal(13, layout.G.Width);
        Assert.Equal(200 - 8 - 50 - 13, layout.W.Width + layout.C.Width);
        // 400:800 is 1:2, so W should get about a third of the 129 spectral dims.
        Assert.InRange(layout.W.Width, 40, 48);
    }

    [Fact]
    public void Allocation_FailsClosed_WhenDModelCannotHoldTheOntology()
    {
        // 64 dims against 100 frames. The old anonymous stream would have silently produced
        // dead directions; this is a configuration error and says so.
        var c = Census(dModel: 64, pe: 8, wordRank: 16, senseRank: 16, frames: 100, bands: 13);
        var ex = Assert.Throws<InvalidOperationException>(() => ResidualStrata.Allocate(c));
        Assert.Contains("cannot hold the counted strata", ex.Message);
    }

    [Fact]
    public void Owner_ReportsTheStratumForEveryDim()
    {
        var layout = ResidualStrata.Allocate(Census());
        Assert.Equal(ResidualStrata.Stratum.S, layout.Owner(0));
        Assert.Equal(ResidualStrata.Stratum.W, layout.Owner(layout.W.Offset));
        Assert.Equal(ResidualStrata.Stratum.C, layout.Owner(layout.C.End - 1));
        Assert.Equal(ResidualStrata.Stratum.G, layout.Owner(layout.DModel - 1));
        Assert.Null(layout.Owner(layout.DModel));
    }

    [Fact]
    public void BlockOrthonormalize_ProducesAnOrthonormalBasis()
    {
        var layout = ResidualStrata.Allocate(
            new ResidualStrata.Census(DModel: 6, PeDims: 1, WordSpectralRank: 2,
                                      SenseSpectralRank: 2, FramesWithWitnessedLus: 0, BandCount: 1));
        int rows = 8;
        var basis = new double[rows * layout.DModel];
        var rng = new Random(1);
        for (int i = 0; i < basis.Length; i++) basis[i] = rng.NextDouble() - 0.5;

        int collapsed = ResidualStrata.BlockOrthonormalize(basis, rows, layout);
        Assert.Equal(0, collapsed);

        int d = layout.DModel;
        for (int a = 0; a < d; a++)
            for (int b = a; b < d; b++)
            {
                double dot = 0.0;
                for (int r = 0; r < rows; r++) dot += basis[r * d + a] * basis[r * d + b];
                Assert.Equal(a == b ? 1.0 : 0.0, dot, 9);
            }
    }

    [Fact]
    public void BlockOrthonormalize_ReportsCollapsedDirections_RatherThanHidingThem()
    {
        // Two identical columns: the second carries no independent direction. That is a
        // finding about the data, and the count is how the caller learns it.
        var layout = ResidualStrata.Allocate(
            new ResidualStrata.Census(DModel: 4, PeDims: 0, WordSpectralRank: 2,
                                      SenseSpectralRank: 2, FramesWithWitnessedLus: 0, BandCount: 0));
        int rows = 4;
        var basis = new double[rows * 4];
        for (int r = 0; r < rows; r++)
        {
            basis[r * 4 + 0] = r + 1;
            basis[r * 4 + 1] = r + 1;   // duplicate of column 0
            basis[r * 4 + 2] = r == 0 ? 1 : 0;
            basis[r * 4 + 3] = r == 1 ? 1 : 0;
        }

        Assert.Equal(1, ResidualStrata.BlockOrthonormalize(basis, rows, layout));
        for (int r = 0; r < rows; r++) Assert.Equal(0.0, basis[r * 4 + 1]);
    }
}
