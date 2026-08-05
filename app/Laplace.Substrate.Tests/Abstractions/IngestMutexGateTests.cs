using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// ISA gate G10 — one mutex, one verify
/// (<c>docs/specs/37_Substrate_Operation_ISA.md</c> §7: <i>"more than one implementation
/// of the ingest mutex or the evidence_count verify exists"</i>; plan
/// <c>docs/plan/W6_Architecture_Gates.md</c> §3).
///
/// <para><b>W6 recorded the mutex as UNVERIFIED</b> — <i>"the ingest mutex may not exist
/// under the names spec 37 assumes."</i> It does. Measured 2026-08-05, and the counts
/// spec 37 §5 asserts (<c>:328</c>, <i>"6 ingest-mutex + 11 verify implementations"</i>)
/// are exactly right:</para>
/// <list type="bullet">
///   <item><b>6 ingest mutexes</b> = 5 copies of the process-level probe
///     (<c>Get-CimInstance Win32_Process | ... Laplace\.Cli</c>) + 1 database-level
///     implementation (<see cref="ProcessMutexAllowlist"/>,
///     <see cref="DatabaseMutexSanctionedHome"/>).</item>
///   <item><b>11 verify implementations</b> = 11 files hand-rolling
///     <c>evidence_count(…) &gt; 0</c> as "is this source/layer ingested?"
///     (<see cref="EvidenceVerifyAllowlist"/>, 13 call sites).</item>
/// </list>
///
/// <para><b>The database half is already one implementation</b> and W6 does not record
/// that: every ingest transaction takes its lock through
/// <c>AdvisoryTxLock.BeginWithLockAsync</c>, called from exactly one place
/// (<c>NpgsqlWorkingSetApply</c>). The gate keeps it that way rather than reporting a
/// violation.</para>
///
/// <para><b>Where the verify belongs.</b> <c>ops/source_status.sql.in</c>'s own header is
/// the argument, and it names this gate's defect precisely: <i>"There was no standardized
/// answer to the most basic operational question in the system, so every caller assembled
/// one, and every assembly was wrong in a different way … evidence_count(source_id('X'))
/// &gt; 0 says DOCUMENTS ARE NOT INGESTED … This exact false negative has been reached
/// three times."</i> The 11 files below are those callers. They are enumerated, not
/// deleted, because a gate that goes red on merge-day teaches people to ignore it.</para>
/// </summary>
public sealed class IngestMutexGateTests
{
    /// <summary>
    /// The process-level ingest mutex: "a Laplace.Cli ingest is already running".
    /// Byte-identical PowerShell in four <c>.cmd</c> files and a fifth spelling in
    /// <c>bench-matrix.ps1</c>.
    /// </summary>
    private static readonly Regex ProcessMutexProbe = new(
        @"Win32_Process[\s\S]{0,300}?Laplace\\?\.Cli",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The database-level ingest mutex.</summary>
    private static readonly Regex DatabaseAdvisoryLock = new(
        @"pg_advisory(?:_xact)?_lock\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The <c>evidence_count</c> verify: a count compared against zero to decide whether
    /// something was ingested. Whole-file (not line-oriented) because the SQL is spread
    /// over several lines in most of these; bounded by <c>;</c> so it cannot span
    /// statements.
    /// </summary>
    private static readonly Regex EvidenceVerify = new(
        @"evidence_count\s*\([^;]{0,400}?\)\s*>\s*0",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The one sanctioned database mutex implementation. Excluded from the scan the way
    /// <c>ReadPathArchitectureGateTests</c> excludes <c>Crud/Npgsql</c>: this is where the
    /// thing is SUPPOSED to live.
    /// </summary>
    private const string DatabaseMutexSanctionedHome =
        "app/Laplace.Substrate/Crud/Npgsql/AdvisoryTxLock.cs";

    /// <summary>The surface the 11 hand-rolled verifies must migrate onto.</summary>
    private const string VerifySanctionedHome =
        "extension/laplace_substrate/sql/functions/ops/source_status.sql.in";

    /// <summary>
    /// Process-level ingest mutex copies, 2026-08-05. THIS LIST MAY ONLY SHRINK.
    /// All five ask the same question — is any <c>dotnet.exe</c>/<c>Laplace.Cli.exe</c>
    /// running with <c>Laplace.Cli</c> on its command line — and four of them are the same
    /// 180-character PowerShell one-liner pasted verbatim. The destination is one CLI
    /// subcommand (spec 37 §8 OP10), so the roster scripts stop carrying a copy each.
    /// </summary>
    private static readonly HashSet<string> ProcessMutexAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "scripts/win/bench-matrix.ps1",           // throws instead of exiting; same predicate
        "scripts/win/seed-chain.cmd",             // verbatim copy
        "scripts/win/seed-everything.cmd",        // verbatim copy
        "scripts/win/seed-post-wiktionary.cmd",   // verbatim copy
        "scripts/win/seed-step.cmd",              // verbatim copy — the original
    };

    /// <summary>
    /// Advisory-lock call sites OUTSIDE <see cref="DatabaseMutexSanctionedHome"/>,
    /// 2026-08-05. THIS LIST MAY ONLY SHRINK.
    ///
    /// <para><c>highway_mask_deposit</c> is not the ingest mutex — it serializes mask
    /// deposit on its own lock name, and its header records the contention measurement
    /// that justified it. It is listed so that a SECOND SQL-side advisory lock, or any
    /// C# one outside AdvisoryTxLock, is a named failure rather than a quiet second
    /// mutex.</para>
    /// </summary>
    private static readonly HashSet<string> DatabaseMutexAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "extension/laplace_substrate/sql/functions/highway/highway_mask_deposit.sql.in", // 1x @ 101
    };

    /// <summary>
    /// Hand-rolled <c>evidence_count(…) &gt; 0</c> verifies, 2026-08-05: 11 files,
    /// 13 call sites. THIS LIST MAY ONLY SHRINK. Destination is
    /// <see cref="VerifySanctionedHome"/> (<c>laplace.source_status()</c>), which already
    /// answers the question correctly for content-only lanes.
    ///
    /// <para>The C# three are not three different questions — they are the same
    /// layer-complete marker probe written three times:
    /// <c>NpgsqlSubstrateReader.HasSourceCompletedAsync</c>,
    /// <c>NpgsqlIngestOps.LayerMarkedCompleteAsync</c> and its generic sibling
    /// <c>EvidenceExistsForTypeAndSourceAsync</c> all issue
    /// <c>evidence_count(p_type =&gt; canonical_id('substrate/type/HasLayerCompleted/N/v1'),
    /// p_source =&gt; …) &gt; 0</c>. <c>ensure-foundation.sh</c> and
    /// <c>decomposer-ensure-floor.sh</c> carry that same string in shell.</para>
    /// </summary>
    private static readonly HashSet<string> EvidenceVerifyAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // C# — three spellings of the layer-complete marker probe.
        "app/Laplace.Substrate/Crud/Npgsql/NpgsqlIngestOps.cs",         // 2x @ 23,151
        "app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReader.cs",   // 2x @ 19,36
        "app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs",   // 1x @ 137
        // Shell / Python — the seed and gate lanes.
        "scripts/audit-decomposers.sh",                                 // 1x @ 42
        "scripts/decomposer-ensure-floor.sh",                           // 1x @ 16
        "scripts/decomposer-gate-check.py",                             // 1x @ 184
        "scripts/ensure-foundation.sh",                                 // 1x @ 43
        // PowerShell / raw SQL — the operator lane.
        "scripts/sql/substrate-audit.sql",                              // 1x @ 12
        "scripts/win/seed-layer-check-batch.ps1",                       // 1x @ 42
        "scripts/win/seed-layer-check.ps1",                             // 1x @ 41
        "scripts/win/sql/chess-test-status.sql",                        // 1x @ 33
    };

