using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Read-only substrate access used by IngestRunner for layer-ordering
/// enforcement + by IDecomposer.InitializeAsync for bootstrap verification.
/// Distinct from the cascade read surface (#226 tracking).
/// </summary>
public interface ISubstrateReader
{
    /// <summary>Returns true iff at least one decomposer with
    /// <see cref="Laplace.Engine.Core.Hash128"/>-encoded source ID has
    /// completed an ingest run at the given layer order. Used by
 /// IngestRunner to enforce layer-ordering prerequisites.
    /// </summary>
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    /// <summary>Returns true iff THIS source has completed an ingest run at
    /// the given layer order (its completion marker is present). Used by
    /// IngestRunner's re-ingest guard: rows are idempotent, testimony is not —
    /// a completed source must not fold its games into consensus twice.
    /// </summary>
    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    /// <summary>Count of entity rows whose <c>type_id</c> equals
    /// <paramref name="typeId"/>. Used in bootstrap idempotency tests.
    /// </summary>
    Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default);

    /// <summary>Returns a packed bitmap (LSB-first per byte) where bit i
    /// is set iff <paramref name="candidates"/>[i] is currently present in
    /// the <c>entities</c> table. Used by the SubstrateCRUD apply path to
    /// drive merkle-dedup filtering — calls the engine-backed
    /// <c>laplace_substrate.entities_exist_bitmap</c> SRF per Story D.3.
    /// </summary>
    Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default);
}
