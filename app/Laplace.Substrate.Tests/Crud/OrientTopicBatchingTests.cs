using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class OrientTopicBatchingTests
{
    [Fact]
    public void OrientTopic_BatchesCandidateLanguageLookup()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(
            repoRoot,
            "extension", "laplace_substrate", "sql", "functions", "converse",
            "orient_topic.sql.in"));

        // GH #1047 / main integration timeout: candidate election is a relation-in
        // operation. The scalar word_language wrapper re-scans the HAS_LANGUAGE
        // relation once per candidate; orient_topic must use the canonical batch
        // surface once over the distinct candidate set instead.
        Assert.Contains("converse.word_language_candidates_batch(i.ids)", sql, StringComparison.Ordinal);
        Assert.Contains("array_agg(DISTINCT r.synset_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("converse.word_language(", sql, StringComparison.Ordinal);

        // Preserve deterministic scalar semantics for candidates with several
        // language witnesses: same mu-desc/lang tie-break as word_language().
        Assert.Contains("PARTITION BY l.word", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY l.mu DESC, l.lang", sql, StringComparison.Ordinal);
        Assert.Contains("AND l.rn = 1", sql, StringComparison.Ordinal);
    }
}
