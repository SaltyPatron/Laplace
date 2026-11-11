using System.Collections.Concurrent;

namespace Laplace.Chess.Service;

/// <summary>
/// At most one recording per game, started after a move's reply. <see cref="Start"/> runs
/// <c>begin</c> (validation and the durable receipt) before it returns, so the reply never
/// outruns the receipt, then runs <c>complete</c> on the thread pool. Finish and retry for
/// the same game wait on that completion instead of recording beside it, and see its failure
/// as they would have had they run it themselves.
/// </summary>
internal sealed class PlayRecordingCompletions
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public Task? Running(Guid id) => _running.TryGetValue(id, out var task) ? task : null;

    public Task All() => Task.WhenAll(_running.Values);

    /// <returns>False when a completion for this game is already running (nothing new starts).</returns>
    public bool Start<TState>(Guid id, Func<TState> begin, Func<TState, Task> complete, Action<Exception?, long>? done = null)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(id, finished.Task)) return false;
        TState state;
        try { state = begin(); }
        catch (Exception refused)
        {
            _running.TryRemove(id, out _);
            finished.TrySetException(refused);
            throw;
        }
        _ = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await complete(state);
                _running.TryRemove(id, out _);
                done?.Invoke(null, clock.ElapsedMilliseconds);
                finished.TrySetResult();
            }
            catch (Exception failure)
            {
                _running.TryRemove(id, out _);
                done?.Invoke(failure, clock.ElapsedMilliseconds);
                finished.TrySetException(failure);
            }
        });
        return true;
    }
}
