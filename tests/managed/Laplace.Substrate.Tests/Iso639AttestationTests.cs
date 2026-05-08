namespace Laplace.Substrate.Tests;

using System.Collections.Generic;
using System.IO;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies the ISO 639-3 language attestation layer. Each of the 7,927
/// languages is a tier-1 substrate entity (its 3-letter Id is the canonical
/// content; its hash is BLAKE3 Merkle of that code's codepoint LINESTRING).
/// Property edges connect each language to its scope, type, ref_name, and
/// alternate-code concept entities.
///
/// Per CLAUDE.md feedback "Substrate is language-agnostic — no anchor
/// entities": cat / neko / gato are PEERS. The ISO 639 entities here are
/// the language-typing scaffold, not anchors over content.
///
/// Closes verification gate G3 #21 ("ISO 639 entity completeness") at the
/// artifact level.
/// </summary>
[Collection("GeneratedSubstrate")]
public class Iso639AttestationTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public Iso639AttestationTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void English_eng_LanguageEntityHash_EqualsCodepointLinestringMerkle()
    {
        Skip.IfNotAvailable(_fix);
        var hashing = new IdentityHashing();
        var atomLookup = BuildAtomHashLookup();

        // "eng" = [e, n, g] → BLAKE3 Merkle of (h(e), h(n), h(g)) with rle=1 each.
        var children = new List<AtomId>
        {
            atomLookup[(int)'e'],
            atomLookup[(int)'n'],
            atomLookup[(int)'g'],
        };
        var counts = new List<int> { 1, 1, 1 };
        var expected = hashing.CompositionId(children, counts);

        var stored = FindConceptHash("eng");
        AssertHashesEqual(expected.AsSpan(), stored);
    }

    [Fact]
    public void English_eng_HasIso639ScopeIndividualEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_scope",
            sourceName:   "eng",
            targetName:   "Individual",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void English_eng_HasIso639TypeLivingEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_type",
            sourceName:   "eng",
            targetName:   "Living",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void English_eng_HasIso639RefNameEnglishEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_ref_name",
            sourceName:   "eng",
            targetName:   "English",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void English_eng_HasIso639Part1EnEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_part1",
            sourceName:   "eng",
            targetName:   "en",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void Japanese_jpn_HasIso639TypeLivingEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_type",
            sourceName:   "jpn",
            targetName:   "Living",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void Latin_lat_HasIso639TypeHistoricalEdge()
    {
        Skip.IfNotAvailable(_fix);
        // ISO 639-3 (iso-639-3.tab) marks Latin as type code 'H' = Historical.
        AssertEdgeExists(
            edgeTypeName: "iso639_type",
            sourceName:   "lat",
            targetName:   "Historical",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void Esperanto_epo_HasIso639TypeConstructedEdge()
    {
        Skip.IfNotAvailable(_fix);
        AssertEdgeExists(
            edgeTypeName: "iso639_type",
            sourceName:   "epo",
            targetName:   "Constructed",
            edgeFile:     "edge_iso639.tsv");
    }

    [Fact]
    public void Iso639EdgeMemberCount_IsExactlyTwiceEdgeCount()
    {
        Skip.IfNotAvailable(_fix);
        var edgeFile   = ResolveTsv("edge_iso639.tsv");
        var memberFile = ResolveTsv("edge_member_iso639.tsv");
        if (edgeFile is null || memberFile is null) { Assert.Fail("ISO 639 edge files missing"); }
        var edgeCount   = CountLines(edgeFile);
        var memberCount = CountLines(memberFile);
        Assert.Equal(edgeCount * 2, memberCount);
    }

    private void AssertEdgeExists(
        string edgeTypeName,
        string sourceName,
        string targetName,
        string edgeFile)
    {
        var hashing = new IdentityHashing();
        var edgeType   = AtomId.FromSpan(FindConceptHash(edgeTypeName));
        var sourceHash = AtomId.FromSpan(FindConceptHash(sourceName));
        var targetHash = AtomId.FromSpan(FindConceptHash(targetName));
        var sourceRole = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole = AtomId.FromSpan(FindConceptHash("target"));

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, sourceHash),
            (targetRole, 0, targetHash),
        };
        var expected = hashing.EdgeId(edgeType, members);
        var expectedHex = System.Convert.ToHexString(expected.AsSpan());

        var path = ResolveTsv(edgeFile);
        Assert.NotNull(path);
        using var r = new StreamReader(path!);
        string? line;
        var found = false;
        while ((line = r.ReadLine()) != null)
        {
            // line begins with `\x` + 64 hex chars + tab. Compare bytes.
            if (line.Length < 66) { continue; }
            var hexCandidate = line.Substring(2, 64);
            if (string.Equals(hexCandidate, expectedHex, System.StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"Substrate is missing ({sourceName})-{edgeTypeName}-({targetName}) edge in {edgeFile}");
    }

    private byte[] FindConceptHash(string name)
    {
        foreach (var c in _fix.Concepts)
        {
            if (Encoding.UTF8.GetString(c.Content) == name) { return c.EntityHash; }
        }
        throw new KeyNotFoundException($"concept entity '{name}' not in seed");
    }

    private Dictionary<int, AtomId> BuildAtomHashLookup()
    {
        var result = new Dictionary<int, AtomId>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms) { result[a.Codepoint] = AtomId.FromSpan(a.EntityHash); }
        return result;
    }

    private string? ResolveTsv(string fileName)
    {
        var path = Path.Combine(_fix.GeneratedDir, fileName);
        return File.Exists(path) ? path : null;
    }

    private static long CountLines(string path)
    {
        long n = 0;
        using var r = new StreamReader(path);
        while (r.ReadLine() != null) { n++; }
        return n;
    }

    private static void AssertHashesEqual(System.ReadOnlySpan<byte> expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; ++i)
        {
            Assert.True(expected[i] == actual[i],
                $"hash mismatch at byte {i}: expected=0x{expected[i]:X2} actual=0x{actual[i]:X2}");
        }
    }
}
