using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Npgsql;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Laplace.Endpoints.Mcp.Tests;

[Collection("MCP process diagnostics")]
public sealed class WorkspaceFailureDiagnosticTests
{
    [Fact]
    public async Task OptionalPeerFailureRetainsTheActualDatabaseCauseAndRequestContext()
    {
        var previous = Log.Logger;
        var events = new CapturedEvents();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(events).CreateLogger();
        Log.Logger = logger;
        try
        {
            await using var database = NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Username=validation;Database=not_serving;Timeout=1;SSL Mode=Disable");
            using (LogContext.PushProperty("CorrelationId", "workspace-peer-request"))
                Assert.Null(await WorkspaceCodeIndex.ResolveSemanticPeersAsync(database, [UniqueWord()]));
            var diagnostic = Assert.Single(events.Rows, row => row.MessageTemplate.Text.StartsWith("MCP workspace peer lookup failed", StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Warning, diagnostic.Level);
            Assert.IsAssignableFrom<NpgsqlException>(diagnostic.Exception);
            Assert.Equal("\"workspace-peer-request\"", diagnostic.Properties["CorrelationId"].ToString());
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task CallerCancellationCannotBeReportedAsNoSemanticPeers()
    {
        await using var database = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=validation;Database=not_serving;Timeout=1;SSL Mode=Disable");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkspaceCodeIndex.ResolveSemanticPeersAsync(database, [UniqueWord()], cancelled.Token));
    }

    [Fact]
    public void FailedDiscoveryCannotReturnAnEmptySuccessfulInventory()
    {
        string missing = Path.Combine(Path.GetTempPath(), "missing-workspace-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => WorkspaceCodeIndex.DiscoverSourceFiles(missing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInvalidDeclaredWorkspaceCannotSelectADifferentCheckout(bool file)
    {
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_ROOT");
        string selected = Path.Combine(Path.GetTempPath(), "workspace-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (file) File.WriteAllText(selected, "not a directory");
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", selected);
            if (file) Assert.Throws<InvalidDataException>(() => WorkspaceCodeIndex.ResolveWorkspaceRoot());
            else Assert.Throws<FileNotFoundException>(() => WorkspaceCodeIndex.ResolveWorkspaceRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", previous);
            if (file) File.Delete(selected);
        }
    }

    [Fact]
    public void AReadableEmptyWorkspaceStillProducesAnEmptyInventory()
    {
        string root = Path.Combine(Path.GetTempPath(), "empty-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.Empty(WorkspaceCodeIndex.DiscoverSourceFiles(root)); }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void ALockedSourceFileCannotProduceASuccessfulEmptyReindex()
    {
        string? previousRoot = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_ROOT");
        var previousLog = Log.Logger;
        string root = Path.Combine(Path.GetTempPath(), "locked-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var events = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(events).CreateLogger();
        try
        {
            string path = Path.Combine(root, "locked.cs");
            File.WriteAllText(path, "public class InputFixture { }");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", root);
            Log.Logger = logger;
            var failure = Assert.Throws<AggregateException>(() => WorkspaceCodeIndex.EnsureIndex(forceReindex: true));
            Assert.All(failure.InnerExceptions, error => Assert.IsAssignableFrom<IOException>(error));
            var diagnostic = Assert.Single(events.Rows, row => row.MessageTemplate.Text.StartsWith("MCP workspace indexing failed", StringComparison.Ordinal));
            Assert.IsAssignableFrom<IOException>(diagnostic.Exception);
            Assert.Contains(path, diagnostic.RenderMessage());
        }
        finally
        {
            Log.Logger = previousLog;
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Hash128 UniqueWord() => Hash128.FromBytes(Guid.NewGuid().ToByteArray());

    [Fact]
    public void ARecentCacheCannotOverrideANewExplicitWorkspaceRoot()
    {
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_ROOT");
        string parent = Path.Combine(Path.GetTempPath(), "workspace-selection-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(parent, "first"), second = Path.Combine(parent, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", first);
            Assert.Equal(first, WorkspaceCodeIndex.EnsureIndex(forceReindex: true).RootPath);
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", second);
            Assert.Equal(second, WorkspaceCodeIndex.EnsureIndex(forceReindex: false).RootPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", previous);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void AFailedRebuildCannotChangeTheLastSuccessfulSnapshotsFileCount()
    {
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_SOURCE_ROOT");
        string parent = Path.Combine(Path.GetTempPath(), "workspace-publication-" + Guid.NewGuid().ToString("N"));
        string good = Path.Combine(parent, "good"), rejected = Path.Combine(parent, "rejected");
        Directory.CreateDirectory(good);
        Directory.CreateDirectory(rejected);
        try
        {
            File.WriteAllText(Path.Combine(good, "empty.cs"), "   ");
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", good);
            Assert.Equal(1, WorkspaceCodeIndex.EnsureIndex(forceReindex: true).FileCount);
            string blocked = Path.Combine(rejected, "blocked.cs");
            File.WriteAllText(blocked, "public class RejectedInput { }");
            using (var locked = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", rejected);
                Assert.Throws<AggregateException>(() => WorkspaceCodeIndex.EnsureIndex(forceReindex: true));
            }
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", good);
            var retained = WorkspaceCodeIndex.EnsureIndex(forceReindex: false);
            Assert.Equal(good, retained.RootPath);
            Assert.Equal(1, retained.FileCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SOURCE_ROOT", previous);
            Directory.Delete(parent, recursive: true);
        }
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Rows { get; } = new();
        public void Emit(LogEvent logEvent) => Rows.Enqueue(logEvent);
    }
}
