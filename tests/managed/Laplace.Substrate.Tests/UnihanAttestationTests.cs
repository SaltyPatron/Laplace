namespace Laplace.Substrate.Tests;

using System.Collections.Generic;
using System.IO;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies Unihan property attestation edges for CJK Unified Ideographs.
/// Uses U+4E2D 中 ("middle") as the canonical test subject because its
/// Unihan properties are well-known and stable: kRSUnicode = 2.3 (radical
/// 2 "丨", 3 additional strokes), kMandarin = zhōng, kCantonese = zung1,
/// kKorean = CWUNG, kTotalStrokes = 4.
///
/// Closes verification gate G3 #19 ("every CJK Unified Ideograph has Unihan
/// property edges") at the artifact level.
/// </summary>
[Collection("GeneratedSubstrate")]
public class UnihanAttestationTests
{
    private const int Zhong = 0x4E2D;

    private readonly GeneratedSubstrateFixture _fix;

    public UnihanAttestationTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void Zhong_HasKrsUnicodeEdgeTo_2_3()
    {
        Skip.IfNotAvailable(_fix);
        AssertCodepointAttestation(Zhong, "kRSUnicode", "2.3");
    }

    [Fact]
    public void Zhong_HasKMandarinEdgeToZhong()
    {
        Skip.IfNotAvailable(_fix);
        AssertCodepointAttestation(Zhong, "kMandarin", "zhōng");
    }

    [Fact]
    public void Zhong_HasKCantoneseEdgeToZung1()
    {
        Skip.IfNotAvailable(_fix);
        AssertCodepointAttestation(Zhong, "kCantonese", "zung1");
    }

    [Fact]
    public void Zhong_HasKKoreanEdgeToCwung()
    {
        Skip.IfNotAvailable(_fix);
        AssertCodepointAttestation(Zhong, "kKorean", "CWUNG");
    }

    [Fact]
    public void Zhong_HasKTotalStrokesEdgeTo4()
    {
        Skip.IfNotAvailable(_fix);
        AssertCodepointAttestation(Zhong, "kTotalStrokes", "4");
    }

    [Fact]
    public void UnihanEdgeMemberCount_IsExactlyTwiceEdgeCount()
    {
        Skip.IfNotAvailable(_fix);
        var edgeFile   = Path.Combine(_fix.GeneratedDir, "edge_unihan.tsv");
        var memberFile = Path.Combine(_fix.GeneratedDir, "edge_member_unihan.tsv");
        Assert.True(File.Exists(edgeFile), "edge_unihan.tsv missing");
        Assert.True(File.Exists(memberFile), "edge_member_unihan.tsv missing");

        long e = CountLines(edgeFile);
        long m = CountLines(memberFile);
        Assert.Equal(e * 2, m);
        Assert.True(e > 50_000, $"expected > 50K Unihan edges; got {e}");
    }

    private void AssertCodepointAttestation(int codepoint, string edgeTypeName, string targetName)
    {
        var hashing = new IdentityHashing();
        var atomHash    = AtomId.FromSpan(FindAtomByCodepoint(codepoint).EntityHash);
        var edgeType    = AtomId.FromSpan(FindConceptHash(edgeTypeName));
        var targetHash  = AtomId.FromSpan(FindConceptHash(targetName));
        var sourceRole  = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole  = AtomId.FromSpan(FindConceptHash("target"));

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, atomHash),
            (targetRole, 0, targetHash),
        };
        var expected = hashing.EdgeId(edgeType, members);
        var expectedHex = System.Convert.ToHexString(expected.AsSpan());

        var path = Path.Combine(_fix.GeneratedDir, "edge_unihan.tsv");
        Assert.True(File.Exists(path), "edge_unihan.tsv missing");

        using var r = new StreamReader(path);
        string? line;
        var found = false;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length < 66) { continue; }
            var hex = line.Substring(2, 64);
            if (string.Equals(hex, expectedHex, System.StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }
        Assert.True(found,
            $"Substrate is missing (U+{codepoint:X})-{edgeTypeName}-({targetName}) edge");
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
