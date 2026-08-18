using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class IngestFileJournalStateTests
{
    [Fact]
    public void ComposedFilesAreVisibleAndReconciledAsIncomplete()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var observability = Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlIngestObservability.cs");
        var text = File.ReadAllText(observability);

        Assert.Contains("status = 'composed'", text, StringComparison.Ordinal);
        Assert.Contains(
            "WHERE f.status IN ('running','composed')",
            text,
            StringComparison.Ordinal);
    }
}
