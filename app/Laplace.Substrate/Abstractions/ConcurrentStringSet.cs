using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Thread-safe string set with <see cref="HashSet{T}"/> semantics, for the canonical-name
/// readback a provider accumulates during compose. Record-aligned segments of one file
/// compose concurrently against the same provider instance (see MonolithSegmenter), so
/// this state is shared across threads. The accumulation is a set union, so a concurrent
/// first-wins Add gives exactly the serial result.
/// </summary>
public sealed class ConcurrentStringSet : IReadOnlyCollection<string>
{
    private readonly ConcurrentDictionary<string, byte> _inner;

    public ConcurrentStringSet(IEqualityComparer<string>? comparer = null)
        => _inner = comparer is null
            ? new ConcurrentDictionary<string, byte>()
            : new ConcurrentDictionary<string, byte>(comparer);

    /// <summary>Adds <paramref name="value"/>; returns true if newly added (HashSet.Add semantics).</summary>
    public bool Add(string value) => _inner.TryAdd(value, 0);

    public bool Contains(string value) => _inner.ContainsKey(value);

    public int Count => _inner.Count;

    public IEnumerator<string> GetEnumerator() => _inner.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
