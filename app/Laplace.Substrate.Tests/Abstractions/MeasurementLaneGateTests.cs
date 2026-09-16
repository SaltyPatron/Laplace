using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The measurement lane is mutual exclusion between "something is writing to the
/// substrate" and "something is timing it": ingest holds it SHARED for a run's lifetime,
/// a measurement holds it EXCLUSIVE for its duration, and Postgres conflicts the two
/// modes. Four independent facts have to line up — same class, same key, opposite modes,
/// session scope — and the compiler checks none of them.
///
/// <para>MEASURED 2026-08-15, the failure this exists to stop: generation.compose_batch
/// returned 81,701 ms to 316,998 ms across one session for near-identical code with an
/// ingest active throughout; quiesced, realize.resolve_name read 36,000 -&gt; 0 ms and
/// generation.separator_ids 9,450 -&gt; 11 ms with no code change. Both had been recorded
/// as defects and both were contention. Drift here does not fail loudly — it silently
/// returns the lane to that state, and every number taken afterwards looks exactly as
/// trustworthy as a real one.</para>
///
/// <para>Run liveness is read through the canonical SQL predicate. Its advisory
/// beacon must agree with the C# writer, while the quiet shell delegates to that
/// predicate rather than maintaining another lock/heartbeat interpretation.</para>
///
/// If one of these fails, fix the divergence, never the fixture.
/// </summary>
public class MeasurementLaneGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(Root, Path.Combine(parts));
        Assert.True(File.Exists(path), $"not found: {path}");
        return File.ReadAllText(path);
    }

    private static string LockHome() =>
        Read("app", "Laplace.Substrate", "Crud", "Npgsql", "AdvisoryTxLock.cs");

    private static string Observability() =>
        Read("app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlIngestObservability.cs");

    private static string Runner() =>
        Read("app", "Laplace.Substrate", "Crud", "Npgsql", "MeasurementLane.cs");

    private static string Wrapper() => Read("scripts", "measure-lane.sh");

    private static string QuietGate() => Read("scripts", "wait-for-quiet-substrate.sh");

    private static int CsharpHexConst(string source, string name)
    {
        var m = Regex.Match(source, @"\b" + Regex.Escape(name) + @"\s*=\s*0x([0-9A-Fa-f]+)\s*;");
        Assert.True(m.Success, $"C# constant {name} not found as a hex literal");
        return Convert.ToInt32(m.Groups[1].Value, 16);
    }

    private static string RunLiveness() =>
        Read("extension", "laplace_substrate", "sql", "functions", "ops", "ingest_runs.sql.in");

    private static int SqlRunLivenessClass(string source)
    {
        var predicate = Regex.Match(source,
            @"CREATE OR REPLACE FUNCTION ops\.ingest_run_live\([\s\S]*?BEGIN ATOMIC(?<body>[\s\S]*?)END;");
        Assert.True(predicate.Success, "canonical ingest-run liveness predicate is missing");
        var identity = Regex.Match(predicate.Groups["body"].Value, @"\bl\.classid\s*=\s*([0-9]+)::oid");
        Assert.True(identity.Success, "canonical liveness advisory class is missing");
        Assert.Contains("l.objsubid = 2", predicate.Groups["body"].Value);
        Assert.Contains("hashtext(j.run_id::text)", predicate.Groups["body"].Value);
        Assert.Contains("current_database()", predicate.Groups["body"].Value);
        return int.Parse(identity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// ONE definition of the lane's identity, in the sanctioned lock home. The first cut
    /// of this feature kept a C# copy and a shell copy in step with a parity test; a
    /// parity test only proves two copies agree TODAY, and the copy is what makes drift
    /// possible at all. IngestMutexGateTests rejected the shell copy on 2026-08-16 and it
    /// was right — this asserts the copy never comes back.
    /// </summary>
    [Fact]
    public void LaneIdentity_IsDefinedExactlyOnce()
    {
        // Split so THIS file does not contain the literal it searches for. An exemption
        // for the gate's own source would be the first hole in a single-definition rule.
        var laneClassLiteral = "0x4C50" + "4C4E";
        var defs = new List<string>();
        foreach (var dir in new[] { "app", "scripts", "extension" })
        {
            var root = Path.Combine(Root, dir);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (f.Contains("/obj/") || f.Contains("/bin/")) continue;
                var ext = Path.GetExtension(f);
                if (ext is not (".cs" or ".sh" or ".py" or ".sql" or ".in" or ".ps1" or ".cmd")) continue;
                var text = File.ReadAllText(f);
                // The literal itself, in code or in shell arithmetic. Comments quoting
                // "LPLN" are prose and are not a definition.
                if (text.Contains(laneClassLiteral, StringComparison.OrdinalIgnoreCase))
                    defs.Add(Path.GetRelativePath(Root, f));
            }
        }
        Assert.True(defs.Count == 1,
            "the measurement lane's class must be defined exactly once (AdvisoryTxLock.cs); found in: "
            + string.Join(", ", defs));
        Assert.Equal("app/Laplace.Substrate/Crud/Npgsql/AdvisoryTxLock.cs", defs[0]);
    }

    /// <summary>
    /// The lane must not share a class with the per-run liveness beacon. LPLK is keyed
    /// hashtext(run_id::text) — an arbitrary int32 that can equal the lane's fixed key —
    /// so a shared class would let one run's beacon collide with the lane and either
    /// block every measurement or, worse, satisfy one.
    /// </summary>
    [Fact]
    public void MeasurementLane_DoesNotShareAClassWithRunLiveness()
    {
        Assert.NotEqual(
            CsharpHexConst(Observability(), "RunLivenessLockClass"),
            CsharpHexConst(LockHome(), "MeasurementLaneLockClass"));
    }

    [Fact]
    public void RunLivenessClass_AgreesBetweenCsharpAndCanonicalSql()
    {
        Assert.Equal(
            CsharpHexConst(Observability(), "RunLivenessLockClass"),
            SqlRunLivenessClass(RunLiveness()));
        var gate = QuietGate();
        Assert.Contains("FROM ops.ingest_reconcile_orphans(", gate);
        Assert.Contains("AND ops.ingest_run_live(j.run_id,", gate);
        Assert.DoesNotContain("pg_locks", gate);
        Assert.DoesNotMatch(@"\bLOCK_CLASS\s*=", gate);
    }

    /// <summary>
    /// Fresh-db deliberately leaves a reachable empty database between nuke and migration.
    /// The quiet gate must distinguish that bootstrap state from a broken observer: only a
    /// successful exact to_regclass probe may classify the missing ingest journal as quiet,
    /// and that proof has to occur before any query dereferences the journal itself.
    /// </summary>
    [Fact]
    public void QuietGate_PreSchemaBootstrapIsProvenWithoutFailingOpen()
    {
        var gate = QuietGate();
        var probe = gate.IndexOf("to_regclass('laplace.ingest_run_journal')", StringComparison.Ordinal);
        var provenMissing = gate.IndexOf("[ \"$probe_rc\" -eq 0 ] && [ \"$probe\" = \"missing\" ]", StringComparison.Ordinal);
        var journalRead = gate.IndexOf("FROM laplace.ingest_run_journal;", StringComparison.Ordinal);

        Assert.True(probe >= 0, "quiet gate no longer proves whether the exact ingest journal exists");
        Assert.True(provenMissing > probe, "missing journal may be accepted without a successful PostgreSQL probe");
        Assert.Contains("2>&1) || probe_rc=$?", gate[..provenMissing]);
        Assert.True(journalRead > provenMissing, "quiet gate dereferences the ingest journal before handling pre-schema bootstrap");
        Assert.Contains("[ \"$probe_rc\" -eq 0 ] && [ \"$probe\" = \"present\" ]", gate);
        Assert.Contains("[ \"$capability_rc\" -eq 0 ] && [[ \"$capability\" =~ ^([0-9]+)\\ (present|missing)$ ]]", gate);
        Assert.Contains("[ \"$canonical_ready\" -eq 1 ]", gate);
        Assert.Contains("[ \"$state_rc\" -eq 0 ]", gate);
        Assert.Contains("if [[ \"$live\" =~ ^[0-9]+$ ]]; then", gate);
        Assert.Contains("substrate not proven quiet after", gate);
        Assert.DoesNotContain("*relation*does not exist*", gate);
    }

    /// <summary>
    /// The modes are the whole mechanism. Ingest SHARED lets any number of ingests run
    /// together — exclusive there would serialise ingest against ingest, the exact thing
    /// wait-for-quiet-substrate.sh's header refuses to do. Measurement EXCLUSIVE is what
    /// actually empties the substrate; shared there would leave the lane looking correct
    /// while ingests wrote straight through it.
    /// </summary>
    [Fact]
    public void Ingest_TakesTheLaneShared()
        => Assert.Matches(@"HoldMeasurementLaneAsync\(\s*conn,\s*exclusive:\s*false", Observability());

    [Fact]
    public void Measurement_TakesTheLaneExclusive()
        => Assert.Matches(@"HoldMeasurementLaneAsync\(\s*conn,\s*exclusive:\s*true", Runner());

    /// <summary>
    /// SESSION scope, not transaction. The runner holds the lane across a child process
    /// that runs no transaction of its own, and an ingest holds it across thousands; an
    /// _xact_ variant would release at the first COMMIT and the lane would be empty for
    /// the whole measurement while still reading as held.
    /// </summary>
    [Fact]
    public void Lane_IsSessionScoped()
    {
        var m = Regex.Match(LockHome(),
            @"exclusive\s*\?\s*""(pg_advisory\w*)""\s*:\s*""(pg_advisory\w*)""");
        Assert.True(m.Success, "the lane's two lock functions are no longer a visible pair in AdvisoryTxLock");
        Assert.Equal("pg_advisory_lock", m.Groups[1].Value);
        Assert.Equal("pg_advisory_lock_shared", m.Groups[2].Value);
    }

    /// <summary>
    /// The wrapper stays a wrapper. If it grows its own acquisition again it becomes a
    /// second mutex with a second copy of the lane's identity — the thing
    /// IngestMutexGateTests already refused once.
    /// </summary>
    [Fact]
    public void Wrapper_DoesNotTakeTheLockItself()
        => Assert.DoesNotMatch(new Regex(@"pg_advisory\w*_lock\w*\s*\("), Wrapper());
}
