using System.Diagnostics;
using System.Linq;
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

        // THE LANE LOCK IS NOT SUFFICIENT ON ITS OWN, and assuming it was cost real
        // throughput. An ingest only takes the shared lane if its binary contains
        // NpgsqlIngestObservability's acquire — so any run started from an older build, a
        // different branch, or a deployed release sails straight through an "exclusive"
        // lane that reports itself held. MEASURED 2026-08-16: a ConceptNet ingest launched
        // from main wrote continuously while this wrapper reported the lane held, and a
        // 30,000-id probe workload ran against a 231M-row table on top of it.
        //
        // So the journal is checked too, on the same authority scripts/wait-for-quiet-
        // substrate.sh uses: status='running' BELIEVED only when the run also holds its
        // per-run liveness lock (LPLK, objid = hashtext(run_id)), which the server drops
        // the instant the holder dies. That makes a corpse unable to block a measurement
        // while a live run of ANY vintage does.
        await RefuseIfIngestRunningAsync(conn, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Throw if a journalled ingest is live, naming it. FAIL CLOSED: a probe that cannot
    /// answer is treated as busy, because an unreachable or misconfigured database is not
    /// proof of quiet — the same law wait-for-quiet-substrate.sh's header records after
    /// its <c>|| echo 0</c> collapsed "the database says zero" into "the probe did not run".
    /// </summary>
    private static async Task RefuseIfIngestRunningAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // PROGRESS IS THE LIVENESS SIGNAL, not a beacon. The first cut of this asked whether
        // a 'running' row also held its LPLK liveness lock, the way
        // wait-for-quiet-substrate.sh does, and it FAILED ON ITS FIRST REAL TEST: measured
        // 2026-08-16, a ConceptNet ingest that was demonstrably writing (input_units_done
        // climbing 6.0M -> 6.3M -> 7.4M) held NO advisory lock at all — pg_locks returned
        // zero rows for the database — so the check read it as a corpse and let the
        // measurement run straight over it.
        //
        // That is not a corner case. The beacon is a session lock on a dedicated pooled
        // connection; if that connection is pruned or dropped the lock dies while the run
        // continues, and the acquire path logs INGEST_RUN_LIVENESS_LOCK_FAILED and proceeds
        // by design. So its ABSENCE proves nothing about the run.
        //
        // A counter that advances cannot be faked by a corpse and cannot be dropped by a
        // pool. Sample input_units_done, wait, sample again: movement means live, full stop,
        // whatever the run's binary vintage or lock state. This is strictly stronger than the
        // beacon — it also excludes the corpse the beacon was invented to detect.
        const int SettleMs = 1500;
        string Snapshot() =>
            "SELECT coalesce(string_agg(j.run_id::text || '|' || j.source_name || '|' "
            + "  || j.input_units_done || '|' || j.input_units_total, E'\\n' ORDER BY j.run_id), '') "
            + "FROM laplace.ingest_run_journal j WHERE j.status = 'running'";

        async Task<Dictionary<string, (string src, long done, long total)>> ReadAsync()
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Snapshot();
            var raw = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string ?? string.Empty;
            var map = new Dictionary<string, (string, long, long)>();
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('|');
                if (f.Length == 4 && long.TryParse(f[2], out var d) && long.TryParse(f[3], out var t))
                    map[f[0]] = (f[1], d, t);
            }
            return map;
        }

        Dictionary<string, (string src, long done, long total)> a, b;
        try
        {
            a = await ReadAsync().ConfigureAwait(false);
            if (a.Count == 0) return;                      // no 'running' row at all: quiet
            await Task.Delay(SettleMs, ct).ConfigureAwait(false);
            b = await ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // FAIL CLOSED. An unreachable or misconfigured database is not proof of quiet —
            // the law wait-for-quiet-substrate.sh's header records after its `|| echo 0`
            // collapsed "the database says zero" into "the probe did not run".
            throw new InvalidOperationException(
                "measurement refused: could not establish whether an ingest is running "
                + $"({ex.Message}). An unanswerable probe is not proof of quiet.", ex);
        }

        var advancing = b
            .Where(kv => a.TryGetValue(kv.Key, out var prev) && kv.Value.done > prev.done)
            .Select(kv => $"{kv.Value.src} ({kv.Value.done}/{kv.Value.total})")
            .ToList();

        if (advancing.Count > 0)
            throw new InvalidOperationException(
                $"measurement refused: ingest ADVANCING — {string.Join(", ", advancing)}. "
                + "The lane lock does not stop a run whose binary predates it, and the liveness "
                + "beacon can be absent under a live run; a moving counter cannot. Measuring "
                + "across a live write produces numbers that are not measurements of the code.");

        // A 'running' row that did NOT advance is reported, not obeyed: it may be a corpse
        // (the case the beacon exists for) or a run between batches. Naming it keeps the
        // operator from reading silence as proof.
        if (a.Count > 0)
            Console.Error.WriteLine(
                $"::warning::{a.Count} journal row(s) read 'running' but did not advance in "
                + $"{SettleMs} ms — treating as not-in-flight. If a seed is mid-batch, re-run this "
                + "measurement rather than trusting it.");
    }
}
