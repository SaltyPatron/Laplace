namespace Laplace.Substrate.Tests;

using Xunit;

/// <summary>
/// Geometric invariants of the tier-0 codepoint atom layout. Per CLAUDE.md
/// invariant 2: every tier-0 codepoint sits on S^3 (unit 3-sphere) at a
/// deterministic super-Fibonacci sample of its canonical-ordering rank.
/// These tests prove on the 1.114M-row generated artifact that:
///
///   1. Every position is on S^3 (norm = 1 within float epsilon).
///   2. The empirical centroid is at the origin (within 1e-5 per axis) —
///      what uniform distribution on S^3 predicts (antipodal cancellation).
///   3. The empirical per-axis variance equals 0.25 — what uniform-on-S^3
///      predicts (each component has E[c^2] = 1/4 since E[||p||^2] = 1).
///   4. The full Unicode codepoint range [0, 0x10FFFF] is covered, no gaps,
///      no duplicates.
///
/// These are the tests verification gate G3 #17 ("1,114,112 codepoint atoms
/// present") + G3 #20 ("script clustering on S^3" — empirical-distribution
/// half) require. Script-coherence-clustering tests require additional
/// per-codepoint script metadata that lives in subsequent task work.
/// </summary>
[Collection("GeneratedSubstrate")]
public class S3DistributionTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public S3DistributionTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void TierZero_RowCount_Equals_FullCodepointSpace()
    {
        Skip.IfNotAvailable(_fix);
        Assert.Equal(1_114_112, _fix.Atoms.Count);
    }

    [Fact]
    public void EveryTierZeroPosition_LiesOnS3_NormEqualsOne()
    {
        Skip.IfNotAvailable(_fix);
        const double tolerance = 1e-9;

        var minNorm = double.MaxValue;
        var maxNorm = double.MinValue;
        var offCount = 0;

        foreach (var a in _fix.Atoms)
        {
            var norm = System.Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z + a.W * a.W);
            if (norm < minNorm) { minNorm = norm; }
            if (norm > maxNorm) { maxNorm = norm; }
            if (System.Math.Abs(norm - 1.0) > tolerance) { offCount++; }
        }

        Assert.True(
            offCount == 0,
            $"Expected every tier-0 position on S^3, but {offCount} were off (range=[{minNorm}, {maxNorm}]).");
    }

    [Fact]
    public void EmpiricalCentroid_IsAtOrigin_WithinTolerance()
    {
        Skip.IfNotAvailable(_fix);
        // For uniform-on-S^3 with N points, the centroid drift is O(1/sqrt(N)).
        // 1/sqrt(1.114M) ≈ 9.5e-4, with super-Fibonacci low-discrepancy bound
        // tightening that further. 1e-5 is a comfortable upper bound.
        const double tolerance = 1e-5;

        double sx = 0, sy = 0, sz = 0, sw = 0;
        foreach (var a in _fix.Atoms)
        {
            sx += a.X; sy += a.Y; sz += a.Z; sw += a.W;
        }
        var n = _fix.Atoms.Count;
        var cx = sx / n; var cy = sy / n; var cz = sz / n; var cw = sw / n;

        Assert.True(System.Math.Abs(cx) < tolerance, $"centroid.x = {cx}");
        Assert.True(System.Math.Abs(cy) < tolerance, $"centroid.y = {cy}");
        Assert.True(System.Math.Abs(cz) < tolerance, $"centroid.z = {cz}");
        Assert.True(System.Math.Abs(cw) < tolerance, $"centroid.w = {cw}");
    }

    [Fact]
    public void PerAxisVariance_EqualsUniformS3Prediction()
    {
        Skip.IfNotAvailable(_fix);
        // For uniform on S^3, E[c_i^2] = 1/4 for each of the 4 coordinates.
        // (The four squared components sum to ||p||^2 = 1, and by symmetry
        // each contributes 1/4 in expectation.)
        const double expected  = 0.25;
        const double tolerance = 1e-3;

        double sx2 = 0, sy2 = 0, sz2 = 0, sw2 = 0;
        foreach (var a in _fix.Atoms)
        {
            sx2 += a.X * a.X;
            sy2 += a.Y * a.Y;
            sz2 += a.Z * a.Z;
            sw2 += a.W * a.W;
        }
        var n = _fix.Atoms.Count;
        var ex2 = sx2 / n;
        var ey2 = sy2 / n;
        var ez2 = sz2 / n;
        var ew2 = sw2 / n;

        Assert.InRange(ex2, expected - tolerance, expected + tolerance);
        Assert.InRange(ey2, expected - tolerance, expected + tolerance);
        Assert.InRange(ez2, expected - tolerance, expected + tolerance);
        Assert.InRange(ew2, expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void EveryCodepointInUnicodeRange_PresentExactlyOnce()
    {
        Skip.IfNotAvailable(_fix);
        var seen = new System.Collections.Generic.HashSet<int>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms)
        {
            Assert.InRange(a.Codepoint, 0, 0x10FFFF);
            Assert.True(seen.Add(a.Codepoint), $"duplicate codepoint U+{a.Codepoint:X}");
        }
        // Coverage check: every integer in [0, 0x10FFFF] must be present.
        for (var cp = 0; cp <= 0x10FFFF; ++cp)
        {
            Assert.True(seen.Contains(cp), $"missing codepoint U+{cp:X}");
        }
    }

    [Fact]
    public void EveryHash_Is32Bytes_AndDistinct()
    {
        Skip.IfNotAvailable(_fix);
        var byHash = new System.Collections.Generic.HashSet<string>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms)
        {
            Assert.Equal(32, a.EntityHash.Length);
            var key = System.Convert.ToHexString(a.EntityHash);
            Assert.True(byHash.Add(key), "duplicate entity_hash detected — content-address collision");
        }
    }
}

internal static class Skip
{
    public static void IfNotAvailable(GeneratedSubstrateFixture fix)
    {
        // xunit.v3 dynamic skip: Assert.Skip(message) raises SkipException
        // internally and the runner reports the test as skipped.
        if (!fix.IsAvailable)
        {
            Xunit.Assert.Skip(
                "Generated substrate seed not present at ext/laplace_pg/generated/. " +
                "Run 'dotnet bin/Laplace.SeedTableGenerator/Release/net10.0/laplace-seed-gen.dll generate' first.");
        }
    }
}
