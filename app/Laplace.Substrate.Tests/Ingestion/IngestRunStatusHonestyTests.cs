using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// CONSOLIDATION Q5: status=ok must not survive partial/incoherent files_done/files_total.
/// </summary>
public sealed class IngestRunStatusHonestyTests
{
    [Theory]
    [InlineData(0, false, false, 10, 10, "ok")]
    [InlineData(0, false, false, 0, 0, "ok")]
    [InlineData(1, false, false, 9, 10, "failed")]
    [InlineData(0, false, false, 33, 14900, "failed")]
    [InlineData(0, false, false, 0, 10, "failed")]
    [InlineData(0, false, false, 11, 1, "failed")] // ChessPgn segment markers as files (11/1 ok was a lie)
    [InlineData(0, true, false, 0, 10, "empty-noop")]
    [InlineData(0, false, true, 3, 10, "capped")]
    [InlineData(2, false, true, 3, 10, "failed")]
    public void DeriveRunStatus_ReflectsFileCompleteness(
        long unitsFailed, bool emptyNoOp, bool capped,
        int filesDone, long filesTotal, string expected)
    {
        Assert.Equal(expected, IngestRunner.DeriveRunStatus(
            unitsFailed, emptyNoOp, capped, filesDone, filesTotal));
    }

    [Fact]
    public void Inventory_TracksFileCompletion_GatesFileCount()
    {
        var specs = new[] { new IngestFileSpec("a", "/a", 1), new IngestFileSpec("b", "/b", 1) };
        var tracked = new IngestInventory("documents", 2, specs, TracksFileCompletion: true);
        var untracked = new IngestInventory("files", 2, specs, TracksFileCompletion: false);

        Assert.Equal(2, tracked.FileCount);
        Assert.Equal(0, untracked.FileCount);
        Assert.Equal(0, IngestInventory.Single(14900, "files").FileCount);
    }
}
