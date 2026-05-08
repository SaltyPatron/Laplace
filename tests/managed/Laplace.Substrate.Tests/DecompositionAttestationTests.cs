namespace Laplace.Substrate.Tests;

using System.Collections.Generic;
using System.IO;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies UCD decomposition attestations. Each codepoint with a non-trivial
/// dt/dm gets two edges plus a tier-1 composition entity for the decomposition
/// target. Test subject: U+00C1 LATIN CAPITAL LETTER A WITH ACUTE, dt = "can",
/// dm = "0041 0301" (decomposes to A + combining acute).
/// </summary>
[Collection("GeneratedSubstrate")]
public class DecompositionAttestationTests
{
    private const int LatinAWithAcute = 0x00C1;

    private readonly GeneratedSubstrateFixture _fix;

    public DecompositionAttestationTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void LatinAWithAcute_HasDtCanonicalEdge()
    {
        Skip.IfNotAvailable(_fix);
        var hashing      = new IdentityHashing();
        var srcAtomHash  = AtomId.FromSpan(FindAtomByCodepoint(LatinAWithAcute).EntityHash);
        var dtType       = AtomId.FromSpan(FindConceptHash("decomposition_type"));
        var canConcept   = AtomId.FromSpan(FindConceptHash("can"));
        var sourceRole   = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole   = AtomId.FromSpan(FindConceptHash("target"));

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, srcAtomHash),
            (targetRole, 0, canConcept),
        };
        var expected = hashing.EdgeId(dtType, members);
        AssertEdgePresent(expected, "edge_decomp.tsv");
    }

    [Fact]
    public void LatinAWithAcute_HasDmEdgeToDecompositionTargetCompositionOf_A_plus_CombiningAcute()
    {
        Skip.IfNotAvailable(_fix);
        var hashing      = new IdentityHashing();
        var srcAtomHash  = AtomId.FromSpan(FindAtomByCodepoint(LatinAWithAcute).EntityHash);
        var aHash        = AtomId.FromSpan(FindAtomByCodepoint(0x0041).EntityHash);
        var acuteHash    = AtomId.FromSpan(FindAtomByCodepoint(0x0301).EntityHash);
        var sourceRole   = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole   = AtomId.FromSpan(FindConceptHash("target"));
        var dmType       = AtomId.FromSpan(FindConceptHash("decomposition_mapping"));

        // Target composition hash = BLAKE3 Merkle of (h(A), h(acute)) with rle 1,1.
        var children = new List<AtomId> { aHash, acuteHash };
        var counts   = new List<int>    { 1,     1         };
        var targetComposition = hashing.CompositionId(children, counts);

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, srcAtomHash),
            (targetRole, 0, targetComposition),
        };
        var expected = hashing.EdgeId(dmType, members);
        AssertEdgePresent(expected, "edge_decomp.tsv");
    }

    [Fact]
    public void DecompositionTargetCompositionFor_A_plus_CombiningAcute_ExistsInTier1Decomp()
    {
        Skip.IfNotAvailable(_fix);
        var hashing   = new IdentityHashing();
        var aHash     = AtomId.FromSpan(FindAtomByCodepoint(0x0041).EntityHash);
        var acuteHash = AtomId.FromSpan(FindAtomByCodepoint(0x0301).EntityHash);
        var children  = new List<AtomId> { aHash, acuteHash };
        var counts    = new List<int>    { 1,     1         };
        var expected  = hashing.CompositionId(children, counts);
        var expectedHex = System.Convert.ToHexString(expected.AsSpan());

        var path = Path.Combine(_fix.GeneratedDir, "entity_tier1_decomp.tsv");
        Assert.True(File.Exists(path), "entity_tier1_decomp.tsv missing");

        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length < 66) { continue; }
            var hex = line.Substring(2, 64);
            if (string.Equals(hex, expectedHex, System.StringComparison.OrdinalIgnoreCase))
            {
                return; // found
            }
        }
        Assert.Fail("decomposition target composition for A + combining acute not in entity_tier1_decomp.tsv");
    }

    [Fact]
    public void DecompositionEdgeMemberCount_IsExactlyTwiceEdgeCount()
    {
        Skip.IfNotAvailable(_fix);
        var edgeFile   = Path.Combine(_fix.GeneratedDir, "edge_decomp.tsv");
        var memberFile = Path.Combine(_fix.GeneratedDir, "edge_member_decomp.tsv");
        Assert.True(File.Exists(edgeFile), "edge_decomp.tsv missing");
        Assert.True(File.Exists(memberFile), "edge_member_decomp.tsv missing");
        Assert.Equal(CountLines(edgeFile) * 2, CountLines(memberFile));
    }

    [Fact]
    public void DecompositionTargets_AllHaveAtLeastOneEntityChildRow()
    {
        Skip.IfNotAvailable(_fix);
        var tier1Path = Path.Combine(_fix.GeneratedDir, "entity_tier1_decomp.tsv");
        var childPath = Path.Combine(_fix.GeneratedDir, "entity_child_decomp.tsv");
        Assert.True(File.Exists(tier1Path), "entity_tier1_decomp.tsv missing");
        Assert.True(File.Exists(childPath), "entity_child_decomp.tsv missing");

        var tier1Hashes = new HashSet<string>();
        using (var r = new StreamReader(tier1Path))
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 66) { continue; }
                tier1Hashes.Add(line.Substring(2, 64));
            }
        }

        var childParents = new HashSet<string>();
        using (var r = new StreamReader(childPath))
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length < 66) { continue; }
                childParents.Add(line.Substring(2, 64));
            }
        }

        var orphans = 0;
        foreach (var t in tier1Hashes)
        {
            if (!childParents.Contains(t)) { orphans++; }
        }
        Assert.Equal(0, orphans);
    }

    private void AssertEdgePresent(AtomId expectedHash, string fileName)
    {
        var expectedHex = System.Convert.ToHexString(expectedHash.AsSpan());
        var path        = Path.Combine(_fix.GeneratedDir, fileName);
        Assert.True(File.Exists(path), $"{fileName} missing");

        using var r = new StreamReader(path);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length < 66) { continue; }
            var hex = line.Substring(2, 64);
            if (string.Equals(hex, expectedHex, System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        Assert.Fail($"expected edge_hash {expectedHex} not found in {fileName}");
    }

    private byte[] FindConceptHash(string name)
    {
        foreach (var c in _fix.Concepts)
        {
            if (Encoding.UTF8.GetString(c.Content) == name) { return c.EntityHash; }
        }
        throw new KeyNotFoundException($"concept entity '{name}' not in seed");
    }

    private GeneratedSubstrateFixture.TierZeroRow FindAtomByCodepoint(int cp)
    {
        foreach (var a in _fix.Atoms)
        {
            if (a.Codepoint == cp) { return a; }
        }
        throw new KeyNotFoundException($"U+{cp:X}");
    }

    private static long CountLines(string path)
    {
        long n = 0;
        using var r = new StreamReader(path);
        while (r.ReadLine() != null) { n++; }
        return n;
    }
}
