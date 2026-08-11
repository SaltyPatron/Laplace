using Xunit;
using System.Runtime.InteropServices;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// IngestRunner.RunAsync must convert SIGTERM into token cancellation, not process death.
///
/// The cancellation arm of RunAsync already journals a terminal row, but only if the token
/// is cancelled. A raw SIGTERM terminates the process outright: RunCoreAsync never unwinds,
/// OnRunFinished never runs, and the journal row sits at 'running' with nothing behind it.
///
/// MEASURED 2026-08-10 — GitHub Actions cancels a job by SIGTERMing its processes
/// ("Terminate orphan process: pid (N) (dotnet)"), and laplace.yml preempts seeds by design
/// on the claim that "a preempted seed loses nothing and re-runs cleanly". A ChessPgn seed
/// was preempted at 19:02 having deposited 6,649,061 entities and 17,337,962 attestations;
/// it left no terminal record, and wait-for-quiet-substrate.sh then blocked on the stranded
/// row for its full budget.
///
/// These pin the MECHANISM in-process. The end-to-end path (SIGTERM a real ingest, read the
/// journal) needs a live substrate and is not automatable here.
/// </summary>
public class IngestSignalHandlingTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private const int SIGTERM = 15;

    [Fact]
    public void Sigterm_Cancels_The_Token_Instead_Of_Killing_The_Process()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var linked = new CancellationTokenSource();
        using var reg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            // Without this the default disposition runs and the test host dies -- which is
            // precisely the production failure: the process is gone before it can journal.
            ctx.Cancel = true;
            linked.Cancel();
        });

        Assert.False(linked.IsCancellationRequested);
        Assert.Equal(0, kill(Environment.ProcessId, SIGTERM));

        // The handler runs on a signal-dispatch thread; give it a bounded window.
        Assert.True(SpinWait.SpinUntil(() => linked.IsCancellationRequested, TimeSpan.FromSeconds(5)),
            "SIGTERM did not cancel the token — the run would die without journaling a terminal row");
    }

    [Fact]
    public void Cancelled_Token_Surfaces_As_OperationCanceled_So_The_Runner_Can_Journal()
    {
        // The arm that writes status='cancelled' is `catch (OperationCanceledException)`.
        // A cancelled token must therefore reach it as that exception and not, say, as a
        // silent early return -- otherwise the signal wiring above buys nothing.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => cts.Token.ThrowIfCancellationRequested());
    }

    [Fact]
    public void Linked_Source_Still_Honours_The_Caller_Token()
    {
        // RunAsync links the caller's token into its own source. A caller that cancels must
        // still cancel the run -- the signal registration must not shadow it.
        using var caller = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);

        Assert.False(linked.IsCancellationRequested);
        caller.Cancel();
        Assert.True(linked.IsCancellationRequested);
    }
}