    /// <summary>
    /// Ratchet ceilings, measured 2026-08-05. Compile-time consts on purpose (W6 D2):
    /// a ceiling in generated data is a ceiling nobody reviews. Never raise.
    /// </summary>
    private const int ProcessMutexCeiling = 5;

    /// <inheritdoc cref="ProcessMutexCeiling"/>
    private const int DatabaseMutexCeiling = 1;

    /// <inheritdoc cref="ProcessMutexCeiling"/>
    private const int EvidenceVerifyCeiling = 11;

    /// <inheritdoc cref="ProcessMutexCeiling"/>
    /// <remarks>Second dimension: an allowlisted file must not GROW its verify count.</remarks>
    private const int EvidenceVerifySiteCeiling = 13;

    private static readonly string[] ScanRoots = ["app", "scripts", "extension"];

    private static readonly string[] CLike = [".cs", ".c", ".h"];
    private static readonly string[] SqlLike = [".sql", ".sql.in"];
    private static readonly string[] HashLike = [".sh", ".py", ".ps1"];
    private static readonly string[] RemLike = [".cmd", ".bat"];

    private static readonly Regex CmdRemComment = new(
        @"(?im)^[ \t]*(?:rem\b|::).*$", RegexOptions.Compiled);

    /// <summary>
    /// Strip comments so the counts describe code, not prose about code. Necessary here
    /// and not merely tidy: <c>AdvisoryTxLock.cs</c>'s own doc comment quotes
    /// <c>pg_advisory_xact_lock(...)</c>, and <c>source_status.sql.in</c>'s header quotes
    /// <c>evidence_count(source_id('X')) &gt; 0</c> as the anti-pattern it replaces.
    /// Without the strip both would be counted as the thing they warn about.
    /// </summary>
    private static string Strip(string name, string text)
    {
        if (CLike.Any(name.EndsWith)) return StripPairs(text, ["//"], "/*", "*/");
        if (SqlLike.Any(name.EndsWith)) return StripPairs(text, ["--"], "/*", "*/");
        if (HashLike.Any(name.EndsWith)) return StripPairs(text, ["#"], null, null);
        if (RemLike.Any(name.EndsWith)) return CmdRemComment.Replace(text, "");
        return text;
    }

