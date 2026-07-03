using Xunit;
using Laplace.Ingestion;

namespace Laplace.Ingestion.Tests;

public class TransientErrorRetryPolicyTests
{
    [Fact]
    public void Default_HasExpectedShape()
    {
        var p = TransientErrorRetryPolicy.Default;
        Assert.Equal(3, p.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), p.InitialDelay);
        Assert.Equal(10.0, p.BackoffMultiplier);
        Assert.Equal(0.1, p.JitterFraction);
    }

    [Fact]
    public void NoRetry_HasOneAttempt()
    {
        Assert.Equal(1, TransientErrorRetryPolicy.NoRetry.MaxAttempts);
        Assert.False(TransientErrorRetryPolicy.NoRetry.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void DelayBeforeAttempt_GrowsExponentially()
    {
        var p = TransientErrorRetryPolicy.Default with { JitterFraction = 0 };
        var rng = new Random(0);
        var d0 = p.DelayBeforeAttempt(0, rng);
        var d1 = p.DelayBeforeAttempt(1, rng);
        var d2 = p.DelayBeforeAttempt(2, rng);
        Assert.Equal(100.0, d0.TotalMilliseconds, 1);
        Assert.Equal(1000.0, d1.TotalMilliseconds, 1);
        Assert.Equal(10000.0, d2.TotalMilliseconds, 1);
    }

    [Fact]
    public void DelayBeforeAttempt_RespectsJitter()
    {
        var p = TransientErrorRetryPolicy.Default;
        var rng = new Random(0);
        for (int i = 0; i < 20; i++)
        {
            var d = p.DelayBeforeAttempt(0, rng);
            Assert.InRange(d.TotalMilliseconds, 90.0, 110.0);
        }
    }

    [Fact]
    public void DefaultIsTransient_ClassifiesTimeoutAsTransient()
    {
        Assert.True(TransientErrorRetryPolicy.Default.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void DefaultIsTransient_RejectsArbitraryException()
    {
        Assert.False(TransientErrorRetryPolicy.Default.IsTransient(new ArgumentException()));
    }
}
