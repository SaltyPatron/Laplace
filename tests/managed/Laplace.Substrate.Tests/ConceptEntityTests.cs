namespace Laplace.Substrate.Tests;

using System.Collections.Generic;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies tier-1 concept entities (script names, general_category names,
/// block names, age strings, bidi class names, plus edge-type / role /
/// physicality-type names) are real compositions of their codepoint
/// LINESTRINGs per CLAUDE.md invariant 1+4 — NOT hardcoded English-string
/// labels in schema.
///
/// Per CLAUDE.md invariant 2: tier-1 compositions get the vertex centroid
/// IN THE 4-BALL (NOT on S^3). The radial coordinate encodes specificity;
/// tier-1 names should have norm ≤ 1 with most below 1 (interior).
///
/// Closes verification gate G3 #22.
/// </summary>
[Collection("GeneratedSubstrate")]
public class ConceptEntityTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public ConceptEntityTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void ConceptCount_IsReasonableForFoundationalSeed()
    {
        Skip.IfNotAvailable(_fix);
        // UCD properties + ISO 639 (~7,927 langs × {code, ref_name, alts})
        // + Unihan property values (~30K-50K distinct readings, radicals,
        // strokes, frequencies). Together ~50K-60K tier-1 concept entities.
        Assert.InRange(_fix.Concepts.Count, 400, 200_000);
    }

    [Fact]
    public void EveryConcept_HashEqualsMerkleOfCodepointLinestring()
    {
        Skip.IfNotAvailable(_fix);
        var hashing = new IdentityHashing();
        var atomHashByCp = BuildAtomLookup();

        var verified = 0;
        foreach (var c in _fix.Concepts)
        {
            var name = Encoding.UTF8.GetString(c.Content);
            var (children, counts) = ComposeLinestring(name, atomHashByCp);
            if (children.Count == 0) { continue; }
            var recomputed = hashing.CompositionId(children, counts);

            var stored = c.EntityHash;
            var recomp = recomputed.AsSpan().ToArray();
            Assert.Equal(stored.Length, recomp.Length);
            for (var i = 0; i < stored.Length; ++i)
            {
                Assert.True(
                    stored[i] == recomp[i],
                    $"hash mismatch at byte {i} for concept '{name}': stored=0x{stored[i]:X2} recomputed=0x{recomp[i]:X2}");
            }
            verified++;
        }
        Assert.True(verified >= 400, $"only verified {verified} concepts; expected ≥ 400");
    }

    [Fact]
    public void EveryConceptCentroid_LiesInsideOrOnTheClosed4Ball()
    {
        Skip.IfNotAvailable(_fix);
        // Vertex centroid of N points each on S^3 is at distance ≤ 1 from
        // origin (mean of unit vectors). Single-codepoint concept entities
        // (e.g., the CJK ideograph "中" in a kSimplifiedVariant value) land
        // ON S^3 since their centroid IS the single codepoint's position.
        // Multi-codepoint concept names land strictly inside.
        //
        // The strict invariant: NORM ≤ 1 + float-epsilon. We do not assert
        // an "interior majority" because Unihan readings + ISO 639 codes
        // include many 1-rune values.
        foreach (var c in _fix.Concepts)
        {
            var norm = System.Math.Sqrt(
                c.CentroidX * c.CentroidX + c.CentroidY * c.CentroidY +
                c.CentroidZ * c.CentroidZ + c.CentroidW * c.CentroidW);
            Assert.True(norm <= 1.0 + 1e-9,
                $"centroid norm {norm} exceeds unit ball for concept '{System.Text.Encoding.UTF8.GetString(c.Content)}'");
        }
    }

    [Fact]
    public void EveryConceptTrajectory_HasAtLeastTwoVertices()
    {
        Skip.IfNotAvailable(_fix);
        foreach (var c in _fix.Concepts)
        {
            Assert.True(c.TrajectoryVertices.Count >= 2,
                $"concept '{Encoding.UTF8.GetString(c.Content)}' trajectory has {c.TrajectoryVertices.Count} vertices (need ≥ 2)");
        }
    }

    [Fact]
    public void EveryEntityChildRow_ReferencesExistingTierZeroAtom()
    {
        Skip.IfNotAvailable(_fix);
        var atomHashes = new HashSet<string>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms) { atomHashes.Add(System.Convert.ToHexString(a.EntityHash)); }

        var conceptHashes = new HashSet<string>(_fix.Concepts.Count);
        foreach (var c in _fix.Concepts) { conceptHashes.Add(System.Convert.ToHexString(c.EntityHash)); }

        foreach (var ch in _fix.EntityChildren)
        {
            Assert.True(ch.ParentTier == 1, "Phase B emits tier-1 parents only");
            Assert.True(ch.ChildTier  == 0, "Phase B emits tier-0 children only");
            Assert.True(conceptHashes.Contains(System.Convert.ToHexString(ch.ParentHash)),
                "entity_child parent_hash references a concept that doesn't exist in entity_tier1.tsv");
            Assert.True(atomHashes.Contains(System.Convert.ToHexString(ch.ChildHash)),
                "entity_child child_hash references an atom that doesn't exist in entity_tier0.tsv");
            Assert.True(ch.RleCount >= 1, "entity_child rle_count must be >= 1");
            Assert.True(ch.Position >= 0, "entity_child position must be >= 0");
        }
    }

    [Fact]
    public void RleCounts_GreaterThanOne_AppearForRepeatedChars()
    {
        Skip.IfNotAvailable(_fix);
        // A name like "11.0" or "Lo" or "Common" contains adjacent repeated
        // characters — the RLE encoder MUST collapse them. Find at least one
        // entity_child row with rle_count > 1 to prove the encoder fires.
        var maxRle = 0;
        foreach (var ch in _fix.EntityChildren)
        {
            if (ch.RleCount > maxRle) { maxRle = ch.RleCount; }
        }
        Assert.True(maxRle >= 2,
            $"no entity_child row has rle_count >= 2; RLE collapse not firing (max={maxRle})");
    }

    [Fact]
    public void WellKnownConceptNames_ArePresent()
    {
        Skip.IfNotAvailable(_fix);
        var contentBytesByName = new Dictionary<string, bool>();
        foreach (var c in _fix.Concepts)
        {
            contentBytesByName[Encoding.UTF8.GetString(c.Content)] = true;
        }

        // Edge type names that MUST be in the seed.
        Assert.True(contentBytesByName.ContainsKey("script"), "edge-type 'script' missing");
        Assert.True(contentBytesByName.ContainsKey("general_category"), "edge-type 'general_category' missing");
        Assert.True(contentBytesByName.ContainsKey("block"), "edge-type 'block' missing");
        Assert.True(contentBytesByName.ContainsKey("age"), "edge-type 'age' missing");
        Assert.True(contentBytesByName.ContainsKey("bidi_class"), "edge-type 'bidi_class' missing");

        // Role names.
        Assert.True(contentBytesByName.ContainsKey("source"), "role 'source' missing");
        Assert.True(contentBytesByName.ContainsKey("target"), "role 'target' missing");

        // Physicality type.
        Assert.True(contentBytesByName.ContainsKey("codepoint_s3_substrate"), "physicality type 'codepoint_s3_substrate' missing");

        // Common script values that should always be in Unicode.
        Assert.True(contentBytesByName.ContainsKey("Latn"), "script 'Latn' missing");
        Assert.True(contentBytesByName.ContainsKey("Hani"), "script 'Hani' missing");

        // Common general_category values.
        Assert.True(contentBytesByName.ContainsKey("Lu"), "gc 'Lu' missing");
        Assert.True(contentBytesByName.ContainsKey("Ll"), "gc 'Ll' missing");
        Assert.True(contentBytesByName.ContainsKey("Cn"), "gc 'Cn' (unassigned) missing");
    }

    private Dictionary<int, AtomId> BuildAtomLookup()
    {
        // Build codepoint→AtomId by re-encoding the codepoint to UTF-8 and
        // hashing — that's how the seed generator produced the hash, and it
        // matches the hash stored in entity_tier0.tsv (proven by
        // ContentAddressedHashTests).
        var hashing = new IdentityHashing();
        var result = new Dictionary<int, AtomId>(_fix.Atoms.Count);
        foreach (var a in _fix.Atoms)
        {
            result[a.Codepoint] = AtomId.FromSpan(a.EntityHash);
        }
        return result;
    }

    private static (List<AtomId> Children, List<int> Counts) ComposeLinestring(
        string name,
        Dictionary<int, AtomId> atomHashByCp)
    {
        var children = new List<AtomId>(name.Length);
        var counts   = new List<int>(name.Length);
        int? prevCp = null;
        AtomId prevHash = default;
        var prevRun = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            var cp = rune.Value;
            if (!atomHashByCp.TryGetValue(cp, out var hash)) { continue; }
            if (prevCp.HasValue && prevCp.Value == cp)
            {
                prevRun++;
            }
            else
            {
                if (prevCp.HasValue)
                {
                    children.Add(prevHash);
                    counts.Add(prevRun);
                }
                prevCp   = cp;
                prevHash = hash;
                prevRun  = 1;
            }
        }
        if (prevCp.HasValue)
        {
            children.Add(prevHash);
            counts.Add(prevRun);
        }
        return (children, counts);
    }
}
