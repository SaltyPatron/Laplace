using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Run-scoped record of content roots whose tier ladder is proven durably present in
/// the target substrate. Consulted by <see cref="ContentTierSpine.TryStageIntoBuilder"/>
/// to answer "has this surface's ladder already been deposited?" BEFORE the ladder is
/// derived.
///
/// The defect this closes: <c>content_witness_batch_add</c> builds the entire ladder
/// (decompose to codepoints, walk the grapheme ladder, Merkle-hash every node) and only
/// THEN asks whether it has been seen — <c>content_witness_batch.c:364</c>, and it asks
/// an <c>intent_stage_t</c>, whose seen-set lives exactly one record batch. Across a
/// corpus the same surface is therefore re-derived AND RE-EMITTED once per batch it
/// appears in. The re-emitted nodes already exist, so they arrive at the working-set
/// apply as PRESENT rows and pour into the merge lane instead of the COPY lane.
///
/// MEASURED on the 2026-07-26 OMW seed (1226 language files over a shared Latin
/// alphabet — a near-total repeat class): merge applies of 149,247 and 242,563 present
/// rows, PRECEDES carrying 4,955,844 observations across 785,637 rows with a single
/// codepoint-adjacency edge at observation_count 290,320, and PRECEDES alone accounting
/// for more than half of all attestation UPDATEs on the run.
///
/// Those counts were never testimony. <c>intent_stage_witness_seen</c> already suppresses
/// the second emission WITHIN a batch, so the recorded count is a function of where the
/// batch boundaries fell — not of the corpus. Re-deriving a surface's ladder observes
/// nothing new: the decomposition of content into its codepoints is identity, owned by
/// the spine, not evidence about the world. Attest each fact once, at the tier and
/// provenance the source asserts it.
///
/// Membership must have NO false positives — a wrongly-skipped ladder is a dropped
/// entity, not a slow one. Ids enter only from <c>presentEntities</c>: probed present in
/// the target, or written by an apply of this run that has COMMITTED. A miss is always
/// safe and merely costs the derivation that happens today.
///
/// Root presence proves ladder presence — the same premise
/// <c>merkle_dedup_trunk_shortcircuit</c> already runs on: a present trunk short-circuits
/// its whole subtree. This ledger reaches that conclusion one step earlier, before the
/// subtree is built.
///
/// <see cref="End"/> disarms skips but KEEPS membership so a warm re-ingest of the
/// same source (new bulk bracket, same process) does not re-derive every surface.
/// <see cref="Reset"/> clears membership — call on source change or DB recreate so a
/// later source cannot inherit another source's skip set (provenance).
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
    /// Disarms skips. Membership is retained for warm re-ingest of the same source —
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
    /// True iff at least one root is recorded. Armed-but-empty is pure cost at the
    /// staging site — the 2026-08-06 full-file Wiktionary run paid a second, globally
    /// serialized derivation per surface for a membership test that could never pass
    /// (the fill gate never admitted its 738k-distinct working sets). Callers must
    /// check this before probing membership. The armed-empty staging path may still
    /// compute its memo key so the first post-commit recurrence can skip derivation.
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
