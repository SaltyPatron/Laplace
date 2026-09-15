using Laplace.Api.Contracts;
using Laplace.Chess.Service;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed partial class SubstrateClient
{
    // This observes this API process. PostgreSQL's T0 probe is a separate readiness
    // condition. An optional empty transition catalog makes chess readiness false;
    // it does not change the general substrate readiness condition.
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
        // Each map has independent state; retain transition evidence even if the
        // position native library is unavailable. Never serialize exception messages.
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
