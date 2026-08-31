using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class OrientTopicBatchingTests
{
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
    public void OrientTopic_BatchesCandidateLanguageLookup()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(
            repoRoot,
            "extension", "laplace_substrate", "sql", "functions", "converse",
            "orient_topic.sql.in"));
        var executable = ExecutableSql(sql);

        // GH #1047 / main integration timeout: candidate election is a relation-in
        // operation. The scalar word_language wrapper re-scans the HAS_LANGUAGE
        // relation once per candidate; orient_topic must use the canonical batch
        // surface once over the distinct candidate set instead.
        //
        // Inspect executable SQL rather than comments: design prose may legitimately
        // name the retired scalar call while explaining why it must not execute.
        Assert.Contains("converse.word_language_candidates_batch(i.ids)", executable, StringComparison.Ordinal);
        Assert.Contains("array_agg(DISTINCT r.synset_id", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("converse.word_language(", executable, StringComparison.Ordinal);

        // Preserve deterministic scalar semantics for candidates with several
        // language witnesses: same mu-desc/lang tie-break as word_language().
        Assert.Contains("PARTITION BY l.word", executable, StringComparison.Ordinal);
        Assert.Contains("ORDER BY l.mu DESC, l.lang", executable, StringComparison.Ordinal);
        Assert.Contains("AND l.rn = 1", executable, StringComparison.Ordinal);
    }

    private static string ExecutableSql(string sql)
    {
        // Strip quoted data before comments so comment delimiters inside a literal
        // cannot change where comment removal begins. This test owns static SQL only;
        // dynamic SQL belongs to the repository SQL auditor.
        string executable = SingleQuoted.Replace(sql, "''");
        executable = BlockComment.Replace(executable, " ");
        return LineComment.Replace(executable, " ");
    }
}
