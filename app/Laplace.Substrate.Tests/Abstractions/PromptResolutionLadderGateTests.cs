using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Prompt resolution is a LADDER, and every rung may only read rungs below it.
///
/// This gate exists because the law was broken and the breakage was silent until a live
/// query returned "stack depth limit exceeded". converse.prompt_state was changed to
/// resolve each token's language against the prompt's own language tally — correct in
/// itself — while converse.prompt_language (C, src/prompt_language.c) read its token ids
/// back out of converse.prompt_state. prompt_state -> prompt_language -> prompt_state is
/// unbounded mutual recursion. Neither file is wrong when read alone, which is exactly
/// why a test that reads one file cannot catch it.
///
/// The ordering is not a style preference, it is the semantics:
///
///   word_segment      graphemes -> tokens.                  Knows no ids.
///   prompt_words      tokens -> content ids.                Knows no language.
///   prompt_language   ids -> a ranked language tally.       Knows no per-token language.
///   prompt_state      ids + tally -> per-token language.    First rung that ASSIGNS one.
///   prompt_coherence  the rated candidate set.
///   elect             the single election over that set.
///
/// A tally over token identities cannot depend on an assignment derived from the tally.
/// Anything that consumes a rung's output belongs above it, permanently.
///
/// Within-rung references are allowed: converse.elect_topic and converse.elect_sense are
/// projections of converse.elect and live on its rung by construction. Only an UPWARD
/// call is a defect.
/// </summary>
public sealed class PromptResolutionLadderGateTests
{
    /// <summary>Bottom to top. Index is the rung.</summary>
    private static readonly string[] Ladder =
    [
        "word_segment",
        "prompt_words",
        "prompt_language",
        "prompt_state",
        "prompt_coherence",
        "elect",
    ];

    private static readonly Regex Comments = new(
        @"/\*[\s\S]*?\*/|--[^\r\n]*|//[^\r\n]*",
        RegexOptions.Compiled);

    /// <summary>
    /// A definition, drop, grant or comment names the function without calling it. Left in
    /// place, every rung trivially "calls" itself and the gate reports nothing usable.
    /// </summary>
    private static readonly Regex Declaration = new(
        @"(?:CREATE\s+(?:OR\s+REPLACE\s+)?|DROP\s+|ALTER\s+|COMMENT\s+ON\s+|GRANT\s+[\w\s,]+?\s+ON\s+)"
        + @"FUNCTION\s+(?:IF\s+EXISTS\s+)?[\w@]+\s*\.\s*\w+\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A C rung reaches SQL through SPI query literals, so the .c body counts as part of
    /// the rung. Bound by the PG_FUNCTION_INFO_V1 symbol the .sql.in declares, not by
    /// filename, so a renamed or merged source file cannot quietly drop out of the gate.
    /// </summary>
    private static readonly Regex ModulePathname = new(
        @"AS\s+'MODULE_PATHNAME'\s*,\s*'(?<symbol>\w+)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void PromptResolution_RungsNeverCallUpward()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var violations = new List<string>();

        for (var rung = 0; rung < Ladder.Length; rung++)
        {
            foreach (var (origin, text) in RungSources(repoRoot, Ladder[rung]))
            {
                for (var above = rung + 1; above < Ladder.Length; above++)
                {
                    var call = new Regex(
                        @"\bconverse\s*\.\s*" + Regex.Escape(Ladder[above]) + @"\s*\(",
                        RegexOptions.IgnoreCase);
                    if (call.IsMatch(text))
                    {
                        violations.Add(
                            $"rung {rung} converse.{Ladder[rung]} ({origin}) calls "
                            + $"rung {above} converse.{Ladder[above]}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Prompt resolution must not call upward — the result is unbounded mutual "
            + "recursion, which surfaces only at runtime as \"stack depth limit "
            + "exceeded\":\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Every rung must exist and be reachable as a file. A rung silently renamed out of
    /// the ladder would make the gate above vacuously green.
    /// </summary>
    [Fact]
    public void PromptResolution_EveryRungIsDefined()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var rung in Ladder)
        {
            var path = RungPath(repoRoot, rung);
            Assert.True(File.Exists(path), $"Ladder rung has no definition: converse.{rung}");
            Assert.Matches(
                new Regex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+converse\s*\.\s*"
                    + Regex.Escape(rung) + @"\s*\(", RegexOptions.IgnoreCase),
                File.ReadAllText(path));
        }
    }

    /// <summary>
    /// The ladder is also an INSTALL order. Every rung is BEGIN ATOMIC, so PostgreSQL
    /// resolves its callees at CREATE time and a rung listed before the rung it reads
    /// aborts CREATE EXTENSION outright.
    ///
    /// This is asserted because it broke: prompt_language used to read prompt_state, so
    /// the manifests listed prompt_language last. Inverting the dependency to fix the
    /// recursion inverted the required order too, and nothing said so — the isolate
    /// already had every function installed, so it kept answering while a fresh
    /// CREATE EXTENSION failed on the very first statement. An upgrade path can hide an
    /// ordering fault indefinitely; only a clean install proves it.
    /// </summary>
    [Fact]
    public void PromptResolution_ManifestsInstallRungsInLadderOrder()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();

        foreach (var manifest in new[] { "manifest.install", "manifest.upgrade" })
        {
            var path = Path.Combine(
                repoRoot, "extension", "laplace_substrate", "sql", manifest);
            var lines = File.ReadAllLines(path);

            var positions = new List<(string Rung, int Line)>();
            foreach (var rung in Ladder)
            {
                var entry = $"functions/converse/{rung}.sql.in";
                var line = Array.FindIndex(lines, l => l.Trim() == entry);
                Assert.True(line >= 0, $"{manifest} does not install converse.{rung}.");
                positions.Add((rung, line));
            }

            for (var i = 1; i < positions.Count; i++)
            {
                Assert.True(positions[i - 1].Line < positions[i].Line,
                    $"{manifest} installs converse.{positions[i].Rung} "
                    + $"(line {positions[i].Line + 1}) before the rung it reads, "
                    + $"converse.{positions[i - 1].Rung} (line {positions[i - 1].Line + 1}). "
                    + "BEGIN ATOMIC resolves callees at CREATE time, so this fails a fresh "
                    + "CREATE EXTENSION while an already-installed database keeps working.");
            }
        }
    }

    private static string RungPath(string repoRoot, string rung)
        => Path.Combine(repoRoot, "extension", "laplace_substrate", "sql", "functions",
            "converse", rung + ".sql.in");

    private static IEnumerable<(string Origin, string Text)> RungSources(string repoRoot, string rung)
    {
        var sql = File.ReadAllText(RungPath(repoRoot, rung));
        yield return (rung + ".sql.in", Declaration.Replace(StripComments(sql), " "));

        var srcRoot = Path.Combine(repoRoot, "extension", "laplace_substrate", "src");
        foreach (Match module in ModulePathname.Matches(sql))
        {
            var symbol = new Regex(
                @"PG_FUNCTION_INFO_V1\(\s*" + Regex.Escape(module.Groups["symbol"].Value) + @"\s*\)");

            foreach (var file in Directory.EnumerateFiles(srcRoot, "*.c").Order(StringComparer.Ordinal))
            {
                var c = File.ReadAllText(file);
                if (symbol.IsMatch(c))
                    yield return (Path.GetFileName(file), StripComments(c));
            }
        }
    }

    private static string StripComments(string text)
        => Comments.Replace(text, string.Empty);
}
