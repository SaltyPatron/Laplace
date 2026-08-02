using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class IngestConsoleModeTests : IDisposable
{
    public IngestConsoleModeTests() => IngestConsoleMode.Override(null);
    public void Dispose() => IngestConsoleMode.Override(null);

    [Fact]
    public void Ci_Suppresses_PerFile_Lines()
    {
        IngestConsoleMode.Override(IngestConsoleVerbosity.Ci);
        Assert.False(MultiFileTelemetry.ShouldLogFileLine(1, 14900));
        Assert.False(MultiFileTelemetry.ShouldLogFileLine(256, 14900));
        Assert.Equal(30_000, IngestConsoleMode.ProgressMinIntervalMs);
    }

    [Fact]
    public void Verbose_Logs_Every_File()
    {
        IngestConsoleMode.Override(IngestConsoleVerbosity.Verbose);
        Assert.True(MultiFileTelemetry.ShouldLogFileLine(7, 14900));
    }

    [Fact]
    public void Progress_Samples_Large_Corpora()
    {
        IngestConsoleMode.Override(IngestConsoleVerbosity.Progress);
        Assert.True(MultiFileTelemetry.ShouldLogFileLine(1, 14900));
        Assert.False(MultiFileTelemetry.ShouldLogFileLine(2, 14900));
        Assert.True(MultiFileTelemetry.ShouldLogFileLine(256, 14900));
    }
}
