using Laplace.Decomposers.Abstractions.Tests;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class WorkingSetQueryShapeTests
{
    [Fact]
    public void EntityVerify_DoesNotRunAFullTierRosterBeforeTheBoundedProbe()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var apply = Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlWorkingSetApply.cs");
        var text = File.ReadAllText(apply);

        Assert.DoesNotContain(
            "SELECT t FROM unnest($1::smallint[]) AS t",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "SELECT 1 FROM laplace.entities e WHERE e.tier = t.tier LIMIT t.need",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FoldHotPaths_SendOneRoutingTypePerBulkSet()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var apply = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlWorkingSetApply.cs"));
        var fold = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "ConsensusAccumulatingWriter.cs"));
        var native = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "src", "fold_route.c"));

        Assert.Contains("consensus.attestation_merge_type($1, $2, $3, $4, $5, $6, $7)",
            apply, StringComparison.Ordinal);
        // $9-$13 carry exact per-cell rating-period group offsets and values.
        Assert.Contains("consensus.upsert_type($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)",
            fold, StringComparison.Ordinal);
        Assert.DoesNotContain("types[i] =", apply, StringComparison.Ordinal);
        string atomicFold = MethodSource(fold, "UpsertDeltaInTransactionAsync")
            .Split("var maskPairs", StringSplitOptions.None)[0];
        string laneFold = MethodSource(fold, "DispatchDeltaAsync")
            .Split("async Task DepositAsync", StringSplitOptions.None)[0];
        Assert.Contains("consensus.upsert_type", atomicFold, StringComparison.Ordinal);
        Assert.Contains("consensus.upsert_type", laneFold, StringComparison.Ordinal);
        Assert.DoesNotContain("types[i] =", atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("types[i] =", laneFold, StringComparison.Ordinal);
        Assert.Contains("if (start == 0 && n == total)", native, StringComparison.Ordinal);
        Assert.Contains("return original;", native, StringComparison.Ordinal);
        Assert.Contains("fold_run_states", native, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF c", native, StringComparison.Ordinal);
        Assert.Contains("upsert_merge_with_retry", native, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CROSS JOIN LATERAL laplace.laplace_glicko2_accumulate_period",
            native,
            StringComparison.Ordinal);
    }

    private static string MethodSource(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == name)
            .ToFullString();

    [Fact]
    public void WorkingSetApply_HasOneCoordinationOwner_NoInMemoryClaimPolling()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var apply = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlWorkingSetApply.cs"));

        Assert.Contains(
            "AdvisoryTxLock.BeginWithLockAsync(\n            conn, \"laplace_apply_batch\"",
            apply,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedEntityIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedPhysIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedAttIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("claimDelayMs", apply, StringComparison.Ordinal);
    }
}
