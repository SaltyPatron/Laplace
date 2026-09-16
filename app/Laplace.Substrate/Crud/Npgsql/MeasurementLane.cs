using System.Diagnostics;
using System.Linq;
using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Run a measurement only when the substrate is observably quiet.
///
/// Measurements are diagnostic work. They must never stop ingestion or serving in
/// order to manufacture a quiet benchmark. A measurement refuses to start when live
/// ingest is observed and invalidates itself if ingest starts before it finishes.
/// Product work always wins.
/// </summary>
public static class MeasurementLane
{
    /// <summary>
    /// Compatibility entry point retained for existing callers. The old implementation
    /// acquired an exclusive advisory lock that forced ingestion to wait. This implementation
    /// never acquires that lock: it verifies quiet before and after the child instead.
    /// </summary>
    public static async Task<int> RunExclusiveAsync(
        string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest);
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);

        await RefuseIfIngestRunningAsync(conn, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{file}'");

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        // A benchmark that overlapped product writes is not evidence. Reject the
        // measurement after the fact rather than ever making product work wait for it.
        await RefuseIfIngestRunningAsync(conn, ct).ConfigureAwait(false);
        return proc.ExitCode;
    }

    /// <summary>
    /// Throw if a journalled ingest is live, naming it. A probe failure is not proof of
    /// quiet. This method intentionally refuses the measurement; it does not control or
    /// block the ingest process.
    /// </summary>
    private static async Task RefuseIfIngestRunningAsync(NpgsqlConnection conn, CancellationToken ct)
    {
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
            if (a.Count == 0) return;

            await using (var busy = conn.CreateCommand())
            {
                busy.CommandText =
                    "SELECT count(*) FROM pg_stat_activity "
                    + "WHERE datname = current_database() AND state = 'active' "
                    + "  AND pid <> pg_backend_pid() "
                    + "  AND query ~* 'consensus\\.upsert|COPY laplace\\.|highway_mask_deposit"
                    + "|attestations_exist|physicalities_exist'";
                var n = Convert.ToInt64(await busy.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                if (n > 0)
                    throw new InvalidOperationException(
                        $"measurement refused: {n} backend(s) executing ingest work; "
                        + string.Join(", ", a.Values.Select(v => $"{v.src} ({v.done}/{(v.total > 0 ? v.total : -1)})")));
            }

            await Task.Delay(SettleMs, ct).ConfigureAwait(false);
            b = await ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "measurement refused: could not establish whether an ingest is running "
                + $"({ex.Message})", ex);
        }

        var advancing = b
            .Where(kv => a.TryGetValue(kv.Key, out var prev) && kv.Value.done > prev.done)
            .Select(kv => $"{kv.Value.src} ({kv.Value.done}/{(kv.Value.total > 0 ? kv.Value.total : -1)})")
            .ToList();

        if (advancing.Count > 0)
            throw new InvalidOperationException(
                $"measurement refused: ingest advancing — {string.Join(", ", advancing)}");

        // A non-advancing running row may be a corpse or a run between batches. Do not
        // block product work on it; report that the measurement is proceeding without a
        // claim of enforced exclusivity.
        if (a.Count > 0)
            Console.Error.WriteLine(
                $"::warning::{a.Count} journal row(s) read 'running' but did not advance in "
                + $"{SettleMs} ms; measurement is not exclusive and must be discarded if product work resumes.");
    }
}
