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
        // A run that ends leaves every file it had not finished visible as incomplete.
        var runs = File.ReadAllText(Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions", "ops", "ingest_runs.sql.in"));
        Assert.Contains(
            "f.status IN ('inventoried', 'running', 'composed')",
            runs,
            StringComparison.Ordinal);
    }
}
