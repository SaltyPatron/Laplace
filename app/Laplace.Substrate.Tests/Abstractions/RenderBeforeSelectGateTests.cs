using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// ISA gate G2: row-producing and looped realization is forbidden. IDs are
/// selected/ranked first, then resolved through an aligned batch. Scalar calls
/// remain legal only for exact, documented singleton values.
/// </summary>
public sealed class RenderBeforeSelectGateTests
{
    private static readonly Regex ScalarRealizer = new(
        @"\b(?:@extschema@\.)?(realize|realize_canonical|render|render_text|render_text_fast"
        + @"|type_label|label|label_or_hex)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnorderedRowNumber = new(
        @"\brow_number\s*\(\s*\)\s*over\s*\(\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FittedExactRenderDepth = new(
        @"\brender_text(?!_fast)(?:_batch)?\s*\([^\r\n]*,\s*\d+\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ExcludedDirectories = ["realize/", "readback/"];
    private static readonly string[] ExcludedPrefixes = ["converse/label"];
    private static readonly string[] ExcludedFiles = ["lexical/type_label.sql.in"];

    private static readonly string[] BatchSurfaces =
    [
        "realize/realize_batch.sql.in",
        "realize/resolve_name_batch.sql.in",
        "readback/render_text_batch.sql.in",
        "readback/render_batch.sql.in",
        "converse/label_batch.sql.in",
        "lexical/type_label_batch.sql.in",
    ];

    /// <summary>
    /// Exact scalar sites that execute once per function invocation, never once
    /// per output row or loop element. Counts are equality-pinned: additions and
    /// removals both require reviewing this classification.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> SingletonSites =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["converse/chat.sql.in"] = 1,                   // one elected topic
            ["converse/converse_about.sql.in"] = 1,         // one fallback topic
            ["converse/converse_facts.sql.in"] = 3,         // topic label + one top definition
            ["recall/recall_examples_response.sql.in"] = 1, // MATERIALIZED topic label
            ["recall/recall_fallback_gloss.sql.in"] = 1,    // MATERIALIZED topic label
            ["recall/recall_interaction_response.sql.in"] = 1, // aggregate topic label
            ["recall/recall_is_a_no_reply.sql.in"] = 4,     // exactly two endpoint ids
            ["recall/recall_related_response.sql.in"] = 1,  // MATERIALIZED topic label
            ["recall/recall_what_is_response.sql.in"] = 1,  // MATERIALIZED topic label
            ["consensus/relate_path.sql.in"] = 1,           // one winning relation plane
        };

    private static string FunctionsRoot(string repoRoot) =>
        Path.Combine(repoRoot, "extension", "laplace_substrate", "sql", "functions");

    private static bool IsExcluded(string relative) =>
        ExcludedDirectories.Any(d => relative.StartsWith(d, StringComparison.OrdinalIgnoreCase))
        || ExcludedPrefixes.Any(p => relative.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ExcludedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);

