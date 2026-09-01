using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Process-local invalidation epoch for persisted chess transition and position-atom
/// evidence. Completing a playing advances the epoch so the immutable board-evaluation
/// snapshot refreshes between searches, never halfway through one search.
/// </summary>
internal static class ChessTransitionObservations
{
    private static long _epoch;

    public static long Epoch => Volatile.Read(ref _epoch);

    public static void MarkObserved(IEnumerable<Hash128> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Interlocked.Increment(ref _epoch);
    }
}
