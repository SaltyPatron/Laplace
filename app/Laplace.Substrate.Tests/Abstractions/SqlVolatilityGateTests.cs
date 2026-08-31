using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// PostgreSQL volatility is planner law, not documentation. A routine declared
/// STABLE/IMMUTABLE promises that repeated calls may be folded/reordered; calling a
/// VOLATILE primitive from that body makes those rewrites observably wrong.
///
/// GH #991 records two regressions that survived until review because this invariant
/// existed only in prose. This gate owns the mechanical half of that issue. The
/// separate "STABLE scalar inside WHERE" rule needs a measured shrink-only allowlist
/// and intentionally does not belong in this zero-exception check.
/// </summary>
public sealed class SqlVolatilityGateTests
{
    private static readonly Regex RoutineStart = new(
        @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<name>[^\s(]+)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StableDeclaration = new(
        @"\b(?:IMMUTABLE|STABLE|LAPLACE_IMMUTABLE_STRICT|LAPLACE_STABLE_STRICT)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DollarBody = new(
        @"\bAS\s+(?<tag>\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$)(?<body>.*?)\k<tag>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex AtomicBodyStart = new(
        @"\bBEGIN\s+ATOMIC\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // PostgreSQL classifies these as VOLATILE because they change per call or have
    // side effects. Deliberately absent: now()/transaction_timestamp() and
    // statement_timestamp(), which PostgreSQL classifies STABLE. #991's original
    // prose grouped now() with random(); the executable gate follows PostgreSQL's
    // actual contract rather than preserving that stale diagnosis.
    private static readonly Regex VolatileBuiltinCall = new(
        @"(?<![A-Za-z0-9_])(?<name>random|setseed|clock_timestamp|timeofday|nextval|setval|pg_sleep|pg_sleep_for|pg_sleep_until)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SingleQuoted = new(
        @"'(?:''|[^'])*'",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineComment = new(
        @"--[^\r\n]*",
        RegexOptions.Compiled);

    [Fact]
    public void StableAndImmutableSqlFunctions_DoNotCallVolatileBuiltins()
    {
        string repoRoot = TypeIdLawTests.FindRepoRootPublic();
        string functionsRoot = Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions");
        Assert.True(Directory.Exists(functionsRoot), $"SQL function tree missing: {functionsRoot}");

        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     functionsRoot, "*.sql.in", SearchOption.AllDirectories))
        {
            string sql = File.ReadAllText(file);
            foreach ((string name, string body) in StableRoutineBodies(sql))
            {
                foreach (Match call in VolatileBuiltinCall.Matches(ExecutableSql(body)))
                {
                    string rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    violations.Add($"{rel}: {name} -> {call.Groups["name"].Value}()");
                }
            }
        }

        violations.Sort(StringComparer.Ordinal);
        Assert.True(violations.Count == 0,
            "STABLE/IMMUTABLE SQL routine calls a PostgreSQL VOLATILE primitive. "
            + "Correct the routine volatility or move the volatile operation out of its body; "
            + "do not allowlist planner-unsound declarations.\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void GateRecognizesExecutableCallsButIgnoresCommentsAndText()
    {
        const string sql = """
            CREATE OR REPLACE FUNCTION demo.bad() RETURNS float8
            LANGUAGE sql STABLE AS $$
                SELECT random(); -- clock_timestamp() is only a comment
            $$;

            CREATE OR REPLACE FUNCTION demo.good() RETURNS timestamptz
            LANGUAGE sql STABLE
            BEGIN ATOMIC
                SELECT now();
                SELECT 'random() clock_timestamp()';
            END;
            """;

        var bodies = StableRoutineBodies(sql).ToDictionary(x => x.Name, x => x.Body);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(1, VolatileBuiltinCall.Matches(ExecutableSql(bodies["demo.bad"])).Count);
        Assert.Equal(0, VolatileBuiltinCall.Matches(ExecutableSql(bodies["demo.good"])).Count);
    }

    private static IEnumerable<(string Name, string Body)> StableRoutineBodies(string sql)
    {
        MatchCollection starts = RoutineStart.Matches(sql);
        for (int i = 0; i < starts.Count; i++)
        {
            Match start = starts[i];
            int blockEnd = i + 1 < starts.Count ? starts[i + 1].Index : sql.Length;
            string block = sql[start.Index..blockEnd];

            Match dollar = DollarBody.Match(block);
            Match atomic = AtomicBodyStart.Match(block);
            int bodyStart;
            string body;
            if (dollar.Success && (!atomic.Success || dollar.Index < atomic.Index))
            {
                bodyStart = dollar.Index;
                body = dollar.Groups["body"].Value;
            }
            else if (atomic.Success)
            {
                bodyStart = atomic.Index;
                body = block[(atomic.Index + atomic.Length)..];
            }
            else
            {
                // Native/C declarations use AS 'MODULE_PATHNAME' and have no SQL
                // body to police here.
                continue;
            }

            string header = block[..bodyStart];
            if (!StableDeclaration.IsMatch(header)) continue;
            yield return (start.Groups["name"].Value, body);
        }
    }

    private static string ExecutableSql(string body)
    {
        // Remove quoted data before comments so a literal containing "--" or "/*"
        // cannot change where comment stripping starts. Dynamic SQL is deliberately
        // outside this cheap gate; the repository SQL auditor owns that wider class.
        string executable = SingleQuoted.Replace(body, "''");
        executable = BlockComment.Replace(executable, " ");
        return LineComment.Replace(executable, " ");
    }
}
