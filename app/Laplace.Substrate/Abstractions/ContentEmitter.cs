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

    public static Hash128? RootId(string surface) => ContentTierSpine.ResolveRoot(surface);

    public static Hash128? RootId(ReadOnlySpan<byte> canonical) => ContentTierSpine.ResolveRoot(canonical);
}
