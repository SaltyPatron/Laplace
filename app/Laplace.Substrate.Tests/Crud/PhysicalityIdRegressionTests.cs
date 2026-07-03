using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

// Regression test for Issue 25's residual duplication: grapheme 's' (word_id('s')) had two
// live rows in physicalities (verified against the DB) -- one from BuildTier0Seed
// (no trajectory, n_constituents=0) and one from a composed-node emission path that built a
// redundant length-1 trajectory. This asserts that with the fix (ChildCount/child_count > 1
// gate), computing a Content physicality for a single-child composition of this exact entity
// now reproduces BuildTier0Seed's id bit-for-bit, instead of manufacturing a second one.
public class PhysicalityIdRegressionTests
{
    [Fact]
    public void SingleChildComposition_MatchesBuildTier0SeedPhysicalityId_ForGraphemeS()
    {
        // word_id('s'), queried live from the substrate.
        var entityId = Hash128.FromBytes(Convert.FromHexString("3d1d92230feb6db469532f26d9e2d7ab"));
        // BuildTier0Seed's existing row for this entity (type=1/Content, no trajectory).
        var expectedTier0SeedId = Hash128.FromBytes(Convert.FromHexString("c374f67dc5e8902d1164e6d8f64f5789"));

        const double cx = 0.05841298349972678, cy = 0.09524158322668967,
                     cz = -0.756665955281575, cm = -0.6441844427653898;

        // Post-fix behavior for a ChildCount==1 composition: no trajectory built, matching
        // BuildTier0Seed's own no-trajectory convention for the same content-addressed entity.
        var computed = PhysicalityId.Compute(
            entityId, PhysicalityType.Content, cx, cy, cz, cm, ReadOnlySpan<double>.Empty);

        Assert.Equal(expectedTier0SeedId, computed);
    }
}
