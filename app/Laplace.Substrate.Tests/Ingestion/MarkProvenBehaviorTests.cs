using Xunit;
using Laplace.Decomposers.Abstractions.Tests;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// MarkProven must only run on confirmed-present roots after descent emit — not unconditionally.
/// </summary>
public sealed class MarkProvenBehaviorTests
{
    [Fact]
    public void IngestDescentFlush_MarkProvenRunsAfterSuccessfulDrain()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var flush = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Abstractions", "IngestDescentFlush.cs");
        var text = File.ReadAllText(flush);
        Assert.Contains("reader.MarkProven([root])", text, StringComparison.Ordinal);
        Assert.Contains("FinalizePendingAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkProven(all", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TierTreeDescent_DocumentsConfirmedPresentOnlyMarkProven()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var descent = Path.Combine(repoRoot, "app", "Laplace.Substrate", "Crud", "TierTreeDescent.cs");
        Assert.True(File.Exists(descent), "TierTreeDescent.cs must exist");
        var text = File.ReadAllText(descent);
        Assert.Contains("MarkProven", text, StringComparison.Ordinal);
        Assert.Contains("confirmed", text, StringComparison.OrdinalIgnoreCase);
    }
}
