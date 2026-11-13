using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ForwardRealizationLawTests
{
    [Fact]
    public void ForwardTextProjectsSemanticSelectionsThroughTheCanonicalRealizeExit()
    {
        string repoRoot = TypeIdLawTests.FindRepoRootPublic();
        string sql = File.ReadAllText(Path.Combine(
            repoRoot,
            "extension", "laplace_substrate", "sql", "functions", "generation", "walk_text.sql.in"));

        Assert.Contains("CREATE OR REPLACE FUNCTION taxonomy.bubble_down_batch", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION taxonomy.bubble_down(", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION realize.forward_text_batch", sql, StringComparison.Ordinal);
        Assert.Contains("realize.forward_text_batch(", sql, StringComparison.Ordinal);
        Assert.Contains("converse.prompt_language_top(p_prompt)", sql, StringComparison.Ordinal);
        Assert.Contains("has no truthful text surface", sql, StringComparison.Ordinal);

        // A completed semantic act realizes through realize.forward_text_batch, never
        // through a direct label/render of the entities, which would accept NULL as a
        // generated token.
        Assert.DoesNotContain(
            "SELECT realize.batch(entities) AS entities",
            sql,
            StringComparison.Ordinal);
    }
}
