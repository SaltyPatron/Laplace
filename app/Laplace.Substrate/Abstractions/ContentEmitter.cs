using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class ContentEmitter
{
    public static Hash128? Emit(SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return ContentTierSpine.TryStageIntoBuilder(b, Encoding.UTF8.GetBytes(surface), sourceId, out var root)
            ? root : null;
    }

    public static Hash128? Emit(SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;
        return ContentTierSpine.TryStageIntoBuilder(b, canonical, sourceId, out var root) ? root : null;
    }

    /// <summary>
    /// Stage canonical text through the shared content spine and return the exact
    /// natural-root component required by native ordered composition. This is the
    /// common bridge for structures whose identity is made from witnessed content
    /// constituents; callers must not replace it with a formatted-string hash.
    /// </summary>
    public static OrderedCompositionComponent? StageComponent(
        SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return StageComponent(b, Encoding.UTF8.GetBytes(surface), sourceId);
    }

    public static OrderedCompositionComponent? StageComponent(
        SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId) =>
        StageComponent(b, canonical.AsSpan(), sourceId);

    public static unsafe OrderedCompositionComponent? StageComponent(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> canonical, Hash128 sourceId)
    {
        if (canonical.IsEmpty) return null;
        if (!ContentTierSpine.TryStageIntoBuilder(b, canonical, sourceId, out Hash128 root))
            return null;
        using TierTree tree = ContentTierSpine.BuildTree(canonical)
            ?? throw new InvalidOperationException("staged content could not rebuild its tier tree");
        TierNodeView node = tree.GetNode(tree.NaturalUnitIndex());
        if (node.Id != root)
            throw new InvalidOperationException("content root changed between staging and ordered composition");
        return new OrderedCompositionComponent(
            node.Id, node.Tier,
            node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3],
            node.Atom, node.Tier == 0);
    }

    /// <summary>
    /// A named property's value as content: the ordered composition [property, value]
    /// ([hidden_size, 2048], [stop_reason, end_turn]) staged by the native ordered-
    /// composition kernel. A claim states it under one relation (HAS_ATTRIBUTE); the
    /// property is never a relation of its own and never a joined "key=value" string.
    /// </summary>
    public static Hash128? StagePropertyValue(
        SubstrateChangeBuilder b, string property, string value, Hash128 sourceId)
    {
        if (StageComponent(b, property, sourceId) is not { } key
            || StageComponent(b, value, sourceId) is not { } val)
            return null;
        Span<OrderedCompositionResult> result = stackalloc OrderedCompositionResult[1];
        OrderedComposition.StageBatch(b.ContentStage,
            [new OrderedCompositionRequest([key, val], EntityTypeRegistry.PropertyValue, sourceId, 0)],
            result);
        return result[0].Id;
    }

    public static Hash128? RootId(string surface) => ContentTierSpine.ResolveRoot(surface);

    public static Hash128? RootId(ReadOnlySpan<byte> canonical) => ContentTierSpine.ResolveRoot(canonical);
}