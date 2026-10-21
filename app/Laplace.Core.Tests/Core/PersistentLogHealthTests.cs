using Laplace.Ops;
using Xunit;

namespace Laplace.Core.Tests;

public sealed class PersistentLogHealthTests
{
    [Fact]
    public void UninitializedOrFailedPersistentLoggingCannotReportReady()
    {
        var health = new PersistentLogHealth();
        Assert.False(health.Snapshot().Ready);
        health.Initialized();
        Assert.True(health.Snapshot().Ready);
        health.Failed();
        health.Failed();
        var failed = health.Snapshot();
        Assert.True(failed.Initialized);
        Assert.False(failed.Ready);
        Assert.Equal(2, failed.FailureCount);
        Assert.NotNull(failed.LastFailureUtc);
        health.Initialized();
        Assert.False(health.Snapshot().Ready);
    }
}
