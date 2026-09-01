using System.Collections.Concurrent;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Process-local invalidation epoch for persisted chess transition and position-atom
/// evidence. Completing a playing advances the epoch so the immutable board-evaluation
/// snapshot refreshes between searches, never halfway through one search.
/// </summary>
internal static class ChessTransitionObservations
{
    private static readonly ConcurrentDictionary<Hash128, long> PositionVersions = new();
    private static readonly ConcurrentDictionary<Hash128, long> MoveVersions = new();
    private static long _epoch;

    public static long Epoch => Volatile.Read(ref _epoch);

    /// <summary>
    /// Evidence generation for exactly one decision frontier. Position-transition testimony
    /// changes only the roots a completed game traversed, while reusable move testimony changes
    /// only the typed moves it contained. Keeping those generations separate lets concurrent
    /// games reuse the already-batched frontier read without hiding an online observation.
    /// </summary>
    public static long Version(Hash128 position, IReadOnlyList<Hash128> moveIds)
    {
        long version = PositionVersions.TryGetValue(position, out var pv) ? pv : 0;
        for (int i = 0; i < moveIds.Count; i++)
            if (MoveVersions.TryGetValue(moveIds[i], out var mv) && mv > version)
                version = mv;
        return version;
    }

    public static void MarkObserved(IEnumerable<Hash128> positions)
        => MarkObserved(positions, Array.Empty<Hash128>());

    public static void MarkObserved(
        IEnumerable<Hash128> positions, IEnumerable<Hash128> moves)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(moves);
        long version = Interlocked.Increment(ref _epoch);
        foreach (var position in positions)
            PositionVersions.AddOrUpdate(position, version, (_, _) => version);
        foreach (var move in moves)
            MoveVersions.AddOrUpdate(move, version, (_, _) => version);
    }
}
