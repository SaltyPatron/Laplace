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
    [Fact]
    public async Task SuccessfulImport_JournalsEachArchiveAndOneOkRun()
    {
        var journal = new RecordingObservability();
        var run = new ChessLabIngestRun(journal, NullLogger.Instance, "job-1", "chesscom/hikaru");
        run.Start();
        await run.IngestUnitAsync("2018/07", 3, () => Task.FromResult(new ChessPgnIngestor.Result(3, 2, 2, 0, 3, 120)));
        await run.IngestUnitAsync("2018/08", 2, () => Task.FromResult(new ChessPgnIngestor.Result(2, 2, 2, 0, 2, 80)));
        run.Complete();
        run.Fail(new InvalidOperationException("late"));

        Assert.Equal(new[]
        {
            "run-start ChessPgn 20",
            "file-start chesscom/hikaru/2018/07",
            "file-end chesscom/hikaru/2018/07 ok",
            "file-start chesscom/hikaru/2018/08",
            "file-end chesscom/hikaru/2018/08 ok",
            "run-end ChessPgn ok",
        }, journal.Events);
        var finished = Assert.Single(journal.Finished);
        Assert.Equal(ChessVocabulary.PgnSourceId, finished.Result.SourceId);
        Assert.Equal(5, finished.Result.UnitsAttempted);
        Assert.Equal(4, finished.Result.UnitsApplied);
        Assert.Equal(0, finished.Result.UnitsFailed);
        Assert.Equal(2, finished.Result.FilesDone);
        Assert.Null(finished.Error);
    }

    [Fact]
    public async Task FailedArchive_JournalsTheFileAndRunAsFailedWithTheError()
    {
        var journal = new RecordingObservability();
        var run = new ChessLabIngestRun(journal, NullLogger.Instance, "job-2", "chesscom/hikaru");
        run.Start();
        await run.IngestUnitAsync("2018/07", 1, () => Task.FromResult(new ChessPgnIngestor.Result(1, 1, 1, 0, 1, 40)));
        var boom = new InvalidDataException("duplicate key value violates unique constraint");
        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            run.IngestUnitAsync("2018/08", 181, () => Task.FromException<ChessPgnIngestor.Result>(boom)));
        Assert.Same(boom, thrown);
        run.Fail(thrown);
        run.Complete();

        Assert.Equal("file-end chesscom/hikaru/2018/08 failed", journal.Events[^2]);
        Assert.Equal("run-end ChessPgn failed", journal.Events[^1]);
        Assert.Contains("duplicate key", journal.FileErrors["chesscom/hikaru/2018/08"]);
        var finished = Assert.Single(journal.Finished);
        Assert.Equal("[InvalidDataException] duplicate key value violates unique constraint", finished.Error);
        Assert.Equal(181, finished.Result.UnitsFailed);
        Assert.Equal(1, finished.Result.FilesDone);
    }

    [Fact]
    public async Task CancelledArchive_FinishesTheRunCancelled()
    {
        var journal = new RecordingObservability();
        var run = new ChessLabIngestRun(journal, NullLogger.Instance, "job-3", "lichess/someone");
        run.Start();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            run.IngestUnitAsync("someone_lichess.pgn", 5,
                () => Task.FromException<ChessPgnIngestor.Result>(new OperationCanceledException())));
        run.Cancel();
        Assert.Equal("file-end lichess/someone/someone_lichess.pgn cancelled", journal.Events[^2]);
        Assert.Equal("run-end ChessPgn cancelled", journal.Events[^1]);
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
