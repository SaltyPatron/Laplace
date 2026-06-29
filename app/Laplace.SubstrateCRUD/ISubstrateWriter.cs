using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public interface ISubstrateWriter
{
    Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default);

    async Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        int ea = 0, ei = 0, pa = 0, pi = 0, aa = 0, ai = 0, rt = 0;
        long es = 0, ps = 0;
        var wall = TimeSpan.Zero;
        bool allShort = changes.Count > 0;
        foreach (var change in changes)
        {
            var r = await ApplyAsync(change, ct);
            ea += r.EntitiesAttempted;      ei += r.EntitiesInserted;
            pa += r.PhysicalitiesAttempted; pi += r.PhysicalitiesInserted;
            aa += r.AttestationsAttempted;  ai += r.AttestationsInserted;
            rt += r.RoundTrips;             wall += r.WallClock;
            es += r.EntitiesSkippedAtMerge; ps += r.PhysicalitiesSkippedAtMerge;
            allShort &= r.TrunkShortcircuitHit;
        }
        return new ApplyResult(ea, ei, pa, pi, aa, ai, rt, wall, allShort, es, ps);
    }

    
    
    
    
    
    
    Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => ApplyManyAsync(changes, ct);

    
    
    
    
    Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => Task.FromResult((0, 0, 0));
}
