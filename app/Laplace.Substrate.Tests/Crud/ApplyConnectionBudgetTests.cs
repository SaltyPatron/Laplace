using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The ingest connection budget closes: PostgresResourcePlan sizes the pool as
/// 1 control + COPY fan + fold fan + observability, and MaxPoolSize is set from it, so
/// every simultaneously live owner fits. Pure arithmetic, so it holds on every machine shape.
/// </summary>
[Collection("cpu-topology-global")]
public sealed class ApplyConnectionBudgetTests
{
    [Fact]
    public void CopyBudget_Plus_FoldFan_Plus_Control_And_Observability_Fits_The_Pool()
    {
        var plan = PostgresResourcePlan.Current;
        int copy = NpgsqlSubstrateWriter.ResolveCopyConnectionBudget();
        // The fold fan comes from the fold's own sizing (FoldConnections), not an
        // assumption that it mirrors the COPY fan.
        int foldFan = IngestSizing.ResolveConsensusFold(
            IngestTopology.Current.ApplyPartitions).Connections;
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
        // Overlapping phases together claim no more than one phase's fan-out.
        Assert.True(copy <= NpgsqlSubstrateWriter.ApplyParallelism,
            $"copy budget {copy} exceeds one phase's fan-out {NpgsqlSubstrateWriter.ApplyParallelism}");
    }
}
