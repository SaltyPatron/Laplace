namespace Laplace.Pipeline.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Knowledge-as-intersections primitive. Returns counts and enumerations of
/// substrate entities that contain the target entity in their composition
/// tree.
///
/// Examples the substrate is built to answer:
///   - How many things intersect with the number 3.14?
///   - How many things intersect with the substring 'noreply@'?
///   - How many entities contain the sentence 'The quick brown fox'?
///   - How many images contain the same sky-blue pixel?
///
/// The substrate's knowledge IS the edge graph + intersection counts. This
/// interface is first-class.
/// </summary>
public interface IIntersectionQuery
{
    /// <summary>Count entities whose composition tree contains <paramref name="target"/>.</summary>
    Task<long> CountAsync(AtomId target, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerate up to <paramref name="limit"/> entities that contain
    /// <paramref name="target"/>. Streamed via IAsyncEnumerable to avoid
    /// materializing huge result sets in memory.
    /// </summary>
    IAsyncEnumerable<AtomId> EnumerateAsync(AtomId target, int limit, CancellationToken cancellationToken);
}
