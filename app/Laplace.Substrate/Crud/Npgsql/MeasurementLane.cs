using System.Diagnostics;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Run a command holding the measurement lane EXCLUSIVE, so nothing writes to the
/// substrate while it is timed.
///
/// <para>WHY. A wall-clock number taken while an ingest is writing is not a measurement
/// of the code. MEASURED 2026-08-15: <c>generation.compose_batch('what is a wolf', 12)</c>
/// returned 264,605 / 244,533 / 81,701 / 316,998 / 144,256 / 144,947 / 153,203 / 301,571
/// ms across one session for near-identical code, with an ingest run active throughout —
/// a 3.9x spread, and causal claims were drawn from single runs inside it. Re-measured on
/// a quiet database, <c>realize.resolve_name</c> read 36,000 -&gt; 0 ms and
/// <c>generation.separator_ids</c> 9,450 -&gt; 11 ms with NO code change. Both had been
/// recorded as defects. Both were contention.</para>
///
/// <para>WHY A LOCK AND NOT A CHECK. docs/sql-refactor-tasklist.md already states the
/// precondition — "confirm no ingest run is active, then measure each variant at least 3
/// times" — as an operator discipline. A discipline is checked once at the start; an
/// ingest dispatched a minute later still lands mid-measurement, and the resulting number
/// is indistinguishable from a valid one. Exclusion has to be a property of the system.</para>
///
/// <para>The lane is session-scoped, so the server releases it the instant this process
/// dies — no cleanup path has to run, and a killed measurement cannot wedge every
/// subsequent ingest.</para>
/// </summary>
public static class MeasurementLane
{
    /// <summary>
    /// Hold the lane exclusive and run <paramref name="file"/> with <paramref name="args"/>,
    /// returning its exit code. The child inherits stdio, so a measurement's output is
    /// unchanged by being run under the lane.
    /// </summary>
    public static async Task<int> RunExclusiveAsync(
        string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);

        await AdvisoryTxLock.HoldMeasurementLaneAsync(
            conn, exclusive: true,
            onWaiting: msg => Console.Error.WriteLine($"::notice::{msg}"),
            ct: ct).ConfigureAwait(false);

        Console.Error.WriteLine("measurement lane held — substrate is exclusive to this run");

        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{file}'");

        // The lane outlives the child by exactly the connection's dispose, below. A child
        // that spawns its own background work and returns early is therefore NOT covered —
        // measure the thing that blocks, not a launcher.
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode;
    }
}
