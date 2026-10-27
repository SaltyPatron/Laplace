using System.Runtime.InteropServices;
using Xunit;

namespace Laplace.Ingestion.Tests;

[CollectionDefinition("process-signal", DisableParallelization = true)]
public sealed class ProcessSignalCollection;

[Collection("process-signal")]
public sealed class IngestSignalHandlingTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [SkippableTheory]
    [InlineData(15)]
    [InlineData(2)]
    public void ProcessSignalCancelsTheProductionIngestScope(int signal)
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            || OperatingSystem.IsFreeBSD(), "POSIX signals require a Unix process");
        using var scope = new IngestCancellationScope();
        Assert.False(scope.Token.IsCancellationRequested);
        Assert.Equal(0, kill(Environment.ProcessId, signal));
        Assert.True(SpinWait.SpinUntil(() => scope.Token.IsCancellationRequested,
            TimeSpan.FromSeconds(5)), "The ingest did not receive process cancellation");
    }

    [Fact]
    public void CallerCancellationReachesTheSameScope()
    {
        using var caller = new CancellationTokenSource();
        using var scope = new IngestCancellationScope(caller.Token);
        caller.Cancel();
        Assert.True(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public void AlreadyCancelledCallerRemainsCancelled()
    {
        using var scope = new IngestCancellationScope(new CancellationToken(true));
        Assert.True(scope.Token.IsCancellationRequested);
    }
}
