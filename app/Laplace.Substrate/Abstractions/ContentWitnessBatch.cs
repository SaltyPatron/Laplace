using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Back-compat surface over <see cref="ContentTierSpine"/>. New code should call
/// the spine directly; this type remains so existing witnesses compile unchanged.
/// </summary>
public static class ContentWitnessBatch
{
    public static Hash128? Emit(SubstrateChangeBuilder builder, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return ContentTierSpine.TryStageIntoBuilder(builder, Encoding.UTF8.GetBytes(surface), sourceId, out var root)
            ? root : null;
    }

    public static Hash128? Emit(SubstrateChangeBuilder builder, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;
        return ContentTierSpine.TryStageIntoBuilder(builder, canonical, sourceId, out var root) ? root : null;
    }

    public static Hash128? RootId(ReadOnlySpan<byte> canonical) => ContentTierSpine.ResolveRoot(canonical);

    public static Hash128? RootId(string surface) => ContentTierSpine.ResolveRoot(surface);

    public static bool TryAddToIntentStage(
        IntentStage stage, ReadOnlySpan<byte> canonical, Hash128 sourceId, out Hash128 rootId) =>
        stage.TryAddContentWitness(canonical, sourceId, out rootId);

    public static bool TryAppendToBuilder(
        SubstrateChangeBuilder builder, ReadOnlySpan<byte> canonical, Hash128 sourceId, out Hash128 rootId) =>
        ContentTierSpine.TryStageIntoBuilder(builder, canonical, sourceId, out rootId);

    public static TierTree? BuildTree(ReadOnlySpan<byte> canonical) => ContentTierSpine.BuildTree(canonical);

    public static bool TryEmitTree(
        SubstrateChangeBuilder builder, TierTree tree, Hash128 sourceId, ReadOnlySpan<byte> existingBitmap,
        out Hash128 rootId) =>
        ContentTierSpine.EmitTree(builder, tree, sourceId, existingBitmap, out rootId);

    public static bool TryAppendUnderscoredToBuilder(
        SubstrateChangeBuilder builder, ReadOnlySpan<byte> underscoredUtf8, Hash128 sourceId, out Hash128 rootId) =>
        ContentTierSpine.TryStageUnderscoredIntoBuilder(builder, underscoredUtf8, sourceId, out rootId);
}
