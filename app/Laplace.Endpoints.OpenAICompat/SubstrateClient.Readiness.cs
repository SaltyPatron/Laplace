using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    // Observes the chess position and transition perfcaches in this process; the
    // PostgreSQL T0 probe is separate. Chess readiness does not gate substrate readiness.
    internal static ChessPerfcacheObservation ObserveChessPerfcache(Action? initialize = null)
    {
        bool initialized = false;
        string? failure = null;
        ChessPositionPerfcacheObservation? position = null;
        ChessTransitionPerfcacheObservation? transition = null;
        try
        {
            (initialize ?? ChessCompose.InitializePerfcaches)();
            initialized = true;
        }
        catch (Exception ex) when (IsChessObservationFailure(ex))
        {
            failure = ex.GetType().Name;
        }
        try
        {
            var value = ChessPositionFloor.Observe();
            position = new(value.IsLoaded, value.RecordCount, value.LookupHits, value.LookupMisses);
        }
        catch (Exception ex) when (IsChessObservationFailure(ex))
        {
            failure ??= ex.GetType().Name;
        }
        // The transition map is observed even when the position map failed; only
        // exception type names are reported.
        var transitions = ChessTransitionFloor.Observe();
        transition = new(transitions.IsLoaded, transitions.RecordCount, transitions.NovelCount,
            transitions.PersistentHits, transitions.NovelHits, transitions.LookupMisses);
        return new(Environment.ProcessId, DateTimeOffset.UtcNow,
            "process-lifetime completed managed lookups; counters include earlier mappings",
            initialized, position, transition, failure);
    }

    private static bool IsChessObservationFailure(Exception ex) => ex is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        or InvalidOperationException or IOException or UnauthorizedAccessException;
}
