namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Physicality closure of the entities one writer wrote: every entity it admitted must be
/// realized by a physicality it admitted with it. Each writer accumulates it from rows it
/// already holds; a source run reads its own writer's once at the end instead of scanning
/// every entity the source owns.
/// </summary>
public interface IPhysicalityClosure
{
    /// <summary>Returns and clears the counts accumulated since the last call.</summary>
    (long Written, long Unplaced) TakePhysicalityClosure();
}

internal sealed class PhysicalityClosureLedger
{
    private long _written, _unplaced;

    internal void Record(long written, long unplaced)
    {
        Interlocked.Add(ref _written, written);
        Interlocked.Add(ref _unplaced, unplaced);
    }

    internal (long Written, long Unplaced) Take() =>
        (Interlocked.Exchange(ref _written, 0), Interlocked.Exchange(ref _unplaced, 0));
}
