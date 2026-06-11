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
        // All content witnesses in one change share the builder's coalesced stage:
        // the writer stages it once (one COPY + one INSERT per table) instead of
        // paying the pair per witness. A failed witness leaves the shared stage
        // intact with its prior successful rows.
        return TryAddToIntentStage(builder.ContentStage, canonical, sourceId, out rootId);
    }
}