    internal static string StripSqlComments(string text)
    {
        var outBuf = new StringBuilder(text.Length);
        var state = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            switch (state)
            {
                case 0:
                    if (c == '-' && n == '-') { outBuf.Append("  "); i++; state = 1; continue; }
                    if (c == '/' && n == '*') { outBuf.Append("  "); i++; state = 2; continue; }
                    if (c == '\'') state = 3;
                    outBuf.Append(c);
                    break;
                case 1:
                    if (c is '\r' or '\n') { outBuf.Append(c); state = 0; }
                    else outBuf.Append(' ');
                    break;
                case 2:
                    if (c == '*' && n == '/') { outBuf.Append("  "); i++; state = 0; continue; }
                    outBuf.Append(c is '\r' or '\n' ? c : ' ');
                    break;
                default:
                    outBuf.Append(c);
                    if (c == '\'' && n == '\'') { outBuf.Append(n); i++; continue; }
                    if (c == '\'') state = 0;
                    break;
            }
        }
        return outBuf.ToString();
    }

    private static SortedDictionary<string, int> ScalarSites()
    {
        var root = FunctionsRoot(TypeIdLawTests.FindRepoRootPublic());
        var found = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(root), $"substrate function tree not found at {root}");
        foreach (var file in Directory.EnumerateFiles(root, "*.sql.in", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsExcluded(relative)) continue;
            var count = ScalarRealizer.Matches(StripSqlComments(File.ReadAllText(file))).Count;
            if (count > 0) found[relative] = count;
        }
        return found;
    }

    [Fact]
    public void RenderBeforeSelect_BatchSurfacesExist()
    {
        var root = FunctionsRoot(TypeIdLawTests.FindRepoRootPublic());
        foreach (var relative in BatchSurfaces)
            Assert.True(File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))),
                $"required batch realization surface is missing: {relative}");
    }

    [Fact]
    public void RenderBeforeSelect_HasNoRowProducingScalarCalls()
    {
        var actual = ScalarSites();
        var unexpected = actual.Keys.Except(SingletonSites.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = SingletonSites.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = SingletonSites.Keys.Intersect(actual.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => SingletonSites[k] != actual[k])
            .Select(k => $"{k}: expected {SingletonSites[k]}, actual {actual[k]}").ToList();

        Assert.True(unexpected.Count == 0,
            "row-producing/looped scalar realization returned; aggregate ids and use a batch:\n  "
            + string.Join("\n  ", unexpected));
        Assert.True(missing.Count == 0,
            "documented singleton sites disappeared; delete their exceptions:\n  "
            + string.Join("\n  ", missing));
        Assert.True(changed.Count == 0,
            "singleton scalar counts changed; classify every changed site:\n  "
            + string.Join("\n  ", changed));
        // 17 -> 16: taxonomy/synset_gloss.sql.in no longer realizes a scalar at all
        // (measured 0 matches), so its exception was deleted above and the pinned
        // total shrinks with it. Shrink-only is the point — this number may fall as
        // sites migrate to a batch surface, and may not rise without classifying the
        // new site in SingletonSites first.
        //
        // 16 -> 15: chess/chess_game.sql.in realized one game document — the stored
        // PGN movetext. #1258 stopped storing PGN and this change removed the dead
        // column, so the site is gone rather than exempted. The mainline is now the
        // line's typed move trajectory, rendered by replay outside SQL.
        Assert.Equal(15, actual.Values.Sum());
    }

    [Fact]
    public void BatchProjection_DoesNotInventOrderAfterRanking()
    {
        var root = FunctionsRoot(TypeIdLawTests.FindRepoRootPublic());
        var violations = Directory.EnumerateFiles(root, "*.sql.in", SearchOption.AllDirectories)
            .Select(file => new
            {
                Relative = Path.GetRelativePath(root, file).Replace('\\', '/'),
                Sql = StripSqlComments(File.ReadAllText(file)),
            })
            .Where(x => UnorderedRowNumber.IsMatch(x.Sql))
            .Select(x => x.Relative)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(violations.Count == 0,
            "row_number() OVER () does not preserve an operation's emitted ranking; "
            + "capture set-returning-function order with WITH ORDINALITY:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void EpistemicStatus_BatchesTypeLabelsOnlyForRelationTypes()
    {
        var path = Path.Combine(FunctionsRoot(TypeIdLawTests.FindRepoRootPublic()),
            "converse", "epistemic_status.sql.in");
        var sql = StripSqlComments(File.ReadAllText(path));

        Assert.Contains("type_ids AS MATERIALIZED", sql);
        Assert.Contains("lexical.type_label_batch(type_ids.a)", sql);
        Assert.DoesNotContain("lexical.type_label_batch(object_ids.a)", sql);
    }

    [Fact]
    public void ExactRendering_DoesNotDeclareAnImplicitDepthCeiling()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sqlRoot = Path.Combine(repoRoot, "extension", "laplace_substrate", "sql");
        var violations = Directory.EnumerateFiles(sqlRoot, "*.sql.in", SearchOption.AllDirectories)
            .Select(file => new
            {
                Relative = Path.GetRelativePath(sqlRoot, file).Replace('\\', '/'),
                Sql = StripSqlComments(File.ReadAllText(file)),
            })
            .Where(x => FittedExactRenderDepth.IsMatch(x.Sql))
            .Select(x => x.Relative)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(violations.Count == 0,
            "exact rendering must not silently truncate a valid constituent DAG; "
            + "use render_text_fast for an explicit preview budget:\n  "
            + string.Join("\n  ", violations));

        var closure = File.ReadAllText(Path.Combine(
            FunctionsRoot(repoRoot), "readback", "constituents_closure.sql.in"));
        Assert.Contains("p_max_depth integer DEFAULT 0", closure, StringComparison.Ordinal);

        // Cycle termination, exact bytes and scalar/batch depth behavior execute
        // against PostgreSQL in NpgsqlContentReconstructorTests. Native plan caching
        // and traversal implementation are not specified by source-string assertions.
    }
}
