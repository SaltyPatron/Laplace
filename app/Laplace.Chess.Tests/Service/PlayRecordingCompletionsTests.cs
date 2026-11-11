using Xunit;

namespace Laplace.Chess.Service.Tests;

// A game-ending move replies first and records after, through one completion per game.
// Start is always called off the test thread with a deadline: a Start that waited for the
// recording (the reply waiting on it again) fails on the deadline instead of hanging.
public sealed class PlayRecordingCompletionsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static Task<bool> StartWithin<TState>(PlayRecordingCompletions completions, Guid id,
        Func<TState> begin, Func<TState, Task> complete, Action<Exception?, long>? done = null)
        => Task.Run(() => completions.Start(id, begin, complete, done)).WaitAsync(Deadline);

    [Fact]
    public async Task TheReceiptIsWrittenBeforeStartReturnsAndTheRecordingAfter()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var steps = new List<string>();
        try
        {
            Assert.True(await StartWithin(completions, id, () => { lock (steps) steps.Add("receipt"); return 1; },
                async _ => { await release.Task; lock (steps) steps.Add("recorded"); }));
            lock (steps) Assert.Equal(["receipt"], steps);   // the reply can go now
            var running = completions.Running(id);
            Assert.NotNull(running);
            release.SetResult();
            await running!.WaitAsync(Deadline);
            lock (steps) Assert.Equal(["receipt", "recorded"], steps);
            Assert.Null(completions.Running(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ASecondStartWaitsOnTheFirstInsteadOfRecordingAgain()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        int recordings = 0;
        try
        {
            Assert.True(await StartWithin(completions, id, () => 0, async _ => { await release.Task; Interlocked.Increment(ref recordings); }));
            Assert.False(await StartWithin(completions, id, () => 0, _ => { Interlocked.Increment(ref recordings); return Task.CompletedTask; }));
            var running = completions.Running(id)!;   // taken before release: it may finish inline
            release.SetResult();
            await running.WaitAsync(Deadline);
            Assert.Equal(1, recordings);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ARecordingFailureReachesEveryWaiterAndIsReported()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        Exception? reported = null;
        try
        {
            await StartWithin(completions, id, () => 0, async _ => { await release.Task; throw new IOException("substrate unavailable"); },
                (failure, _) => reported = failure);
            var waiter = completions.Running(id)!;   // what finish and retry await
            release.SetResult();
            var seen = await Assert.ThrowsAsync<IOException>(() => waiter.WaitAsync(Deadline));
            Assert.Equal("substrate unavailable", seen.Message);
            Assert.IsType<IOException>(reported);
            Assert.Null(completions.Running(id));    // a later retry starts afresh
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void ARefusedEndingThrowsToTheCallerAndLeavesNothingRunning()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        bool recorded = false;
        Assert.Throws<InvalidOperationException>(() => completions.Start<int>(id,
            () => throw new InvalidOperationException("not available"), _ => { recorded = true; return Task.CompletedTask; }));
        Assert.False(recorded);
        Assert.Null(completions.Running(id));
    }
}
