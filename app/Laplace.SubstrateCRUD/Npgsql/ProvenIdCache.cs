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

    /// <summary>
    /// Atomically claim an id for staging. Returns true if THIS caller won the claim (the id was not
    /// already present) and should stage it; false if another worker (or a prior committed batch)
    /// already owns it and this caller must skip. Lets parallel commit workers that share content-
    /// addressed keys (e.g. OMW's ILI synset anchors referenced by 1226 language files) insert each
    /// entity exactly once instead of all racing the same ON CONFLICT insert and serializing on
    /// transactionid locks. Disabled cache => always true (no dedup). At cap => dedup against existing
    /// without growing. Pair with <see cref="Remove"/> on rollback so a retry can re-stage.
    /// </summary>
    public bool TryClaim(Hash128 id)
    {
        if (_set is not { } s) return true;
        if (s.Count >= _cap) return !s.ContainsKey(id);
        return s.TryAdd(id, 0);
    }

    public void Remove(Hash128 id)
    {
        if (_set is { } s) s.TryRemove(id, out _);
    }
}
