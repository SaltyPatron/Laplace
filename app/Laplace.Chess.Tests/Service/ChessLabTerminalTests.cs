using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabTerminalTests
{
    [Fact]
    public void Append_NumbersLinesMonotonically_AndRetainsOnlyTheRing()
    {
        var terminal = new ChessLabTerminal(capacity: 3);
        for (int i = 0; i < 5; i++) terminal.Append(ChessLabStream.Stdout, $"line {i}");

        Assert.Equal(5, terminal.Total);
        Assert.Equal(3, terminal.Retained);

        var snapshot = terminal.Snapshot();
        Assert.Equal(["line 2", "line 3", "line 4"], snapshot.Select(l => l.Text));

        // Evicted lines are never renumbered: the gap between 0 and the first retained Seq
        // is exactly the scrollback a late viewer missed, and it can say so.
        Assert.Equal([2L, 3L, 4L], snapshot.Select(l => l.Seq));
    }

    [Fact]
    public void Snapshot_AfterSeq_ReturnsOnlyNewerLines()
    {
        var terminal = new ChessLabTerminal(capacity: 10);
        for (int i = 0; i < 4; i++) terminal.Append(ChessLabStream.Stdout, $"line {i}");

        Assert.Equal(["line 2", "line 3"], terminal.Snapshot(afterSeq: 1).Select(l => l.Text));
        Assert.Empty(terminal.Snapshot(afterSeq: 3));
    }

    [Fact]
    public async Task ReadAsync_ReplaysScrollbackThenFollowsLive()
    {
        var terminal = new ChessLabTerminal(capacity: 10);
        terminal.Append(ChessLabStream.Command, "cutechess-cli -engine ...");
        terminal.Append(ChessLabStream.Stdout, "Started game 1 of 2 (Laplace vs Stockfish)");

        var seen = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var line in terminal.ReadAsync(afterSeq: -1, CancellationToken.None))
            {
                lock (seen) seen.Add(line.Text);
                if (line.Text == "Finished match") return;
            }
        });

        // Subscribed after the fact, and still gets both the backlog and what comes next.
        await WaitFor(() => { lock (seen) return seen.Count >= 2; });
        terminal.Append(ChessLabStream.Uci, "bestmove e2e4", "Laplace", ChessLabDirection.Recv);
        terminal.Append(ChessLabStream.Stdout, "Finished match");
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["cutechess-cli -engine ...", "Started game 1 of 2 (Laplace vs Stockfish)", "bestmove e2e4", "Finished match"],
            seen);
    }

    [Fact]
    public async Task ReadAsync_TwoViewers_EachSeeEveryLine()
    {
        // The structured event channel cannot do this — a second reader steals frames from
        // the first. Two people watching the same match is the normal case, not an edge one.
        var terminal = new ChessLabTerminal(capacity: 10);
        var a = new List<long>();
        var b = new List<long>();

        var readA = Drain(terminal, a);
        var readB = Drain(terminal, b);
        await WaitFor(() => terminal.Total == 0);

        for (int i = 0; i < 3; i++) terminal.Append(ChessLabStream.Stdout, $"line {i}");
        terminal.Complete();
        await Task.WhenAll(readA, readB).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0L, 1L, 2L], a);
        Assert.Equal([0L, 1L, 2L], b);

        static Task Drain(ChessLabTerminal t, List<long> into) => Task.Run(async () =>
        {
            await foreach (var line in t.ReadAsync(-1, CancellationToken.None))
                lock (into) into.Add(line.Seq);
        });
    }

    [Fact]
    public async Task ReadAsync_Resumed_DoesNotRepeatWhatTheViewerAlreadyHas()
    {
        var terminal = new ChessLabTerminal(capacity: 10);
        for (int i = 0; i < 3; i++) terminal.Append(ChessLabStream.Stdout, $"line {i}");
        terminal.Complete();

        var resumed = new List<string>();
        await foreach (var line in terminal.ReadAsync(afterSeq: 1, CancellationToken.None))
            resumed.Add(line.Text);

        Assert.Equal(["line 2"], resumed);
    }

    [Fact]
    public async Task Complete_EndsLiveSubscriptions_ButLeavesScrollbackReadable()
    {
        var terminal = new ChessLabTerminal(capacity: 10);
        terminal.Append(ChessLabStream.Stdout, "only line");

        var drained = Task.Run(async () =>
        {
            int n = 0;
            await foreach (var _ in terminal.ReadAsync(-1, CancellationToken.None)) n++;
            return n;
        });

        terminal.Complete();
        Assert.Equal(1, await drained.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(terminal.IsCompleted);
        Assert.Single(terminal.Snapshot());
    }

    [Fact]
    public void Append_CarriesEngineAndDirection()
    {
        var terminal = new ChessLabTerminal();
        var line = terminal.Append(ChessLabStream.Uci, "go depth 4", "Laplace", ChessLabDirection.Send);

        Assert.Equal((ChessLabStream.Uci, "Laplace", ChessLabDirection.Send), (line.Stream, line.Engine, line.Direction));
        Assert.Equal(0, line.Seq);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }
}
