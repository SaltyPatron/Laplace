using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public sealed class ConcurrentIdSet
{
    private readonly ConcurrentDictionary<Hash128, byte> _ids = new();

    public bool Add(Hash128 id) => _ids.TryAdd(id, 0);
}
