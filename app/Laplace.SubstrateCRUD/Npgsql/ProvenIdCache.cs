using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// A bounded, concurrent set of ids the writer has already proven to exist in the live substrate
/// this process. Membership lets <see cref="IntentPreflight"/> skip the existence round-trip for an
/// id seen before, so re-deposits of overlapping sources amortize the preflight cost. Disabled
/// (<c>LAPLACE_PROVEN_CACHE=0</c>) it is a no-op; capacity-bounded
/// (<c>LAPLACE_PROVEN_CACHE_MAX</c>) it stops growing past the cap rather than unbounded.
/// </summary>
internal sealed class ProvenIdCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, byte>? _set;
    private readonly int _cap;

    public ProvenIdCache(bool enabled, int cap)
    {
        _set = enabled ? new() : null;
        _cap = cap;
    }

    public bool Contains(Hash128 id) => _set is { } s && s.ContainsKey(id);

    public void Add(Hash128 id)
    {
        if (_set is { } s && s.Count < _cap) s.TryAdd(id, 0);
    }

    public void AddRange(IEnumerable<Hash128> ids)
    {
        if (_set is null) return;
        foreach (var id in ids) Add(id);
    }
}
