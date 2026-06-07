using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public interface ISubstrateReader
{
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default);

    Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default);
}
