using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Run-scoped record of roots whose entity ladder is proven present in the
/// target substrate. Membership contains only positive database/readback or
/// committed-apply evidence and is bounded by the shared cache envelope.
///
/// This is an entity-presence cache, not a source-unit observation receipt.
/// A known root cannot suppress a source's physicality observation. Native
/// emission preserves the observed bodies; descriptor admission reuses exact
/// form identities and excludes replay of the same source-unit association.
///
/// End disarms lookups while retaining membership for a warm run. Reset clears
/// membership on source change or database recreation. Missing membership may
/// cost another indexed presence probe; it must never alter content identity.
/// </summary>
public static class ContentLadderLedger
{
    /// <summary>
    /// Bounded by DISTINCT content roots deposited on a run. Capacity is supplied by
    /// the generic apply resource plan from its cache byte envelope; past it the ledger
    /// stops accreting and callers fall back to deriving, which loses reuse and never
    /// correctness.
    /// </summary>
    private static ConcurrentDictionary<Hash128, bool>? _persisted;
    private static int _count;
    private static int _armed;
    private static int _capacity;

    /// <summary>Arms the ledger for a bulk run. Keeps any membership left by a prior End.</summary>
    public static void Begin(int? capacity = null)
    {
        Volatile.Write(ref _capacity, Math.Max(1, capacity
            ?? IngestSizing.ResolveApplyIo(IngestTopology.Current.ApplyPartitions).LadderCacheIds));
        if (_persisted is null)
        {
            _persisted = new ConcurrentDictionary<Hash128, bool>();
            Volatile.Write(ref _count, 0);
        }
        Volatile.Write(ref _armed, 1);
    }

    /// <summary>
    /// Disarms presence lookups. Membership is retained for warm re-ingest of the same source —
    /// <see cref="Reset"/> is what forgets.
    /// </summary>
    public static void End() => Volatile.Write(ref _armed, 0);

    /// <summary>Forget every root. Source change / DB recreate / test isolation.</summary>
    public static void Reset()
    {
        Volatile.Write(ref _armed, 0);
        _persisted = null;
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _capacity, 0);
    }

    /// <summary>True while a bulk run has armed the ledger. Outside one, never skip.</summary>
    public static bool Armed => Volatile.Read(ref _armed) != 0;

    /// <summary>
    /// True iff at least one positively proven root is recorded. This does not
    /// prove that an independent source-unit observation has been admitted.
    /// </summary>
    public static bool HasEntries => Volatile.Read(ref _count) > 0;

    /// <summary>True iff this root's ladder is proven present in the target substrate.</summary>
    public static bool IsPersisted(Hash128 root)
    {
        if (!Armed) return false;
        var map = _persisted;
        return map is not null && map.ContainsKey(root);
    }

    /// <summary>
    /// Records roots proven present. Callers MUST only pass ids that are durably in the
    /// target — probed present, or written by an apply that has committed.
    /// </summary>
    public static void MarkPersisted(IEnumerable<Hash128> roots)
    {
        if (!Armed) return;
        var map = _persisted;
        if (map is null) return;
        foreach (var id in roots)
        {
            if (!TryAddBounded(map, id)) return;
        }
    }

    /// <summary>
    /// Allocation-free working-set feed: records ids selected by the caller's
    /// first-occurrence index without manufacturing another Hash128 array.
    /// </summary>
    public static void MarkPersisted(
        IReadOnlyList<Hash128> ids, IReadOnlyList<int> indices)
    {
        if (!Armed) return;
        var map = _persisted;
        if (map is null) return;
        for (int i = 0; i < indices.Count; i++)
        {
            if (!TryAddBounded(map, ids[indices[i]])) return;
        }
    }

    private static bool TryAddBounded(
        ConcurrentDictionary<Hash128, bool> map, Hash128 id)
    {
        int capacity = Volatile.Read(ref _capacity);
        if (Volatile.Read(ref _count) >= capacity) return false;
        if (!map.TryAdd(id, true)) return true;
        int after = Interlocked.Increment(ref _count);
        if (after <= capacity) return true;
        if (map.TryRemove(id, out _)) Interlocked.Decrement(ref _count);
        return false;
    }
}
