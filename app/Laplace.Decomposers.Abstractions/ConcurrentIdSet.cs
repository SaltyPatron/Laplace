using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Concurrent first-sight gate over content-addressed ids. The run-scoped
/// counterpart to a per-batch <see cref="HashSet{T}"/>: parallel decomposer
/// producers share one of these so run-scoped emissions (e.g. a dynamic
/// kind's IS_A taxonomy testimony — ONE witness statement per run) happen
/// exactly once across all workers.
/// </summary>
public sealed class ConcurrentIdSet
{
    private readonly ConcurrentDictionary<Hash128, byte> _ids = new();

    /// <summary>True iff <paramref name="id"/> was NOT seen before (first sight wins).</summary>
    public bool Add(Hash128 id) => _ids.TryAdd(id, 0);
}
