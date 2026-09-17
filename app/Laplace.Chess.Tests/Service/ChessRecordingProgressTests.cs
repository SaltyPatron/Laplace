using System.Diagnostics;
using System.Text.Json;
using Laplace.Chess.Service;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordingProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
        "laplace-recording-progress-" + Guid.NewGuid().ToString("N"));
    public ChessRecordingProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string At(string name) => Path.Combine(_root, name);

    private static async Task<JsonElement> ReadAsync(string path)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForAsync(string path, Func<JsonElement, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var document = await ReadAsync(path);
            if (predicate(document)) return document;
            await Task.Delay(25, deadline.Token);
        }
    }

    [Fact]
    public async Task HeartbeatSurvivesAnIdleOwnerAndNeverSamplesItsMutableCounters()
    {
        string path = At("progress.json");
        var measurement = new ChessRecordingMeasurement(null, 2000);
        await measurement.StartProgressAsync(path);
        try
        {
            measurement.Work.NovelGamesComposed = 7;
            measurement.Work.NovelPliesComposed = 70;
            measurement.Checkpoint("WriterApply", "writer-entered", chunkGames: 2000);
            ILogger logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
            logger.LogInformation("WS_APPLY phase: {Phase} boundary={Boundary}", "physicality-capture", "entered");
            // Deliberately mutate owner state without publishing. The observer must
            // retain the immutable sample, even while the owner makes no callbacks.
            measurement.Work.NovelGamesComposed = 999;
            measurement.Work.NovelPliesComposed = 9999;
            var first = await WaitForAsync(path, document =>
                document.GetProperty("checkpoint").GetProperty("lastWriterBoundary").ValueKind != JsonValueKind.Null);
            double firstHeartbeat = first.GetProperty("heartbeatElapsedSeconds").GetDouble();
            var second = await WaitForAsync(path, document =>
                document.GetProperty("heartbeatElapsedSeconds").GetDouble() > firstHeartbeat + .5);
            var checkpoint = second.GetProperty("checkpoint");
            var counters = checkpoint.GetProperty("counters");
            Assert.Equal(first.GetProperty("checkpoint").GetProperty("sequence").GetInt64(),
                checkpoint.GetProperty("sequence").GetInt64());
            Assert.Equal(7, counters.GetProperty("novelGamesComposed").GetInt64());
            Assert.Equal(70, counters.GetProperty("novelPliesComposed").GetInt64());
            Assert.Equal(2000, counters.GetProperty("currentChunkGames").GetInt32());
            Assert.Equal(0, counters.GetProperty("newlyRecordedGamesWithSealedChunkEvidence").GetInt32());
            Assert.Equal(0, counters.GetProperty("writerCallsAcknowledged").GetInt64());
            Assert.Equal("running", checkpoint.GetProperty("admissionStatus").GetString());
            var writer = checkpoint.GetProperty("lastWriterBoundary");
            Assert.Equal("physicality-capture", writer.GetProperty("phase").GetString());
            Assert.Equal("entered", writer.GetProperty("boundary").GetString());
            Assert.Equal(JsonValueKind.Null, writer.GetProperty("returned").ValueKind);
            Assert.Equal(Environment.ProcessId, second.GetProperty("processId").GetInt32());
            Assert.True(second.GetProperty("heartbeatElapsedSeconds").GetDouble()
                >= writer.GetProperty("observedElapsedSeconds").GetDouble());
        }
        finally { await measurement.StopProgressAsync(); }
        Assert.False(File.Exists(path + ".pending"));
    }

    [Fact]
    public async Task HeartbeatRetainsClosedTimingsBeforeAdmissionCompletion()
    {
        string path = At("live-timings.json");
        var measurement = new ChessRecordingMeasurement(null, 2000);
        await measurement.StartProgressAsync(path);
        try
        {
            measurement.Work.Add(ChessRecordingMeasurement.WorkPhase.WriterApply, Stopwatch.Frequency / 8);
            measurement.Work.NovelGamesComposed = 7;
            measurement.Checkpoint("WriterApply", "writer-entered");
            // Unpublished admission work must not be sampled by the heartbeat.
            measurement.Work.Add(ChessRecordingMeasurement.WorkPhase.WriterApply, Stopwatch.Frequency);
            ILogger logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
            Parallel.For(0, 300, i => logger.LogInformation(
                "WS_APPLY phase: {Phase} boundary={Boundary} returned={Returned} elapsed_ms={ElapsedMs}",
                "consensus-acceptance-participant", "exited", i % 2 == 0, i % 2 == 0 ? 2.0 : 3.0));
            measurement.ReadbackOperations.Add(
                ChessRecordingMeasurement.ReadbackOperation.ExactWitnessBodies,
                Stopwatch.Frequency / 2, returned: true);
            measurement.ReadbackOperations.Add(
                ChessRecordingMeasurement.ReadbackOperation.ExactWitnessBodies,
                Stopwatch.Frequency / 40, returned: false);
            var document = await WaitForAsync(path, value =>
                value.GetProperty("timingAggregates").GetProperty("writerPhaseAggregates")
                    .EnumerateArray().Any(phase => phase.GetProperty("observedExits").GetInt64() == 300)
                && value.GetProperty("timingAggregates").GetProperty("readbackOperations")
                    .EnumerateArray().Any(operation =>
                        operation.GetProperty("operation").GetString() == "ExactWitnessBodies"
                        && operation.GetProperty("interruptedCalls").GetInt64() == 1));
            var checkpoint = document.GetProperty("checkpoint");
            Assert.Equal("running", checkpoint.GetProperty("admissionStatus").GetString());
            Assert.Equal(.125, checkpoint.GetProperty("workTiming")
                .GetProperty("closedExclusiveSeconds").GetProperty("WriterApply").GetDouble(), 6);
            Assert.Equal(0, checkpoint.GetProperty("counters")
                .GetProperty("newlyRecordedGamesWithSealedChunkEvidence").GetInt32());
            var timings = document.GetProperty("timingAggregates");
            var writer = Assert.Single(timings.GetProperty("writerPhaseAggregates").EnumerateArray());
            Assert.Equal(300, writer.GetProperty("observedExits").GetInt64());
            Assert.Equal(150, writer.GetProperty("returnedExits").GetInt64());
            Assert.Equal(150, writer.GetProperty("interruptedExits").GetInt64());
            Assert.Equal(750, writer.GetProperty("totalMilliseconds").GetDouble());
            Assert.True(measurement.WriterLog.DroppedEntries > 0);
            var readback = Assert.Single(timings.GetProperty("readbackOperations").EnumerateArray()
                .Where(value => value.GetProperty("operation").GetString() == "ExactWitnessBodies"));
            Assert.Equal(1, readback.GetProperty("returnedCalls").GetInt64());
            Assert.Equal(1, readback.GetProperty("interruptedCalls").GetInt64());
            Assert.Equal(.525, readback.GetProperty("totalSeconds").GetDouble(), 6);
            Assert.False(document.TryGetProperty("recordedGamesPerSecond", out _));
            Assert.False(document.TryGetProperty("targetMet", out _));
            Assert.True(timings.GetProperty("observedElapsedSeconds").GetDouble()
                <= document.GetProperty("heartbeatElapsedSeconds").GetDouble());
        }
        finally { await measurement.StopProgressAsync(); }
    }

    [Fact]
    public async Task FailingTimingObservationStillWritesTheOwnerCheckpoint()
    {
        string path = At("failed-timing-observer.json");
        await using var sink = new ChessRecordingMeasurement.ProgressSink(path, Stopwatch.GetTimestamp(),
            timingAggregates: () => throw new InvalidOperationException("fixture timing observation failed"));
        ChessRecordingMeasurement.ProgressCounters counters = new(
            0, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        sink.Publish("WriterApply", "writer-entered", "running", counters);
        await sink.FlushAsync();
        var document = await ReadAsync(path);
        Assert.Equal(JsonValueKind.Null, document.GetProperty("timingAggregates").ValueKind);
        Assert.Equal(7, document.GetProperty("checkpoint").GetProperty("counters")
            .GetProperty("novelGamesComposed").GetInt64());
        Assert.Equal("running", document.GetProperty("checkpoint").GetProperty("admissionStatus").GetString());
        Assert.Equal(1, sink.Diagnostics.ObservationFailures);
        Assert.Equal(1, sink.Diagnostics.SuccessfulWrites);
        Assert.Equal(0, sink.Diagnostics.WriteFailures);
        Assert.Equal(typeof(InvalidOperationException).FullName, sink.Diagnostics.LastDiagnosticErrorType);
    }

    [Fact]
    public async Task AcknowledgedWriterAndReadbackCountersCannotBecomeSealedEvidence()
    {
        string path = At("unsealed.json");
        var measurement = new ChessRecordingMeasurement(null, 1);
        await measurement.StartProgressAsync(path);
        measurement.Work.WriterApplyAttempts = 1;
        measurement.Writer.ApplyCalls = 1;
        measurement.Writer.CopyTransactionsCommitted = 2;
        measurement.Work.BuiltManagedPhysicalityObservationRows = 123;
        // Synthetic transport state, not native/PG proof. Even a populated
        // readback list must not advance the independently sealed evidence count.
        measurement.Games.Add(new("playing", "line", "start", ["move"], "1-0",
            null, null, "diagnostic counter fixture"));
        measurement.Complete("cancelled", "fixture interruption after a writer acknowledgement");
        await measurement.StopProgressAsync();
        var document = await ReadAsync(path);
        var checkpoint = document.GetProperty("checkpoint");
        var counters = checkpoint.GetProperty("counters");
        Assert.Equal("cancelled", checkpoint.GetProperty("admissionStatus").GetString());
        Assert.Equal(1, counters.GetProperty("writerCallsAcknowledged").GetInt64());
        Assert.Equal(2, counters.GetProperty("copyTransactionsAcknowledged").GetInt64());
        Assert.Equal(1, counters.GetProperty("exactReadbackGames").GetInt32());
        Assert.Equal(123, counters.GetProperty("builtManagedPhysicalityObservationRows").GetInt64());
        Assert.Equal(0, counters.GetProperty("gamesWithSealedChunkEvidence").GetInt32());
        Assert.Equal(0, counters.GetProperty("newlyRecordedGamesWithSealedChunkEvidence").GetInt32());
        Assert.False(document.TryGetProperty("recordedGamesPerSecond", out _));
        Assert.False(document.TryGetProperty("targetMet", out _));
    }

    [Fact]
    public async Task UnwritableProgressAndThrowingLogRemainExplicitWithoutRejectingTheOwner()
    {
        var measurement = new ChessRecordingMeasurement(null, 1);
        await measurement.StartProgressAsync(At("absent/progress.json"),
            _ => throw new IOException("fixture diagnostic log failure"));
        measurement.Complete("failed", "original admission failure");
        await measurement.StopProgressAsync();
        Assert.Equal("failed", measurement.Status);
        Assert.Equal("original admission failure", measurement.Error);
        Assert.True(measurement.Progress!.WriteFailures >= 2);
        Assert.True(measurement.Progress.LogFailures >= 1);
        Assert.Equal(typeof(DirectoryNotFoundException).FullName, measurement.Progress.LastDiagnosticErrorType);
        Assert.Equal(0, measurement.Progress.SuccessfulWrites);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(json.RootElement.GetProperty("progress").GetProperty("writeFailures").GetInt64() >= 2);
        Assert.Equal("original admission failure", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConcurrentFlushesAreSerializedAndRetainOnlyBoundedLatestState()
    {
        string path = At("serialized.json");
        int active = 0, maximum = 0;
        await using var sink = new ChessRecordingMeasurement.ProgressSink(path, Stopwatch.GetTimestamp(),
            write: async (destination, json) =>
            {
                maximum = Math.Max(maximum, Interlocked.Increment(ref active));
                try
                {
                    await Task.Yield();
                    await File.WriteAllTextAsync(destination + ".pending", json);
                    File.Move(destination + ".pending", destination, overwrite: true);
                }
                finally { Interlocked.Decrement(ref active); }
            });
        ChessRecordingMeasurement.ProgressCounters zero = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        for (int i = 0; i < 10_000; i++)
            sink.Publish("composition", "game-completed", "running", zero with { NovelGamesComposed = i });
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => sink.FlushAsync()));
        Assert.Equal(1, maximum);
        var document = await ReadAsync(path);
        Assert.Equal(10_000, document.GetProperty("checkpoint").GetProperty("sequence").GetInt64());
        Assert.Equal(9999, document.GetProperty("checkpoint").GetProperty("counters")
            .GetProperty("novelGamesComposed").GetInt64());
        Assert.InRange(new FileInfo(path).Length, 1, 8192);
        Assert.False(File.Exists(path + ".pending"));
        Assert.Equal(0, sink.Diagnostics.WriteFailures);
    }

    [Fact]
    public async Task FinalCheckpointRetainsTheLastFailedWriterBoundaryAcrossPhaseUnwind()
    {
        string path = At("failed-writer.json");
        var measurement = new ChessRecordingMeasurement(null, 1);
        await measurement.StartProgressAsync(path);
        ILogger logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
        using (measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.WriterApply))
        {
            logger.LogInformation("WS_APPLY phase: {Phase} boundary={Boundary}", "physicality-capture", "entered");
            logger.LogInformation("WS_APPLY phase: {Phase} boundary={Boundary} returned={Returned} elapsed_ms={ElapsedMs}",
                "physicality-capture", "exited", false, 10.0);
        }
        measurement.Complete("failed", "fixture native failure");
        await measurement.StopProgressAsync();
        var checkpoint = (await ReadAsync(path)).GetProperty("checkpoint");
        Assert.Equal("terminal", checkpoint.GetProperty("boundary").GetString());
        var writer = checkpoint.GetProperty("lastWriterBoundary");
        Assert.Equal("WriterApply", writer.GetProperty("ownerPhase").GetString());
        Assert.Equal("physicality-capture", writer.GetProperty("phase").GetString());
        Assert.Equal("exited", writer.GetProperty("boundary").GetString());
        Assert.False(writer.GetProperty("returned").GetBoolean());
        Assert.Equal(0, checkpoint.GetProperty("counters")
            .GetProperty("newlyRecordedGamesWithSealedChunkEvidence").GetInt32());
    }
}
