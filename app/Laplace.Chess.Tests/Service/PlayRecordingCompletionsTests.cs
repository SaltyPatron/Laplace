using Xunit;

namespace Laplace.Chess.Service.Tests;

// A game-ending move replies first and records after, through one completion per game.
public sealed class PlayRecordingCompletionsTests
{
    [Fact]
    public async Task TheReceiptIsWrittenBeforeStartReturnsAndTheRecordingAfter()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var steps = new List<string>();
        Assert.True(completions.Start(id, () => { steps.Add("receipt"); return 1; },
            async _ => { await release.Task; lock (steps) steps.Add("recorded"); }));
        lock (steps) Assert.Equal(["receipt"], steps);   // the reply can go now
        var running = completions.Running(id);
        Assert.NotNull(running);
        release.SetResult();
        await running!;
        lock (steps) Assert.Equal(["receipt", "recorded"], steps);
        Assert.Null(completions.Running(id));
    }

    [Fact]
    public async Task ASecondStartWaitsOnTheFirstInsteadOfRecordingAgain()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        int recordings = 0;
        Assert.True(completions.Start(id, () => 0, async _ => { await release.Task; Interlocked.Increment(ref recordings); }));
        Assert.False(completions.Start(id, () => 0, _ => { Interlocked.Increment(ref recordings); return Task.CompletedTask; }));
        release.SetResult();
        await completions.Running(id)!;
        Assert.Equal(1, recordings);
    }

    [Fact]
    public async Task ARecordingFailureReachesEveryWaiterAndIsReported()
    {
        var completions = new PlayRecordingCompletions();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource();
        Exception? reported = null;
        completions.Start(id, () => 0, async _ => { await release.Task; throw new IOException("substrate unavailable"); },
            (failure, _) => reported = failure);
        var waiter = completions.Running(id)!;   // what finish and retry await
        release.SetResult();
        var seen = await Assert.ThrowsAsync<IOException>(() => waiter);
        Assert.Equal("substrate unavailable", seen.Message);
        Assert.IsType<IOException>(reported);
        Assert.Null(completions.Running(id));    // a later retry starts afresh
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
