using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class GeneratedStageSinkArchitectureTests
{
    [Fact]
    public void GeneratedSinkUsesProbeThenPureWriteAndConversationRetry()
    {
        Assert.True(Laplace.Engine.Core.LaplaceInstall.TryRepoRoot(out string root));
        string sink = File.ReadAllText(Path.Combine(root,"extension","laplace_substrate","src","generated_stage_sink.c"));
        string catalog = File.ReadAllText(Path.Combine(root,"engine","core","src","sql_catalog.def"));
        string writer = File.ReadAllText(Path.Combine(root,"app","Laplace.Substrate","Crud","Npgsql","ConsensusAccumulatingWriter.cs"));
        Assert.DoesNotContain("SQ_LOCK", sink, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_laplace_entity_write_lock", sink, StringComparison.Ordinal);
        int start = catalog.IndexOf("/* Native generated-stage deposit", StringComparison.Ordinal);
        int end = catalog.IndexOf("/* Direct indexed containment", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string block = catalog[start..end];
        Assert.DoesNotContain("ON CONFLICT", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("advisory", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physicality_presence", block, StringComparison.Ordinal);
        Assert.Contains("attestation_presence", block, StringComparison.Ordinal);
        Assert.Contains("TransientErrorRetryPolicy.ConcurrencyRetry", writer, StringComparison.Ordinal);
    }
}
