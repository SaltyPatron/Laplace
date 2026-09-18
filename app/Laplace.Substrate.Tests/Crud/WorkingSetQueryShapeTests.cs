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
    public void EntityInterpretationPublication_StaysOptimisticSetWiseAndIncremental()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var publisher = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions",
            "identity", "entity_interpretations_publish.sql.in"));
        var retry = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Ingestion",
            "TransientErrorRetryPolicy.cs"));

        Assert.DoesNotContain("FOR UPDATE", publisher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", publisher, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, publisher.Split("input AS MATERIALIZED", StringSplitOptions.None).Length);
        Assert.DoesNotContain("JOIN touched", publisher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "FROM @extschema@.entity_interpretations i\n        JOIN",
            publisher, StringComparison.Ordinal);
        Assert.Contains("entities_stored_bitmap(p_entity_ids)", publisher, StringComparison.Ordinal);
        Assert.Contains("MERGE INTO @extschema@.entity_interpretations", publisher, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", publisher, StringComparison.Ordinal);
        Assert.Contains("incoming_summary AS MATERIALIZED", publisher, StringComparison.Ordinal);
        Assert.Contains("sqlState is \"23505\" or", retry, StringComparison.Ordinal);
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

        Assert.DoesNotContain("consensus.attestation_merge",
            apply, StringComparison.Ordinal);
        Assert.DoesNotContain("types[i] =", apply, StringComparison.Ordinal);
        string atomicFold = MethodSource(fold, "UpsertDeltaInTransactionAsync")
            .Split("var maskPairs", StringSplitOptions.None)[0];
        string laneFold = MethodSource(fold, "DispatchDeltaAsync")
            .Split("async Task DepositAsync", StringSplitOptions.None)[0];

        // Atomic admission reconstructs replayable cells from accepted testimony.
        // The separate explicit-delta lane keeps its existing native period route.
        // Both transmit one scalar type per set; $9-$13 retain the exact period
        // groups needed by continuous/non-replayable evidence.
        Assert.Contains(
            "consensus.upsert_evidence_type($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)",
            atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert_type(", atomicFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert(", atomicFold, StringComparison.Ordinal);
        Assert.Contains("command.Transaction = transaction", atomicFold);
        Assert.Contains("Hash128 type = cells[runStart].Key.T", atomicFold);
        Assert.Contains("cells[runEnd].Key.T == cells[runStart].Key.T", atomicFold);
        Assert.Contains("Value = type.ToBytes()", atomicFold);
        Assert.Contains("NpgsqlDbType = NpgsqlDbType.Bytea", atomicFold);
        Assert.Contains(
            "consensus.upsert_type($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)",
            laneFold, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.upsert_evidence_type(", laneFold, StringComparison.Ordinal);
        Assert.Contains("up.Parameters[0].Value = run.Type.ToBytes()", laneFold);
        Assert.Contains("? NpgsqlDbType.Bytea", laneFold);
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

        var admission = File.ReadAllText(Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlPhysicalityAdmission.cs"));
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

        var materialize = Assert.Single(calls.Where(call =>
            call.Expression.ToString() == "MaterializePhysicalitiesAsync"));
        var applyPrepared = Assert.Single(calls.Where(call =>
            call.Expression.ToString() == "ApplyPreparedStagesCoreAsync"));
        Assert.True(acquire.SpanStart < materialize.SpanStart);
        Assert.True(materialize.SpanStart < applyPrepared.SpanStart);
        foreach (var operation in new[] { materialize, applyPrepared })
        {
            Assert.Equal("connection", operation.ArgumentList.Arguments[0].Expression.ToString());
            Assert.Equal("transaction", operation.ArgumentList.Arguments[1].Expression.ToString());
        }
        Assert.DoesNotContain("AdvisoryTxLock.BeginWithLockAsync",
            MethodSource(apply, "ApplyPreparedStagesCoreAsync"), StringComparison.Ordinal);
        apply += admission;
        Assert.DoesNotContain("_claimedEntityIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedPhysIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("_claimedAttIds", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("claimDelayMs", apply, StringComparison.Ordinal);
    }
}
