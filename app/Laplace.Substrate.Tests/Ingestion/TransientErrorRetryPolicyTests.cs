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

    [Theory]
    [InlineData("40P01", true)]
    [InlineData("40001", true)]
    [InlineData("23505", false)]
    public void ConcurrencyRetry_RetriesOnlyConcurrencySqlStates(string sqlState, bool expected)
    {
        Assert.Equal(expected, TransientErrorRetryPolicy.IsConcurrencySqlState(sqlState));
    }
}

/// <summary>
/// The cancelled-async-read gap, MEASURED 2026-08-16. ConceptNet aborted at 4,304,844 of
/// 31,085,888 units after 33 minutes on
/// <c>XX000: could not read blocks 146..161 in file "base/3061870/3063304": Operation
/// canceled</c> (relfilenode 3063304 = <c>laplace.entities_t3_h0</c>). The cluster runs
/// <c>io_method = io_uring</c>; a cancelled in-flight read surfaces as ECANCELED and
/// Postgres wraps it as XX000, which the transient classes (08/40/53/57/58) do not cover,
/// so a retryable cancellation killed a 31M-unit ingest on its first attempt. The
/// partition read clean afterwards — 3,958,231 rows, and the failing block range returns
/// 1,843.
///
/// <para>These tests exist in BOTH directions because the danger is symmetric: not
/// retrying costs a 33-minute run, and retrying all of XX000 would turn a genuinely
/// damaged relation into a silent loop. Only the cancellation phrasing may pass.</para>
/// </summary>
public class CancelledAsyncReadRetryTests
{
    private static Exception Pg(string sqlState, string messageText)
    {
        // PostgresException's public ctor takes the fields the classifier reads.
        return new global::Npgsql.PostgresException(
            messageText, "ERROR", "ERROR", sqlState);
    }

    [Theory]
    [InlineData("could not read blocks 146..161 in file \"base/3061870/3063304\": Operation canceled")]
    [InlineData("could not read block 12 in file \"base/1/2\": Operation canceled")]
    [InlineData("COULD NOT READ BLOCKS 1..2 in file \"x\": OPERATION CANCELED")]
    public void CancelledAsyncRead_IsTransient(string msg)
        => Assert.True(TransientErrorRetryPolicy.Default.IsTransient(Pg("XX000", msg)));

    [Theory]
    // Real media failure keeps its own fate — class 58 already covers io_error, and an
    // XX000 phrased as an I/O error must not be laundered into a retry by this change.
    [InlineData("XX000", "could not read blocks 146..161 in file \"base/1/2\": Input/output error")]
    [InlineData("XX000", "could not read block 3 in file \"base/1/2\": Bad address")]
    // Any other internal error stays fatal on the first attempt.
    [InlineData("XX000", "tuple concurrently updated")]
    [InlineData("XX001", "could not read blocks 1..2: Operation canceled")]
    [InlineData("XX002", "index \"x\" contains corrupted page at block 7")]
    public void OtherInternalErrors_StayFatal(string sqlState, string msg)
        => Assert.False(TransientErrorRetryPolicy.Default.IsTransient(Pg(sqlState, msg)));

    [Fact]
    public void GenuineIoError_IsStillTransientViaClass58()
        => Assert.True(TransientErrorRetryPolicy.Default.IsTransient(Pg("58030", "could not read file")));

    [Fact]
    public void NoRetry_StillRefusesTheCancelledRead()
        => Assert.False(TransientErrorRetryPolicy.NoRetry.IsTransient(
            Pg("XX000", "could not read blocks 1..2 in file \"x\": Operation canceled")));

    [Fact]
    public void ConcurrencyRetry_DoesNotClaimTheCancelledRead()
        => Assert.False(TransientErrorRetryPolicy.ConcurrencyRetry.IsTransient(
            Pg("XX000", "could not read blocks 1..2 in file \"x\": Operation canceled")));
}
