using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Converts a hash-composed <see cref="TierTree"/> (after
/// <see cref="HashComposer.Run"/> has filled all node IDs) into
/// <see cref="EntityRow"/> + <see cref="PhysicalityRow"/> collections.
///
/// <para>Deduplication layers applied:</para>
/// <list type="number">
///   <item>Level 1 — within-intent: <c>HashSet&lt;Hash128&gt;</c> prevents
///     duplicate entity rows when two tree paths reference the same entity
///     (e.g. a repeated word appearing as a child of two different sentence
///     nodes in the same intent).</item>
///   <item>Level 2 — cross-intent bitmap: if <paramref name="existingBitmap"/>
///     is non-null, <see cref="MerkleDedup.TrunkShortcircuit"/> prunes
///     subtrees already present in the substrate — relying on the
///     SubstrateCRUD invariant that parent-presence implies
///     descendant-presence. Pass <c>null</c> to skip; rely on DB-level
///     <c>ON CONFLICT DO NOTHING</c> as the backstop.</item>
/// </list>
///
/// <para>Trajectories are RLE-compressed via
/// <see cref="Trajectory.BuildRle"/> — one vertex per run of consecutive
/// identical child IDs. <c>NConstituents</c> in each physicality row
/// records the original uncompressed child count.</para>
///
/// <para>Caller must have called <see cref="TierTree.FinalizeParents"/> and
/// <see cref="HashComposer.Run"/> before constructing this builder.</para>
/// </summary>
public sealed class TextEntityBuilder
{
    // Canonical substrate type IDs for each text tier — same formulas used
    // by UnicodeDecomposer (T0) and bootstrapped by SubstrateCanonical.
    public static readonly Hash128 CodepointTypeId = Hash128.OfCanonical("substrate/type/Codepoint/v1");
    public static readonly Hash128 GraphemeTypeId  = Hash128.OfCanonical("substrate/type/Grapheme/v1");
    public static readonly Hash128 WordTypeId      = Hash128.OfCanonical("substrate/type/Word/v1");
    public static readonly Hash128 SentenceTypeId  = Hash128.OfCanonical("substrate/type/Sentence/v1");
    public static readonly Hash128 DocumentTypeId  = Hash128.OfCanonical("substrate/type/Document/v1");

    private readonly TierTree  _tree;
    private readonly Hash128   _sourceId;
    private readonly byte[]?   _existingBitmap;
    private readonly HashSet<Hash128> _emittedIds = new();

    private readonly ImmutableArray<EntityRow>.Builder       _entities;
    private readonly ImmutableArray<PhysicalityRow>.Builder  _physicalities;

    public TextEntityBuilder(TierTree tree, Hash128 sourceId, byte[]? existingBitmap = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree           = tree;
        _sourceId       = sourceId;
        _existingBitmap = existingBitmap;
        int n           = tree.NodeCount;
        _entities       = ImmutableArray.CreateBuilder<EntityRow>(n);
        _physicalities  = ImmutableArray.CreateBuilder<PhysicalityRow>(n);
    }

    /// <summary>Emit entity + physicality rows for all novel nodes in the
    /// tree. Call once; subsequent calls add duplicates.</summary>
    public (ImmutableArray<EntityRow> Entities, ImmutableArray<PhysicalityRow> Physicalities) Build()
    {
        int nodeCount = _tree.NodeCount;
        if (nodeCount == 0)
            return (_entities.ToImmutable(), _physicalities.ToImmutable());

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        if (_existingBitmap is { Length: > 0 })
        {
            var novelIdx = new uint[nodeCount];
            int novelCount = MerkleDedup.TrunkShortcircuit(_tree, _existingBitmap, novelIdx);
            for (int i = 0; i < novelCount; i++)
                EmitNode(novelIdx[i], nowUs);
        }
        else
        {
            for (uint idx = 0; idx < (uint)nodeCount; idx++)
                EmitNode(idx, nowUs);
        }

        return (_entities.ToImmutable(), _physicalities.ToImmutable());
    }

    private void EmitNode(uint idx, long nowUs)
    {
        var node = _tree.GetNode(idx);

        // Level 1 dedup: skip if already emitted within this intent
        if (!_emittedIds.Add(node.Id)) return;

        var typeId = TierTypeId(node.Tier);
        _entities.Add(new EntityRow(node.Id, node.Tier, typeId, _sourceId));

        double[]? trajectoryXyzm = null;
        int nConstituents = 0;

        if (node.ChildCount > 0)
        {
            var childIds = new Hash128[node.ChildCount];
            for (uint ci = 0; ci < node.ChildCount; ci++)
            {
                var child = _tree.GetNode(node.FirstChildIdx + ci);
                childIds[ci] = child.Id;
            }
            trajectoryXyzm = Trajectory.BuildRle(childIds);
            nConstituents  = (int)node.ChildCount;
        }

        double cx, cy, cz, cm;
        unsafe { cx = node.Coord[0]; cy = node.Coord[1]; cz = node.Coord[2]; cm = node.Coord[3]; }

        var physId = PhysicalityId.Compute(
            node.Id, _sourceId, PhysicalityKind.Content,
            cx, cy, cz, cm,
            trajectoryXyzm ?? ReadOnlySpan<double>.Empty);

        _physicalities.Add(new PhysicalityRow(
            Id:                physId,
            EntityId:          node.Id,
            SourceId:          _sourceId,
            Kind:              PhysicalityKind.Content,
            CoordX:            cx,
            CoordY:            cy,
            CoordZ:            cz,
            CoordM:            cm,
            HilbertIndex:      node.Hilbert,
            TrajectoryXyzm:    trajectoryXyzm,
            NConstituents:     nConstituents,
            AlignmentResidual: null,
            SourceDim:         null,
            ObservedAtUnixUs:  nowUs));
    }

    private static Hash128 TierTypeId(byte tier) => tier switch
    {
        0 => CodepointTypeId,
        1 => GraphemeTypeId,
        2 => WordTypeId,
        3 => SentenceTypeId,
        _ => DocumentTypeId,
    };
}