    private static string StripPairs(string text, string[] lineStarts, string? blockOpen, string? blockClose)
    {
        var outBuf = new StringBuilder(text.Length);
        var state = 0; // 0 code, 1 line, 2 block, 3 string
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            if (state == 0)
            {
                var hit = lineStarts.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0);
                if (hit is not null)
                {
                    outBuf.Append(' ', hit.Length);
                    i += hit.Length - 1;
                    state = 1;
                    continue;
                }
                if (blockOpen is not null && string.CompareOrdinal(text, i, blockOpen, 0, blockOpen.Length) == 0)
                {
                    outBuf.Append(' ', blockOpen.Length);
                    i += blockOpen.Length - 1;
                    state = 2;
                    continue;
                }
                if (c is '"' or '\'') { state = 3; quote = c; }
                outBuf.Append(c);
            }
            else if (state == 1)
            {
                if (c is '\r' or '\n') { outBuf.Append(c); state = 0; }
                else outBuf.Append(' ');
            }
            else if (state == 2)
            {
                if (blockClose is not null && string.CompareOrdinal(text, i, blockClose, 0, blockClose.Length) == 0)
                {
                    outBuf.Append(' ', blockClose.Length);
                    i += blockClose.Length - 1;
                    state = 0;
                    continue;
                }
                outBuf.Append(c is '\r' or '\n' ? c : ' ');
            }
            else
            {
                outBuf.Append(c);
                if (c == '\\' && n != '\0') { outBuf.Append(n); i++; continue; }
                if (c == quote && n == quote) { outBuf.Append(n); i++; continue; }
                if (c == quote) state = 0;
            }
        }
        return outBuf.ToString();
    }

    private static IEnumerable<(string Relative, string Text)> ScannedFiles(string repoRoot)
    {
        var sep = Path.DirectorySeparatorChar;
        var suffixes = CLike.Concat(SqlLike).Concat(HashLike).Concat(RemLike).ToArray();

        foreach (var rootName in ScanRoots)
        {
            var root = Path.Combine(repoRoot, rootName);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!suffixes.Any(file.EndsWith)) continue;
                if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
                if (file.Contains($"{sep}node_modules{sep}")) continue;
                if (file.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                yield return (relative, Strip(Path.GetFileName(file), File.ReadAllText(file)));
            }
        }
    }

    private static SortedDictionary<string, int> Violators(Regex rule, string? sanctionedHome = null)
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var found = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, text) in ScannedFiles(repoRoot))
        {
            if (sanctionedHome is not null
                && relative.Equals(sanctionedHome, StringComparison.OrdinalIgnoreCase)) continue;
            var count = rule.Matches(text).Count;
            if (count > 0) found[relative] = count;
        }
        return found;
    }

    private static void AssertRatchet(
        Regex rule, IReadOnlySet<string> allowlist, string listName, string guidance,
        string? sanctionedHome = null)
    {
        var current = Violators(rule, sanctionedHome);
        var newcomers = current.Keys.Where(v => !allowlist.Contains(v)).ToList();
        Assert.True(newcomers.Count == 0, guidance + "\n  " + string.Join("\n  ", newcomers));

        var stale = allowlist.Where(a => !current.ContainsKey(a)).ToList();
        Assert.True(stale.Count == 0,
            $"These files no longer carry it — delete them from {listName} and lower its "
            + $"ceiling to {allowlist.Count - stale.Count}:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void IngestMutex_NoNewProcessLevelCopy()
        => AssertRatchet(ProcessMutexProbe, ProcessMutexAllowlist, nameof(ProcessMutexAllowlist),
            "New copy of the process-level ingest mutex. One ingest at a time is one rule, "
            + "so it gets one implementation — the roster scripts must call it, not paste it. "
            + "Instead of here:");

    /// <summary>
    /// The database half. <c>AdvisoryTxLock</c> is excluded as its sanctioned home, so a
    /// green result means every OTHER advisory lock in the tree is enumerated below.
    /// </summary>
    [Fact]
    public void IngestMutex_NoNewDatabaseLevelImplementation()
        => AssertRatchet(DatabaseAdvisoryLock, DatabaseMutexAllowlist, nameof(DatabaseMutexAllowlist),
            "New advisory lock outside AdvisoryTxLock. The ingest mutex has exactly one "
            + "database implementation (AdvisoryTxLock.BeginWithLockAsync, which also names "
            + "the blocking backend instead of hanging silently). A second one is a second "
            + "mutex. Instead of here:",
            sanctionedHome: DatabaseMutexSanctionedHome);

    /// <summary>
    /// The single database implementation must actually be there. Without this the
    /// exclusion above could pass by virtue of the file having been deleted.
    /// </summary>
    [Fact]
    public void IngestMutex_DatabaseImplementationExists()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var home = Path.Combine(repoRoot, DatabaseMutexSanctionedHome.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(home), $"{DatabaseMutexSanctionedHome} is the one ingest mutex; it is missing.");
        Assert.Matches(DatabaseAdvisoryLock, Strip(Path.GetFileName(home), File.ReadAllText(home)));
    }

    [Fact]
    public void EvidenceVerify_NoNewHandRolledImplementation()
        => AssertRatchet(EvidenceVerify, EvidenceVerifyAllowlist, nameof(EvidenceVerifyAllowlist),
            "New hand-rolled `evidence_count(...) > 0` verify. That test reports "
            + "content-only lanes as NOT INGESTED — source_status.sql.in's header records "
            + "the same false negative being reached three times. Ask laplace.source_status() "
            + "instead of here:",
            sanctionedHome: VerifySanctionedHome);

    /// <summary>The destination must exist for the guidance above to be actionable.</summary>
    [Fact]
    public void EvidenceVerify_SanctionedSurfaceExists()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        Assert.True(
            File.Exists(Path.Combine(repoRoot, VerifySanctionedHome.Replace('/', Path.DirectorySeparatorChar))),
            $"{VerifySanctionedHome} is where the {EvidenceVerifyCeiling} hand-rolled verifies "
            + "are headed; it is missing.");
    }

    [Fact]
    public void G10_AllowlistsOnlyShrink()
    {
        Assert.True(ProcessMutexAllowlist.Count <= ProcessMutexCeiling,
            $"{nameof(ProcessMutexAllowlist)} has {ProcessMutexAllowlist.Count} entries; ceiling is "
            + $"{ProcessMutexCeiling}. This list may only shrink.");
        Assert.True(DatabaseMutexAllowlist.Count <= DatabaseMutexCeiling,
            $"{nameof(DatabaseMutexAllowlist)} has {DatabaseMutexAllowlist.Count} entries; ceiling is "
            + $"{DatabaseMutexCeiling}. This list may only shrink.");
        Assert.True(EvidenceVerifyAllowlist.Count <= EvidenceVerifyCeiling,
            $"{nameof(EvidenceVerifyAllowlist)} has {EvidenceVerifyAllowlist.Count} entries; ceiling is "
            + $"{EvidenceVerifyCeiling}. This list may only shrink.");

        var sites = Violators(EvidenceVerify, VerifySanctionedHome).Values.Sum();
        Assert.True(sites <= EvidenceVerifySiteCeiling,
            $"{sites} hand-rolled evidence_count verify call sites; ceiling is "
            + $"{EvidenceVerifySiteCeiling}. An allowlisted file may not grow its count.");
    }

    /// <summary>
    /// Spec 37 §5 (<c>:328</c>) claims "6 ingest-mutex + 11 verify implementations". W6 §3
    /// recorded that as unverified. Pinning the arithmetic here means the spec's own
    /// number stops being prose: 5 process copies + 1 database implementation = 6.
    /// </summary>
    [Fact]
    public void G10_MatchesSpec37Arithmetic()
    {
        Assert.Equal(6, ProcessMutexAllowlist.Count + 1);
        Assert.Equal(11, EvidenceVerifyAllowlist.Count);
    }
}
