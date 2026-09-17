using System.Text.Json;
using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordingDiagnosticsTests
{
    private static Hash128 Id(ulong value) => new(1, value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualNativeRejectionRetainsFailedWindowWithoutDurableSuccess(bool failDuringCapture)
    {
        // A valid placement is copied with a deliberately false declared identity.
        // The real native staging boundary rejects it before any database access.
        var change = RejectedPlacement(failDuringCapture);
        var measurement = new ChessRecordingMeasurement(null, 1);
        measurement.ObserveBuiltChanges([change]);
        var logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=diagnostics_never_opened;Username=unused;Timeout=1");
        var writer = new NpgsqlSubstrateWriter(dataSource, logger,
            durability: PostgresWriteDurability.Synchronous);

        using (measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.SourceReadParseAndValidation))
        using (measurement.MeasurePhase(ChessRecordingMeasurement.WorkPhase.WriterApply))
        {
            measurement.Work.WriterApplyAttempts++;
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => writer.ApplyAsync(change));
            Assert.Contains("native physicality batch staging failed", failure.Message);
        }
        measurement.Complete("failed", "expected native identity rejection");
        string expectedPhase = failDuringCapture ? "physicality-capture" : "managed-staging";
        var failed = Assert.Single(measurement.WriterLog.Entries.Where(entry =>
            entry.Fields.TryGetValue("Phase", out var phase) && Equals(phase, expectedPhase)
            && Equals(entry.Fields.GetValueOrDefault("Boundary"), "exited")));
        var entered = Assert.Single(measurement.WriterLog.Entries.Where(entry =>
            Equals(entry.Fields.GetValueOrDefault("Phase"), expectedPhase)
            && Equals(entry.Fields.GetValueOrDefault("Boundary"), "entered")));
        Assert.True(entered.AdmissionElapsedSeconds <= failed.AdmissionElapsedSeconds);
        Assert.False(entered.Fields.ContainsKey("Returned"));
        Assert.Equal(false, failed.Fields["Returned"]);
        Assert.True(Convert.ToDouble(failed.Fields["ElapsedMs"]) >= 0);
        var failedAggregate = Assert.Single(measurement.WriterLog.PhaseAggregates.Where(
            value => value.Phase == expectedPhase));
        Assert.Equal(1, failedAggregate.ObservedExits);
        Assert.Equal(0, failedAggregate.ReturnedExits);
        Assert.Equal(1, failedAggregate.InterruptedExits);
        Assert.Equal(Convert.ToDouble(failed.Fields["ElapsedMs"]), failedAggregate.TotalMilliseconds);
        if (failDuringCapture)
            Assert.Contains(measurement.WriterLog.Entries, entry =>
                Equals(entry.Fields.GetValueOrDefault("Phase"), "managed-staging")
                && Equals(entry.Fields.GetValueOrDefault("Returned"), true));
        Assert.DoesNotContain(measurement.WriterLog.Entries, entry =>
            Equals(entry.Fields.GetValueOrDefault("Phase"), "connection-and-apply-lock"));
        Assert.Equal(1, measurement.Work.WriterApplyAttempts);
        Assert.Equal(1, measurement.Work.BuiltManagedPhysicalityRows);
        Assert.Equal(failDuringCapture ? 1 : 0, measurement.Work.BuiltManagedPhysicalityObservationRows);
        Assert.Equal(0, measurement.Work.BuiltNativeStages);
        Assert.Equal(0, measurement.Writer.ApplyCalls);
        Assert.Equal(0, measurement.CommittedGames);
        Assert.Equal(0, measurement.ReadbackGames);
        Assert.Null(measurement.Durability);
        Assert.Equal(0, measurement.Work.ExclusiveSeconds["ExactReadback"]);
        Assert.InRange(measurement.Work.ExclusiveSeconds.Values.Sum(), 0,
            measurement.ElapsedSeconds.Total);

        // Failure diagnostics are serializable in the ordinary recording receipt.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("committedGames").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("work").GetProperty("writerApplyAttempts").GetInt64());
        Assert.NotEmpty(json.RootElement.GetProperty("writerLog").GetProperty("entries").EnumerateArray());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingDiagnosticSinkCannotMaskActualNativeFailure(bool throwOnIsEnabled)
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=diagnostics_never_opened;Username=unused;Timeout=1");
        var writer = new NpgsqlSubstrateWriter(dataSource, new ThrowingWriterLogger(throwOnIsEnabled));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.ApplyAsync(RejectedPlacement(false)));
        Assert.Contains("native physicality batch staging failed", failure.Message);
        Assert.DoesNotContain("diagnostic sink", failure.Message);
    }

    [Fact]
    public void WholeRunPhaseAggregatesSurviveParallelLoggingBeyondTheTail()
    {
        var measurement = new ChessRecordingMeasurement(null, 1);
        ILogger logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
        Parallel.For(0, 1024, i =>
        {
            logger.LogInformation("WS_APPLY phase: {Phase} boundary={Boundary}", "copy", "entered");
            LogPhaseExit(logger, "copy", i + 1, returned: i % 3 != 0);
        });

        var phase = Assert.Single(measurement.WriterLog.PhaseAggregates);
        Assert.Equal("copy", phase.Phase);
        Assert.Equal(1024, phase.ObservedExits);
        Assert.Equal(682, phase.ReturnedExits);
        Assert.Equal(342, phase.InterruptedExits);
        Assert.Equal(524800, phase.TotalMilliseconds);
        Assert.Equal(1, phase.MinimumMilliseconds);
        Assert.Equal(1024, phase.MaximumMilliseconds);
        Assert.Equal(128, measurement.WriterLog.Entries.Count);
        Assert.Equal(1920, measurement.WriterLog.DroppedEntries);
        Assert.Equal(0, measurement.WriterLog.UnaggregatedPhaseExits);
        Assert.Equal(0, measurement.WriterLog.RejectedPhaseExits);

        // Immutable snapshots stay coherent while later observations accumulate.
        LogPhaseExit(logger, "copy", 7, returned: true);
        Assert.Equal(1024, phase.ObservedExits);
        Assert.Equal(1025, Assert.Single(measurement.WriterLog.PhaseAggregates).ObservedExits);
        Assert.Equal(0, measurement.Writer.ApplyCalls);
        Assert.Equal(0, measurement.ReadbackGames);
        Assert.Null(measurement.Durability);
    }

    [Fact]
    public void PhaseAggregateNamesAndInvalidSamplesRemainBoundedAndExplicit()
    {
        var measurement = new ChessRecordingMeasurement(null, 1);
        ILogger logger = new ChessRecordingMeasurement.WriterDiagnosticLogger { Measurement = measurement };
        int maximum = ChessRecordingMeasurement.WriterLogDiagnostics.MaximumDistinctPhases;
        for (int i = 0; i < maximum + 2; i++)
            LogPhaseExit(logger, $"phase-{i:D2}", 2, returned: true);
        // An existing phase continues accumulating after the distinct-name bound.
        LogPhaseExit(logger, "phase-00", 5, returned: false);
        foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
            LogPhaseExit(logger, "phase-00", invalid, returned: true);
        logger.LogInformation("malformed phase {Phase} boundary={Boundary}", "phase-00", "exited");
        LogPhaseExit(logger, new string('x', 4096), 1, returned: true);

        Assert.Equal(maximum, measurement.WriterLog.PhaseAggregates.Count);
        var first = measurement.WriterLog.PhaseAggregates[0];
        Assert.Equal("phase-00", first.Phase);
        Assert.Equal(2, first.ObservedExits);
        Assert.Equal(1, first.ReturnedExits);
        Assert.Equal(1, first.InterruptedExits);
        Assert.Equal(7, first.TotalMilliseconds);
        Assert.Equal(2, first.MinimumMilliseconds);
        Assert.Equal(5, first.MaximumMilliseconds);
        Assert.Equal(2, measurement.WriterLog.UnaggregatedPhaseExits);
        Assert.Equal(5, measurement.WriterLog.RejectedPhaseExits);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("laplace.chess-recording/v2", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(maximum, json.RootElement.GetProperty("writerLog").GetProperty("phaseAggregates").GetArrayLength());
        Assert.Equal(5, json.RootElement.GetProperty("writerLog").GetProperty("rejectedPhaseExits").GetInt64());
    }

    [Fact]
    public void PhaseAggregatesDoNotLeakFromSetupOrFreshIntoReplay()
    {
        var logger = new ChessRecordingMeasurement.WriterDiagnosticLogger();
        LogPhaseExit(logger, "setup", 100, returned: true);
        var fresh = new ChessRecordingMeasurement(null, 1);
        logger.Measurement = fresh;
        LogPhaseExit(logger, "copy", 5, returned: true);
        logger.Measurement = null;
        LogPhaseExit(logger, "between-admissions", 100, returned: true);
        var replay = new ChessRecordingMeasurement(null, 1);
        logger.Measurement = replay;
        Assert.Empty(replay.WriterLog.PhaseAggregates);
        LogPhaseExit(logger, "copy", 2, returned: false);

        var freshPhase = Assert.Single(fresh.WriterLog.PhaseAggregates);
        var replayPhase = Assert.Single(replay.WriterLog.PhaseAggregates);
        Assert.Equal(1, freshPhase.ObservedExits);
        Assert.Equal(5, freshPhase.TotalMilliseconds);
        Assert.Equal(1, freshPhase.ReturnedExits);
        Assert.Equal(1, replayPhase.ObservedExits);
        Assert.Equal(2, replayPhase.TotalMilliseconds);
        Assert.Equal(1, replayPhase.InterruptedExits);
        Assert.Equal(0, fresh.Writer.ApplyCalls);
        Assert.Equal(0, replay.Writer.ApplyCalls);
        Assert.Null(fresh.Durability);
        Assert.Null(replay.Durability);
    }


    [Fact]
    public void ConsensusBackendDeltasExcludeSetupAndSeparateAdmissions()
    {
        var fresh = new ChessRecordingMeasurement(null, 1);
        // Nonzero initial values represent setup on the same owned writer.
        fresh.ObserveConsensusBackend(new(100, 200, 30, 4, 5, 60),
            new(140, 220, 37, 6, 8, 69));
        var retained = fresh.ConsensusBackend;
        // Work outside the measured call must not enter the next delta.
        fresh.ObserveConsensusBackend(new(900, 900, 900, 900, 900, 900),
            new(960, 980, 903, 901, 902, 905));
        Assert.Equal(2, fresh.ConsensusBackend.ObservedApplyWindows);
        Assert.Equal(100.0 / TimeSpan.TicksPerSecond, fresh.ConsensusBackend.ConsensusUpsertSeconds, 12);
        Assert.Equal(100.0 / TimeSpan.TicksPerSecond, fresh.ConsensusBackend.HighwayMaskSeconds, 12);
        Assert.Equal(10, fresh.ConsensusBackend.CellsFolded);
        Assert.Equal(3, fresh.ConsensusBackend.ConsensusUpsertCalls);
        Assert.Equal(5, fresh.ConsensusBackend.HighwayMaskCalls);
        Assert.Equal(14, fresh.ConsensusBackend.HighwayMaskPairs);
        Assert.Equal(1, retained.ObservedApplyWindows);
        Assert.Equal(7, retained.CellsFolded);

        var replay = new ChessRecordingMeasurement(null, 1);
        Assert.Equal(new ChessRecordingMeasurement.ConsensusBackendDiagnostics(0, 0, 0, 0, 0, 0, 0),
            replay.ConsensusBackend);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(fresh,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(2, json.RootElement.GetProperty("consensusBackend")
            .GetProperty("observedApplyWindows").GetInt64());
        Assert.Equal(14, json.RootElement.GetProperty("consensusBackend")
            .GetProperty("highwayMaskPairs").GetInt64());
        Assert.Equal(0, fresh.Writer.ApplyCalls);
        Assert.Null(fresh.Durability);
    }

    [Fact]
    public void ConsensusBackendWindowDoesNotInventUnpublishedFailedWork()
    {
        var measurement = new ChessRecordingMeasurement(null, 1);
        var unchanged = new ChessRecordingMeasurement.ConsensusBackendSnapshot(10, 20, 30, 40, 50, 60);
        measurement.ObserveConsensusBackend(unchanged, unchanged);
        Assert.Equal(new ChessRecordingMeasurement.ConsensusBackendDiagnostics(1, 0, 0, 0, 0, 0, 0),
            measurement.ConsensusBackend);
        Assert.Equal(0, measurement.Writer.ApplyCalls);
        Assert.Null(measurement.Durability);
    }

    private static void LogPhaseExit(ILogger logger, string phase, double milliseconds, bool returned) =>
        logger.LogInformation(
            "WS_APPLY phase: {Phase} boundary={Boundary} returned={Returned} elapsed_ms={ElapsedMs}",
            phase, "exited", returned, milliseconds);

    private static SubstrateChange RejectedPlacement(bool failDuringCapture)
    {
        double[] coordinate = [.1, .2, .3, .4];
        var row = new PhysicalityRow(PhysicalityId.Compute(Id(1), PhysicalityType.Projection),
            Id(1), Id(2), PhysicalityType.Projection, .1, .2, .3, .4,
            Hilbert128.Encode(coordinate), null, 0, null, null, IntentStage.PgEpochUnixUs + 10);
        var invalid = row with { Id = Id(99) };
        return new SubstrateChange([], [failDuringCapture ? row : invalid], [],
            new(Id(3), Id(2), "native-rejection-diagnostic", DateTimeOffset.UtcNow, null))
        {
            PhysicalityObservations = failDuringCapture ? [invalid] : default,
        }.WithSourcePrior(Id(2), .5);
    }

    private sealed class ThrowingWriterLogger(bool throwOnIsEnabled) : ILogger<NpgsqlSubstrateWriter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => throwOnIsEnabled
            ? throw new IOException("diagnostic sink IsEnabled failed") : true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new IOException("diagnostic sink Log failed");
    }

    [Fact]
    public void WriterLogsAreBoundedAndCannotLeakFromSetupOrFreshIntoReplay()
    {
        var logger = new ChessRecordingMeasurement.WriterDiagnosticLogger();
        ILogger sink = logger;
        sink.LogInformation("setup {Index}", -1);
        var fresh = new ChessRecordingMeasurement(null, 1);
        logger.Measurement = fresh;
        int capacity = ChessRecordingMeasurement.WriterLogDiagnostics.Capacity;
        for (int i = 0; i < capacity + 3; i++)
            sink.LogInformation("writer {Index} {Text}", i, new string('x', 4096));
        Assert.Equal(capacity, fresh.WriterLog.Entries.Count);
        Assert.Equal(3, fresh.WriterLog.DroppedEntries);
        Assert.Equal(3, fresh.WriterLog.Entries[0].Fields["Index"]);
        Assert.All(fresh.WriterLog.Entries, entry =>
            Assert.True(entry.Message.Length < 2100
                && ((string)entry.Fields["Text"]!).Length < 2100));
        logger.Measurement = null;
        sink.LogInformation("between admissions");
        var replay = new ChessRecordingMeasurement(null, 1);
        logger.Measurement = replay;
        sink.LogInformation("replay {Index}", 7);
        Assert.Single(replay.WriterLog.Entries);
        Assert.Equal(7, replay.WriterLog.Entries[0].Fields["Index"]);
        Assert.Equal(capacity, fresh.WriterLog.Entries.Count);
        Assert.Equal(0, fresh.Writer.ApplyCalls);
        Assert.Equal(0, replay.Writer.ApplyCalls);
        Assert.Null(fresh.Durability);
        Assert.Null(replay.Durability);
    }
}
