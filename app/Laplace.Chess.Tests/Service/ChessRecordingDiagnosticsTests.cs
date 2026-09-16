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
