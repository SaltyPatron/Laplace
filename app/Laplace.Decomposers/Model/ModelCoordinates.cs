using System.Globalization;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// A circuit COORDINATE is shared content across every ingested model — the board
// square of the model modality. Its identity composes from entities that already
// exist substrate-wide by content: a plane anchor (closed 5-item vocabulary, the
// chess-piece pattern) and numeric Scalar entities (Blake3 of the digit string —
// the recipe-scalar law, so "5" here IS every other "5" in the substrate). The
// model NEVER enters any id: it is the attestation source. Two checkpoints both
// witness (token, APPEARS_IN, L5.H7-attention) into the same consensus cell, so
// Glicko rates cross-model structural convergence while evidence rows keep
// per-model provenance via source.
public static class ModelCoordinates
{
    public static readonly Hash128 PlaneTypeId = EntityTypeRegistry.Id("Model_Plane");
    public static readonly Hash128 CoordinateTypeId = EntityTypeRegistry.Id("Model_Circuit");
    public static readonly Hash128 ScalarTypeId = EntityTypeRegistry.Scalar;

    public static readonly Hash128 AppearsInTypeId = RelationTypeRegistry.RelationTypeId("APPEARS_IN");
    public static readonly Hash128 ContainsTypeId = RelationTypeRegistry.RelationTypeId("CONTAINS");
    public static readonly Hash128 PrecedesTypeId = RelationTypeRegistry.RelationTypeId("PRECEDES");

    public static Hash128 PlaneAnchor(string plane) =>
        Hash128.OfCanonical($"substrate/entity/model/plane/{plane}/v1");

    // Scalar identity = the TEXT CONTENT law (root id of the digit string), never a
    // raw byte hash: "14" here must BE "14" everywhere in the substrate, or numeric
    // content fragments (word_id('14') resolving to a different entity than the
    // deposited scalar is exactly the doc-16 identity-fragmentation class). Single
    // digits collapse to their codepoint ids (tier-floor law), so both laws coincide
    // there. Requires the codepoint perfcache, like every content decomposition.
    public static Hash128 ScalarId(int value) =>
        ScalarId(value.ToString(CultureInfo.InvariantCulture));

    public static Hash128 ScalarId(string value)
    {
        if (!LlamaTokenizerParser.TryDecomposeRoot(Encoding.UTF8.GetBytes(value),
                out var id, out _, out _, out _, out _, out _))
            throw new InvalidOperationException($"scalar '{value}' failed content decomposition");
        return id;
    }

    // Ordered constituents of a coordinate: plane, then layer scalar, then head
    // scalar — omitting whichever the circuit does not have. A single-constituent
    // composition IS its constituent (the "cat is the sentence" collapse law), so
    // an embed-plane coordinate is the plane anchor itself.
    public static Hash128[] Constituents(CircuitDescriptor c)
    {
        if (c.Layer < 0) return [PlaneAnchor(c.Plane)];
        return c.Head < 0
            ? [PlaneAnchor(c.Plane), ScalarId(c.Layer)]
            : [PlaneAnchor(c.Plane), ScalarId(c.Layer), ScalarId(c.Head)];
    }

    public static Hash128 CoordinateId(CircuitDescriptor c)
    {
        var children = Constituents(c);
        return children.Length == 1 ? children[0] : Hash128.Merkle(EntityTier.Word, children);
    }

    // Deposits the coordinate entity, its constituents, and the text-lane
    // structure law verbatim: CONTAINS for membership, PRECEDES for order,
    // both scoped by context = the coordinate. Insert-if-absent everywhere, so
    // every model re-witnesses the same rows. Call ONCE per coordinate per
    // change stream (attestation rows are observation-counted on merge).
    public static void StageCoordinate(SubstrateChangeBuilder b, CircuitDescriptor c, Hash128 sourceId)
    {
        var children = Constituents(c);
        var coord = children.Length == 1 ? children[0] : Hash128.Merkle(EntityTier.Word, children);

        b.AddEntity(children[0], EntityTier.Word, PlaneTypeId, firstObservedBy: sourceId);
        for (int i = 1; i < children.Length; i++)
            b.AddEntity(children[i], EntityTier.Word, ScalarTypeId, firstObservedBy: sourceId);

        if (children.Length == 1) return;

        b.AddEntity(coord, EntityTier.Word, CoordinateTypeId, firstObservedBy: sourceId);
        foreach (var child in children)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                coord, ContainsTypeId, child, sourceId, coord, 1.0));
        for (int i = 1; i < children.Length; i++)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                children[i - 1], PrecedesTypeId, children[i], sourceId, coord, 1.0));
    }
}
