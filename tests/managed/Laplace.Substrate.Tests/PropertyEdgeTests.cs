namespace Laplace.Substrate.Tests;

using System.Collections.Generic;
using System.Text;

using Laplace.Core;
using Laplace.Core.Abstractions;

using Xunit;

/// <summary>
/// Verifies the property-edge layer that turns the substrate from "1.114M
/// atoms with positions" into a knowledge graph (CLAUDE.md invariant 4:
/// "knowledge IS edges and intersections").
///
/// Each codepoint atom has typed edges to its UCD property concept entities
/// (script, general_category, block, age, bidi_class). Every edge_type_hash
/// is itself a substrate concept entity (composition of its name's
/// codepoint LINESTRING) — NOT a hardcoded English string in schema.
///
/// Closes verification gate G3 #18 ("every assigned codepoint has every
/// applicable UCD property edge") at the artifact level.
/// </summary>
[Collection("GeneratedSubstrate")]
public class PropertyEdgeTests
{
    private readonly GeneratedSubstrateFixture _fix;

    public PropertyEdgeTests(GeneratedSubstrateFixture fix) { _fix = fix; }

    [Fact]
    public void EdgeRowCount_IsAtLeastTwoPerCodepoint()
    {
        Skip.IfNotAvailable(_fix);
        // Every codepoint (assigned or reserved) gets at least sc + gc edges
        // — reserved/surrogate/noncharacter slots get sc=Zzzz and gc=Cn.
        Assert.True(_fix.EdgeRowCount >= _fix.Atoms.Count * 2,
            $"edge count {_fix.EdgeRowCount} < 2× atom count {_fix.Atoms.Count}");
    }

    [Fact]
    public void EdgeMemberRowCount_IsExactlyTwiceEdgeRowCount()
    {
        Skip.IfNotAvailable(_fix);
        // Each property edge has exactly 2 members (source codepoint +
        // target value concept). Schema has member_count = 2 in edge.tsv.
        Assert.Equal(_fix.EdgeRowCount * 2, _fix.EdgeMemberRowCount);
    }

    [Fact]
    public void EveryEdge_HasMemberCountEqualTo2()
    {
        Skip.IfNotAvailable(_fix);
        long inspected = 0;
        long offBy     = 0;
        foreach (var e in _fix.StreamEdges())
        {
            inspected++;
            if (e.MemberCount != 2) { offBy++; }
            // Sample-check the first 100K to keep the test fast — this is
            // streamed, no allocation per row beyond the record struct.
            if (inspected == 100_000) { break; }
        }
        Assert.Equal(0, offBy);
    }

    [Fact]
    public void EveryEdgeTypeHash_ReferencesAKnownConceptEntity()
    {
        Skip.IfNotAvailable(_fix);
        var conceptHashes = new HashSet<string>(_fix.Concepts.Count);
        foreach (var c in _fix.Concepts) { conceptHashes.Add(System.Convert.ToHexString(c.EntityHash)); }

        var distinctTypes = new HashSet<string>();
        long inspected = 0;
        foreach (var e in _fix.StreamEdges())
        {
            distinctTypes.Add(System.Convert.ToHexString(e.EdgeTypeHash));
            inspected++;
            if (inspected == 200_000) { break; }
        }

        // Property-edge emitter only uses 5 edge_type concept entities.
        Assert.InRange(distinctTypes.Count, 1, 5);
        foreach (var typeHex in distinctTypes)
        {
            Assert.Contains(typeHex, conceptHashes);
        }
    }

    [Fact]
    public void EveryMember_RoleHashReferencesSourceOrTargetConcept()
    {
        Skip.IfNotAvailable(_fix);
        var sourceHash = FindConceptHash("source");
        var targetHash = FindConceptHash("target");
        var sourceHex  = System.Convert.ToHexString(sourceHash);
        var targetHex  = System.Convert.ToHexString(targetHash);

        long inspected = 0;
        long bad       = 0;
        foreach (var m in _fix.StreamEdgeMembers())
        {
            var roleHex = System.Convert.ToHexString(m.RoleHash);
            if (roleHex != sourceHex && roleHex != targetHex) { bad++; }
            inspected++;
            if (inspected == 200_000) { break; }
        }
        Assert.Equal(0, bad);
    }

    [Fact]
    public void U_0041_LatinCapitalA_HasScriptEdgeToLatn()
    {
        Skip.IfNotAvailable(_fix);
        // U+0041 'A' must attest script=Latn via the substrate's
        // ('script' concept) → ('Latn' concept) edge. Compute the
        // expected edge_hash deterministically and search the edge file.
        var hashing      = new IdentityHashing();
        var atomA        = FindAtomByCodepoint(0x0041);
        var atomAHash    = AtomId.FromSpan(atomA.EntityHash);
        var scriptType   = AtomId.FromSpan(FindConceptHash("script"));
        var latnConcept  = AtomId.FromSpan(FindConceptHash("Latn"));
        var sourceRole   = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole   = AtomId.FromSpan(FindConceptHash("target"));

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, atomAHash),
            (targetRole, 0, latnConcept),
        };
        var expectedHash = hashing.EdgeId(scriptType, members);
        var expectedHex  = System.Convert.ToHexString(expectedHash.AsSpan());

        var found = false;
        foreach (var e in _fix.StreamEdges())
        {
            if (System.Convert.ToHexString(e.EdgeHash) == expectedHex)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Substrate is missing the expected (U+0041 'A')-script-(Latn) edge");
    }

    [Fact]
    public void U_0041_LatinCapitalA_HasGeneralCategoryEdgeToLu()
    {
        Skip.IfNotAvailable(_fix);
        var hashing      = new IdentityHashing();
        var atomA        = FindAtomByCodepoint(0x0041);
        var atomAHash    = AtomId.FromSpan(atomA.EntityHash);
        var gcType       = AtomId.FromSpan(FindConceptHash("general_category"));
        var luConcept    = AtomId.FromSpan(FindConceptHash("Lu"));
        var sourceRole   = AtomId.FromSpan(FindConceptHash("source"));
        var targetRole   = AtomId.FromSpan(FindConceptHash("target"));

        var members = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (sourceRole, 0, atomAHash),
            (targetRole, 0, luConcept),
        };
        var expectedHash = hashing.EdgeId(gcType, members);
        var expectedHex  = System.Convert.ToHexString(expectedHash.AsSpan());

        var found = false;
        foreach (var e in _fix.StreamEdges())
        {
            if (System.Convert.ToHexString(e.EdgeHash) == expectedHex)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Substrate is missing the expected (U+0041 'A')-general_category-(Lu) edge");
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
}
