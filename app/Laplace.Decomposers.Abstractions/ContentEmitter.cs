using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;


public static class ContentEmitter
{
    public static Hash128? Emit(SubstrateChangeBuilder b, string surface, Hash128 sourceId) =>
        ContentWitnessBatch.Emit(b, surface, sourceId);

    public static Hash128? Emit(SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId) =>
        ContentWitnessBatch.Emit(b, canonical, sourceId);

    public static Hash128? RootId(string surface) =>
        ContentWitnessBatch.RootId(surface);

    public static Hash128? RootId(ReadOnlySpan<byte> canonical) =>
        ContentWitnessBatch.RootId(canonical);
}
