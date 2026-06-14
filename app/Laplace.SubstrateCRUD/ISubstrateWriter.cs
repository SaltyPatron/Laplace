using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public interface ISubstrateWriter
{
    Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default);

    async Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        int ea = 0, ei = 0, pa = 0, pi = 0, aa = 0, ai = 0, rt = 0;
        var wall = TimeSpan.Zero;
        bool allShort = changes.Count > 0;
        foreach (var change in changes)
        {
            var r = await ApplyAsync(change, ct);
            ea += r.EntitiesAttempted;      ei += r.EntitiesInserted;
            pa += r.PhysicalitiesAttempted; pi += r.PhysicalitiesInserted;
            aa += r.AttestationsAttempted;  ai += r.AttestationsInserted;
            rt += r.RoundTrips;             wall += r.WallClock;
            allShort &= r.TrunkShortcircuitHit;
        }
        return new ApplyResult(ea, ei, pa, pi, aa, ai, rt, wall, allShort);
    }

    /// <summary>
    /// Append-only bulk commit: stage rows for <paramref name="sourceId"/> without touching the
    /// live tables, to be merged in one set operation by <see cref="FinalizeSourceAsync"/>.
    /// Default falls back to the immediate <see cref="ApplyManyAsync"/> path (writers that have no
    /// staging just apply now), so callers can always use append/finalize uniformly.
    /// </summary>
    Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => ApplyManyAsync(changes, ct);

    /// <summary>
    /// Merge a source's staged rows into the live tables in one pass (dedup entities/physicalities,
    /// fold attestation observation counts). Default is a no-op for writers that apply immediately.
    /// </summary>
    Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => Task.FromResult((0, 0, 0));
}
