using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabIngestRunTests
{
    private static ChessLabIngestRun Run(
        RecordingObservability journal, string job, string label, string? source,
        List<(string? Source, string Path)> read, Exception? failure = null, string? failAt = null)
        => new(journal, NullLogger.Instance, job, label, source, (s, path, _) =>
        {
            if (failure is not null && (failAt is null || path == failAt)) return Task.FromException(failure);
            read.Add((s, path));
            return Task.CompletedTask;
        });

    [Fact]
    public async Task SuccessfulImport_JournalsEachFileAndOneOkRunUnderItsWitness()
    {
        var journal = new RecordingObservability();
        var read = new List<(string? Source, string Path)>();
        var run = Run(journal, "job-1", "chesscom/hikaru", "Laplace", read);
        run.Start();
        await run.IngestUnitAsync("2018/07", "/spool/2018-07.pgn", default);
        await run.IngestUnitAsync("2018/08", "/spool/2018-08.pgn", default);
        run.Complete();
        run.Fail(new InvalidOperationException("late"));

        Assert.Equal(new[]
        {
            "run-start Laplace -1",
            "file-start chesscom/hikaru/2018/07",
            "file-end chesscom/hikaru/2018/07 ok",
            "file-start chesscom/hikaru/2018/08",
            "file-end chesscom/hikaru/2018/08 ok",
            "run-end Laplace ok",
        }, journal.Events);
        // The Engine read each file as the run's witness states it, in order.
        Assert.Equal(new (string?, string)[] { ("Laplace", "/spool/2018-07.pgn"), ("Laplace", "/spool/2018-08.pgn") }, read);
        var finished = Assert.Single(journal.Finished);
        Assert.Equal(2, finished.Result.UnitsAttempted);
        Assert.Equal(2, finished.Result.UnitsApplied);
        Assert.Equal(0, finished.Result.UnitsFailed);
        Assert.Equal(2, finished.Result.FilesDone);
        Assert.Null(finished.Error);
    }

    [Fact]
    public async Task APgnOfNoCorpusIsReadByTheGenericRecipeAndJournaledAsPgn()
    {
        var journal = new RecordingObservability();
        var read = new List<(string? Source, string Path)>();
        var run = Run(journal, "job-4", "lichess/someone", null, read);
        run.Start();
        await run.IngestUnitAsync("someone_lichess.pgn", "/spool/someone_lichess.pgn", default);
        run.Complete();
        Assert.Equal("run-start pgn -1", journal.Events[0]);
        Assert.Equal("run-end pgn ok", journal.Events[^1]);
        Assert.Equal(new (string?, string)[] { (null, "/spool/someone_lichess.pgn") }, read);
    }

    [Fact]
    public async Task FailedFile_JournalsTheFileAndRunAsFailedWithTheError()
    {
        var journal = new RecordingObservability();
        var read = new List<(string? Source, string Path)>();
        var boom = new InvalidOperationException("The Engine did not record /spool/b.pgn (3): bad movetext");
        var run = Run(journal, "job-2", "chesscom/hikaru", "Laplace", read, boom, "/spool/b.pgn");
        run.Start();
        await run.IngestUnitAsync("2018/07", "/spool/a.pgn", default);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.IngestUnitAsync("2018/08", "/spool/b.pgn", default));
        Assert.Same(boom, thrown);
        run.Fail(thrown);
        run.Complete();

        Assert.Equal("file-end chesscom/hikaru/2018/08 failed", journal.Events[^2]);
        Assert.Equal("run-end Laplace failed", journal.Events[^1]);
        Assert.Contains("bad movetext", journal.FileErrors["chesscom/hikaru/2018/08"]);
        var finished = Assert.Single(journal.Finished);
        Assert.Equal("[InvalidOperationException] The Engine did not record /spool/b.pgn (3): bad movetext", finished.Error);
        Assert.Equal(1, finished.Result.UnitsFailed);
        Assert.Equal(1, finished.Result.FilesDone);
    }

    [Fact]
    public async Task CancelledFile_FinishesTheRunCancelled()
    {
        var journal = new RecordingObservability();
        var run = Run(journal, "job-3", "lichess/someone", null, [], new OperationCanceledException());
        run.Start();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            run.IngestUnitAsync("someone_lichess.pgn", "/spool/someone_lichess.pgn", default));
        run.Cancel();
        Assert.Equal("file-end lichess/someone/someone_lichess.pgn cancelled", journal.Events[^2]);
        Assert.Equal("run-end pgn cancelled", journal.Events[^1]);
        Assert.True(run.IsFinished);
    }

    private sealed class RecordingObservability : IIngestObservability
    {
        public List<string> Events { get; } = [];
        public Dictionary<string, string?> FileErrors { get; } = [];
        public List<(IngestRunResult Result, string Status, string? Error)> Finished { get; } = [];

        public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory)
            => Events.Add($"run-start {sourceName} {layerOrder}");
        public void OnIntentApplied(string sourceName, ApplyResult result) { }
        public void OnIntentFailed(string sourceName, IngestFailure failure) { }
        public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null)
        {
            Events.Add($"run-end {sourceName} {status}");
            Finished.Add((result, status, error));
        }
        public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0)
            => Events.Add($"file-start {fileLabel}");
        public void OnFileFinished(string sourceName, string fileLabel, string status, string? error = null)
        {
            Events.Add($"file-end {fileLabel} {status}");
            FileErrors[fileLabel] = error;
        }
    }
}
