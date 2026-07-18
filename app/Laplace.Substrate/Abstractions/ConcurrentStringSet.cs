using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Thread-safe string set with <see cref="HashSet{T}"/>-identical ergonomics, backing the
/// canonical-name readback accumulators decomposers populate during compose.
///
/// Compose is no longer serial: a monolithic file is cut into record-aligned segments that
/// compose concurrently against the SAME decomposer instance (see MonolithSegmenter). Any
/// mutable side-channel a decomposer touches in its compose path is therefore shared across
/// threads. A plain HashSet corrupts under concurrent Add — measured as the ISO639 ingest
/// crash ("A concurrent update was performed on this collection"). This set is the correct
/// data structure for that job: the accumulation is a content-addressed set-union, so a
/// concurrent first-wins Add is exactly the serial semantics with no loss.
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
