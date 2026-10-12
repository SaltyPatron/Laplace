using global::Npgsql;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class StagedSourceWriterLifecycleTests
{
    [Theory]
    [InlineData(6, 100, 2, 6)]
    [InlineData(6, 7, 3, 2)]
    public async Task WorkRespectsTopologyAndReservesWorkerConnections(int lanes, int pool, int connections, int expected)
    {
        await using var ds = NpgsqlDataSource.Create($"Host=localhost;Maximum Pool Size={pool}");
        await using var writer = new StagedSourceWriter(new ControlledWriter(), ds,
            PostgresWriteDurability.Asynchronous, lanes);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, finished = 0;
        Task work = writer.ForEachAsync(Enumerable.Range(0, 24).ToArray(), async _ =>
        {
            int count = Interlocked.Increment(ref running);
            int observed;
            do { observed = Volatile.Read(ref peak); }
            while (count > observed && Interlocked.CompareExchange(ref peak, count, observed) != observed);
            try { await release.Task; }
            finally { Interlocked.Decrement(ref running); Interlocked.Increment(ref finished); }
        }, CancellationToken.None, connections);
        // Each admitted worker reaches its first await before scheduling returns.
        Assert.Equal(expected, Volatile.Read(ref running));
        release.SetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, peak);
        Assert.Equal(24, finished);
    }

    [Fact]
    public async Task PoolTooSmallFailsBeforeStartingWork()
    {
        await using var ds = NpgsqlDataSource.Create("Host=localhost;Maximum Pool Size=3");
        await using var writer = new StagedSourceWriter(new ControlledWriter(), ds,
            PostgresWriteDurability.Asynchronous, 6);
        bool ran = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.ForEachAsync(new[] { 1 }, _ => { ran = true; return Task.CompletedTask; },
                CancellationToken.None, connectionsPerItem: 3));
        Assert.False(ran);
        Assert.Contains("at least 4 pooled connections", error.Message);
    }

    [Fact]
    public async Task FilesWaitForBulkCommitAndGetANewBarrierEachRun()
    {
        await using var ds = NpgsqlDataSource.Create("Host=localhost");
        var inner = new ControlledWriter();
        await using var writer = new StagedSourceWriter(inner, ds, PostgresWriteDurability.Asynchronous, 2);
        for (int run = 0; run < 2; run++)
        {
            await writer.BeginBulkRunAsync();
            Task file = writer.CompleteFileAsync("input.xml");
            Assert.False(file.IsCompleted);
            Task completion = writer.CompleteBulkRunAsync();
            Assert.False(file.IsCompleted);
            Assert.Equal(run, inner.FilesFinished);
            inner.Commit.SetResult();
            await completion;
            await file.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(run + 1, inner.FilesFinished);
        }
    }

    [Fact]
    public async Task FailedCommitCancelsFileReceiptsAndPreservesBulkFailure()
    {
        await using var ds = NpgsqlDataSource.Create("Host=localhost");
        var inner = new ControlledWriter();
        await using var writer = new StagedSourceWriter(inner, ds, PostgresWriteDurability.Asynchronous, 2);
        await writer.BeginBulkRunAsync();
        Task file = writer.CompleteFileAsync("input.xml");
        Task completion = writer.CompleteBulkRunAsync();
        var failure = new InvalidOperationException("commit failed");
        inner.Commit.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => completion));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, inner.FilesFinished);
    }

    [Fact]
    public async Task CancellingOneFileObserverDoesNotCancelTheRun()
    {
        await using var ds = NpgsqlDataSource.Create("Host=localhost");
        var inner = new ControlledWriter();
        await using var writer = new StagedSourceWriter(inner, ds, PostgresWriteDurability.Asynchronous, 2);
        await writer.BeginBulkRunAsync();
        using var ct = new CancellationTokenSource();
        Task cancelled = writer.CompleteFileAsync("cancelled.xml", ct.Token);
        Task surviving = writer.CompleteFileAsync("surviving.xml");
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(surviving.IsCompleted);
        inner.Commit.SetResult();
        await writer.CompleteBulkRunAsync();
        await surviving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, inner.FilesFinished);
    }

    [Fact]
    public async Task CompletionMetricsPreserveInnerWorkAndResetBetweenRuns()
    {
        await using var ds = NpgsqlDataSource.Create("Host=localhost");
        var inner = new ControlledWriter();
        await using var writer = new StagedSourceWriter(inner, ds, PostgresWriteDurability.Asynchronous, 2);
        await writer.BeginBulkRunAsync();
        inner.Commit.SetResult();
        await writer.CompleteBulkRunAsync();
        Assert.Equal(inner.LastFoldDrainWallClock, writer.LastFoldDrainWallClock);
        Assert.True(writer.LastWriterMaintenanceWallClock >= inner.LastWriterMaintenanceWallClock);
        Assert.Equal(inner.LastFoldSpanWallClock, writer.LastFoldSpanWallClock);
        Assert.Equal(inner.ConsensusUpsertBackendWallClock, writer.ConsensusUpsertBackendWallClock);
        Assert.Equal(inner.HighwayMaskBackendWallClock, writer.HighwayMaskBackendWallClock);
        Assert.Equal(inner.ConsensusUpsertCalls, writer.ConsensusUpsertCalls);
        Assert.Equal(inner.HighwayMaskCalls, writer.HighwayMaskCalls);
        Assert.Equal(inner.HighwayMaskPairs, writer.HighwayMaskPairs);
        await writer.BeginBulkRunAsync();
        Assert.Equal(TimeSpan.Zero, writer.LastFoldDrainWallClock);
        Assert.Equal(TimeSpan.Zero, writer.LastWriterMaintenanceWallClock);
        Assert.Equal(TimeSpan.Zero, writer.LastFoldSpanWallClock);
        Assert.Equal(0, writer.ConsensusUpsertCalls);
    }

    private sealed class ControlledWriter : ISubstrateWriter, IConsensusFoldMetrics
    {
        public TaskCompletionSource Commit { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FilesFinished { get; private set; }
        private bool _completed;
        public long ObservationsAccumulated => 0;
        public long CellsFolded => 0;
        public TimeSpan LastFoldDrainWallClock => _completed ? TimeSpan.FromMilliseconds(17) : TimeSpan.Zero;
        public TimeSpan LastWriterMaintenanceWallClock => _completed ? TimeSpan.FromMilliseconds(11) : TimeSpan.Zero;
        public TimeSpan LastFoldSpanWallClock => _completed ? TimeSpan.FromMilliseconds(31) : TimeSpan.Zero;
        public TimeSpan ConsensusUpsertBackendWallClock => _completed ? TimeSpan.FromMilliseconds(23) : TimeSpan.Zero;
        public TimeSpan HighwayMaskBackendWallClock => _completed ? TimeSpan.FromMilliseconds(7) : TimeSpan.Zero;
        public long ConsensusUpsertCalls => _completed ? 3 : 0;
        public long HighwayMaskCalls => _completed ? 2 : 0;
        public long HighwayMaskPairs => _completed ? 13 : 0;
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BeginBulkRunAsync(CancellationToken ct = default)
        {
            Commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completed = false;
            return Task.CompletedTask;
        }
        public async Task CompleteBulkRunAsync(Action<BulkRunCompletionPhase>? onPhase, CancellationToken ct = default)
        {
            await Commit.Task.WaitAsync(ct);
            _completed = true;
            onPhase?.Invoke(BulkRunCompletionPhase.WriterMaintenance);
        }
        public Task CompleteFileAsync(string fileLabel, CancellationToken ct = default)
        {
            Assert.True(_completed);
            FilesFinished++;
            return Task.CompletedTask;
        }
    }
}
