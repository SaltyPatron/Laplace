namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Physicality closure of the entities this process wrote, accumulated by each
/// working-set apply from rows it already holds in memory. A source run reads it
/// once at the end instead of scanning every entity the source owns.
/// </summary>
public static class PhysicalityClosureLedger
{
    private static long _written, _unplaced;

    internal static void Record(long written, long unplaced)
    {
        Interlocked.Add(ref _written, written);
        Interlocked.Add(ref _unplaced, unplaced);
    }

    /// <summary>Returns and clears the counts accumulated since the last call.</summary>
    public static (long Written, long Unplaced) Take() =>
        (Interlocked.Exchange(ref _written, 0), Interlocked.Exchange(ref _unplaced, 0));
}
