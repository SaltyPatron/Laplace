using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class RefoldSourceRegressionTests
{
    [Fact]
    public void RefoldSource_RoutesReconstructionToTheSharedNativeEvidenceOwner()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions", "ops",
            "refold_source.sql.in"));
        var entry = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions", "fold",
            "consensus_upsert.sql.in"));
        var native = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "src", "fold_route.c"));

        // This source control protects delegation and mutation ownership.
        // ConsensusEvidencePeriodTests exercise actual missing-row identity,
        // neutral reconstruction, replayability and concurrent admission.
        Assert.Contains("SELECT consensus.refold_evidence_type(", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(
            @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+laplace\.consensus\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), sql);
        Assert.DoesNotContain("laplace.consensus_fold(", sql, StringComparison.Ordinal);

        var binding = Regex.Match(entry,
            @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+consensus\.refold_evidence_type\s*\(.*?;",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Assert.True(binding.Success, "Missing native source-refold SQL entry point");
        Assert.Contains("'pg_laplace_consensus_refold_evidence_type'", binding.Value);
        Assert.Matches(@"LANGUAGE\s+C\s+VOLATILE", binding.Value);

        // Recovery owns derived consensus only; testimony remains authoritative.
        var evidenceMutation = new Regex(
            @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+laplace\.attestations\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.DoesNotMatch(evidenceMutation, sql);
        Assert.DoesNotMatch(evidenceMutation, native);
    }
}
