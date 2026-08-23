using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The ingest connection equation must CLOSE. PostgresResourcePlan sizes the pool as
/// 1 control + 2p (COPY fan + fold fan) + observability, and MaxPoolSize is set from
/// it — so every simultaneously-live owner class has to fit inside it. The COPY fan
/// silently exceeded its half once the physicalities and attestations phases began
/// overlapping (each fanning to ApplyParallelism groups), and the pool answered with
/// "connection pool has been exhausted (currently 28)" after a 15s rent timeout,
/// killing seed runs mid-corpus. Arithmetic, so it holds on every machine shape.
/// </summary>
public sealed class ApplyConnectionBudgetTests
{
    [Fact]
    public void CopyBudget_Plus_FoldFan_Plus_Control_And_Observability_Fits_The_Pool()
    {
        var plan = PostgresResourcePlan.Current;
        int copy = NpgsqlSubstrateWriter.ResolveCopyConnectionBudget();
        int foldFan = copy; // the equation's other half
        Assert.True(
            1 + copy + foldFan + plan.ObservabilityConnectionOwners <= plan.IngestConnectionOwners,
            $"copy={copy} fold={foldFan} obs={plan.ObservabilityConnectionOwners} "
            + $"exceeds IngestConnectionOwners={plan.IngestConnectionOwners}");
    }

    [Fact]
    public void CopyBudget_Is_At_Least_One_And_Never_Exceeds_A_Single_Phase_Fanout()
    {
        int copy = NpgsqlSubstrateWriter.ResolveCopyConnectionBudget();
        Assert.True(copy >= 1);
        // Overlapping phases must not be able to claim more than one phase's worth of
        // connections between them.
        Assert.True(copy <= NpgsqlSubstrateWriter.ApplyParallelism,
            $"copy budget {copy} exceeds one phase's fan-out {NpgsqlSubstrateWriter.ApplyParallelism}");
    }
}
