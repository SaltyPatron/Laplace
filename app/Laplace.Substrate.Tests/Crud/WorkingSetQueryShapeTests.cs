using Laplace.Decomposers.Abstractions.Tests;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class WorkingSetQueryShapeTests
{
    [Fact]
    public void ArraySizedSrfs_ExposePlannerRowSupport()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        string support = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions",
            "planner", "array_length_rows_support.sql.in"));
        Assert.Contains("pg_laplace_array_length_rows_support", support, StringComparison.Ordinal);

        foreach (string relative in new[]
        {
            "sql/probes/entities_present_ordinals.sql.in",
            "sql/probes/physicalities_present_ordinals.sql.in",
            "sql/probes/attestations_present_ordinals.sql.in",
            "sql/functions/converse/label.sql.in"
        })
        {
            string sql = File.ReadAllText(Path.Combine(
                repoRoot, "extension", "laplace_substrate",
                relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("SUPPORT @extschema@.array_length_rows_support", sql,
                StringComparison.Ordinal);
            Assert.Contains("ROWS 100", sql, StringComparison.Ordinal);
        }
    }

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
    public void FoldHotPaths_SendOneMixedTypeWorkingSetPerDatabaseCall()
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

        Assert.DoesNotContain("consensus.attestation_merge",
            apply, StringComparison.Ordinal);
        // NpgsqlWorkingSetApply may legitimately build type arrays for unrelated
        // set-sized persistence paths (for example physicality admission). Keep
        // this invariant scoped to the consensus fold methods below instead of
        // banning the syntax repository-wide.
        string atomicFold = MethodSource(fold, "UpsertDeltaInTransactionAsync")
            .Split("var maskPairs", StringSplitOptions.None)[0];
        string laneFold = MethodSource(fold, "DispatchDeltaAsync")
            .Split("async Task DepositAsync", StringSplitOptions.None)[0];

        // Atomic admission sends the complete mixed-type chunk once. Native code
        // owns physical type routing and the exact period groups. The separate
        // explicit-delta lane retains its native period route.
        Assert.Contains(
            "consensus.merge_evidence($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)",
            atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert_evidence_type(", atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert_type(", atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert(", atomicFold, StringComparison.Ordinal);
        Assert.Contains("command.Transaction = transaction", atomicFold);
        Assert.Contains("types[i] = cell.Key.T.ToBytes()", atomicFold);
        Assert.DoesNotContain("runStart", atomicFold, StringComparison.Ordinal);
        Assert.Contains("NpgsqlDbType.Array | NpgsqlDbType.Bytea, types", atomicFold);
        Assert.Contains(
            "consensus.upsert_type($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)",
            laneFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert_evidence_type(", laneFold, StringComparison.Ordinal);
        Assert.Contains("up.Parameters[0].Value = run.Type.ToBytes()", laneFold);
        Assert.Contains("? NpgsqlDbType.Bytea", laneFold);
        Assert.DoesNotContain("types[i] =", laneFold, StringComparison.Ordinal);
        Assert.Contains("if (start == 0 && n == total)", native, StringComparison.Ordinal);
        Assert.Contains("return original;", native, StringComparison.Ordinal);
        Assert.Contains("fold_run_states", native, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF c", native, StringComparison.Ordinal);
        Assert.Contains("pg_laplace_consensus_merge_evidence", native, StringComparison.Ordinal);
        Assert.Contains("MERGE INTO laplace.consensus c", native, StringComparison.Ordinal);
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

        var admission = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlPhysicalityStaging.cs"));
        var owner = CSharpSyntaxTree.ParseText(admission).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "ApplyStagesCoreAsync");
        var calls = owner.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var acquire = Assert.Single(calls.Where(call =>
            call.Expression.ToString() == "AdvisoryTxLock.BeginWithLockAsync"));
        var arguments = acquire.ArgumentList.Arguments;
        Assert.Equal("connection", arguments[0].Expression.ToString());
        Assert.Equal("laplace_apply_batch",
            Assert.IsType<LiteralExpressionSyntax>(arguments[1].Expression).Token.ValueText);
        Assert.Equal("TransactionGucs(Durability)", arguments[2].Expression.ToString());

        var applyPrepared = Assert.Single(calls.Where(call =>
            call.Expression.ToString() == "ApplyPreparedStagesCoreAsync"));
        Assert.True(acquire.SpanStart < applyPrepared.SpanStart);
        Assert.Equal("connection", applyPrepared.ArgumentList.Arguments[0].Expression.ToString());
        Assert.Equal("transaction", applyPrepared.ArgumentList.Arguments[1].Expression.ToString());
        Assert.DoesNotContain("AdvisoryTxLock.BeginWithLockAsync",
            MethodSource(apply, "ApplyPreparedStagesCoreAsync"), StringComparison.Ordinal);
        apply += admission;
        Assert.DoesNotContain("_claimedEntityIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedPhysIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedAttIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("claimDelayMs", apply, StringComparison.Ordinal);
    }
}
