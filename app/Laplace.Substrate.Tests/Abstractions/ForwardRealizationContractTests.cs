using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ForwardRealizationContractTests
{
    private static string ReadForwardSql()
    {
        string root = TypeIdLawTests.FindRepoRootPublic();
        return File.ReadAllText(Path.Combine(
            root, "extension", "laplace_substrate", "sql", "functions", "generation", "walk_text.sql.in"));
    }

    [Fact]
    public void ForwardText_HasARealSemanticMeshExit()
    {
        string sql = ReadForwardSql();

        Assert.Contains("CREATE OR REPLACE FUNCTION taxonomy.bubble_down_batch(", sql);
        Assert.Contains("CREATE OR REPLACE FUNCTION taxonomy.bubble_down(", sql);
        Assert.Contains("p_lang bytea", sql);
        Assert.Contains("p_bands bytea", sql);
        Assert.Contains("p_temp numeric", sql);
        Assert.Contains("IS_SYNONYM_OF", sql);
        Assert.Contains("'HAS_SENSE'", sql);
        Assert.Contains("EVOKES_FRAME", sql);
        Assert.Contains("CORRESPONDS_TO", sql);
        Assert.Contains("consensus.relation_mask_types(p_bands)", sql);
        Assert.Contains("a.context_id = p_lang", sql);
    }

    [Fact]
    public void ForwardText_RealizesAfterSelectionAndNeverAcceptsANullToken()
    {
        string sql = ReadForwardSql();

        Assert.Contains("CREATE OR REPLACE FUNCTION realize.forward_text_batch(", sql);
        Assert.Contains("realize.render_text_batch(down_surfaces)", sql);
        Assert.Contains("realize.forward_text_batch(\n                   entities, converse.prompt_language_top(p_prompt))", sql);
        Assert.Contains("selected entity % has no truthful text surface", sql);
        Assert.DoesNotContain("SELECT realize.batch(entities) AS entities", sql);
    }

    [Fact]
    public void BubbleDown_DefaultElectionIsDeterministicAndExplorationUsesUncertainty()
    {
        string sql = ReadForwardSql();

        Assert.Contains("CASE WHEN p_temp = 0 THEN s.score END DESC", sql);
        Assert.Contains("GREATEST(d.rd::double precision, 1.0)", sql);
        Assert.Contains("random()", sql);
        Assert.Contains("s.rd, s.witnesses DESC, s.surface_id", sql);
    }
}
