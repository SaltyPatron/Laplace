using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Native content witness → <see cref="IntentStage"/> (no C# row materialization).</summary>
public static class ContentWitnessBatch
{
    public static Hash128? Emit(SubstrateChangeBuilder builder, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return TryAppendToBuilder(builder, Encoding.UTF8.GetBytes(surface), sourceId, out var root)
            ? root : null;
    }

    public static Hash128? Emit(SubstrateChangeBuilder builder, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;
        return TryAppendToBuilder(builder, canonical, sourceId, out var root) ? root : null;
    }

    public static bool TryAddToIntentStage(
        IntentStage stage,
        ReadOnlySpan<byte> canonical,
        Hash128 sourceId,
        out Hash128 rootId) =>
        stage.TryAddContentWitness(canonical, sourceId, out rootId);

    public static bool TryAppendToBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> canonical,
        Hash128 sourceId,
        out Hash128 rootId)
    {
        using var stage = IntentStage.New(Math.Max(32, canonical.Length));
        if (!TryAddToIntentStage(stage, canonical, sourceId, out rootId))
            return false;
        builder.AddIntentStage(stage);
        return true;
    }
}
