using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;








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
