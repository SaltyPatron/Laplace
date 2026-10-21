using System.Collections.Concurrent;
using global::Npgsql;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class JournalFailureDiagnosticsTests
{
    [Fact]
    public void UnreachableJournalRetainsTypedRootCauseAndReportsLostFileEvents()
    {
        using var data = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=journal_test;Database=journal_test;Timeout=1;Pooling=false;SSL Mode=Disable");
        var log = new Capture();
        var journal = new NpgsqlIngestObservability(data, logger: log);
        journal.OnRunStart("journal-diagnostic-fixture", 0, null);
        try
        {
            journal.OnFileStarted("journal-diagnostic-fixture", "fixture.txt", 42, null);
        }
        finally { journal.OnRunFailed("journal-diagnostic-fixture", "failed", "fixture finished"); }

        Assert.True(journal.FailureCount >= 4);
        Assert.IsType<NpgsqlException>(journal.LastFailure);
        Assert.Equal(1, journal.LostFileEvents);
        var fileFailure = Assert.Single(log.Rows, x => x.Fields.TryGetValue("FailureEvent", out var tag)
            && Equals(tag, "INGEST_FILE_JOURNAL_WRITE_FAILED"));
        Assert.Equal(LogLevel.Error, fileFailure.Level);
        var exception = Assert.IsType<NpgsqlException>(fileFailure.Exception);
        Assert.NotNull(exception.InnerException);
        Assert.NotNull(exception.StackTrace);
        Assert.Equal(journal.RunId, fileFailure.Fields["RunId"]);
        Assert.Equal(1L, fileFailure.Fields["LostFileEvents"]);
        Assert.Equal(journal.FailureCount, log.Rows.Count);
    }

    private sealed record Entry(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields);
    private sealed class Capture : ILogger
    {
        public ConcurrentQueue<Entry> Rows { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Rows.Enqueue(new(level, exception,
                ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(x => x.Key, x => x.Value)));
    }
}
