namespace Laplace.Substrate.Tests;

using Xunit;

/// <summary>
/// Cross-checks that every atom row has exactly one corresponding
/// physicality row in the codepoint-atom partition with identical position
/// + entity_hash. Per CLAUDE.md invariant 7: physicality is partitioned by
/// physicality_type_hash (one shared type for substrate codepoint atoms);
/// for tier-0 atoms the physicality position must equal the entity centroid
/// (both content-derived from the same super-Fibonacci sample).
/// </summary>
[Collection("GeneratedSubstrate")]
public class PhysicalityConsistencyTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public PhysicalityConsistencyTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void PhysicalityRowCount_EqualsAtomCount()
    {
        Skip.IfNotAvailable(_fix);
        Assert.Equal(_fix.Atoms.Count, _fix.Physicality.Count);
    }

    [Fact]
    public void EveryPhysicalityRow_SharesOnePhysicalityTypeHash()
    {
        Skip.IfNotAvailable(_fix);
        Assert.NotEmpty(_fix.Physicality);
        var expected = _fix.Physicality[0].PhysicalityTypeHash;
        var distinctTypes = new System.Collections.Generic.HashSet<string>();
        foreach (var p in _fix.Physicality)
        {
            distinctTypes.Add(System.Convert.ToHexString(p.PhysicalityTypeHash));
        }
        Assert.Single(distinctTypes);
        Assert.Equal(32, expected.Length);
    }

    [Fact]
    public void PhysicalityPositions_MatchAtomPositions()
    {
        Skip.IfNotAvailable(_fix);

        // Index atoms by hash for O(1) lookup.
        var atomByHash = new System.Collections.Generic.Dictionary<string, GeneratedSubstrateFixture.TierZeroRow>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms)
        {
            atomByHash[System.Convert.ToHexString(a.EntityHash)] = a;
        }

        foreach (var p in _fix.Physicality)
        {
            var key = System.Convert.ToHexString(p.EntityHash);
            Assert.True(atomByHash.TryGetValue(key, out var a),
                $"physicality row for hash {key} has no matching atom");
            Assert.Equal(a.X, p.X, precision: 15);
            Assert.Equal(a.Y, p.Y, precision: 15);
            Assert.Equal(a.Z, p.Z, precision: 15);
            Assert.Equal(a.W, p.W, precision: 15);
        }
    }

    [Fact]
    public void HilbertIndexes_AllDistinct()
    {
        Skip.IfNotAvailable(_fix);
        // 1.114M points in [0, 2^64) via Skilling 4D Hilbert with 16 bits per
        // axis = 64 bits total; bijection guarantees uniqueness.
        var seen = new System.Collections.Generic.HashSet<long>(_fix.Physicality.Count);
        foreach (var p in _fix.Physicality)
        {
            Assert.True(seen.Add(p.HilbertIndex), $"duplicate hilbert_index {p.HilbertIndex:X}");
        }
    }
}
