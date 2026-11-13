using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Ranked projections keep the order an operation emits, and exact rendering declares no
/// implicit depth ceiling.
/// </summary>
public sealed class RenderBeforeSelectGateTests
{
    private static readonly Regex UnorderedRowNumber = new(
        @"\brow_number\s*\(\s*\)\s*over\s*\(\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FittedExactRenderDepth = new(
        @"\brender_text(?!_fast)(?:_batch)?\s*\([^\r\n]*,\s*\d+\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string FunctionsRoot(string repoRoot) =>
        Path.Combine(repoRoot, "extension", "laplace_substrate", "sql", "functions");

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
    public void EpistemicStatus_ReadsStrandsOnTheNativeScale()
    {
        var path = Path.Combine(FunctionsRoot(TypeIdLawTests.FindRepoRootPublic()),
            "converse", "epistemic_status.sql.in");
        var sql = StripSqlComments(File.ReadAllText(path));

        // The verdict is over the strands that hold the entity, read through the
        // Native containment index and standing, never a triple-shaped table.
        Assert.Contains("public.laplace_claims_each", sql);
        Assert.Contains("consensus.refuted(h.rating, h.deviation)", sql);
        Assert.DoesNotContain("laplace.consensus", sql);
        Assert.DoesNotContain(".type_id", sql);
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

        // Cycle termination, exact bytes and scalar/batch depth are proven against
        // PostgreSQL in NpgsqlContentReconstructorTests, not by source-string assertions.
    }
}
