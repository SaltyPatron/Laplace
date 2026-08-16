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

    /// <summary>
    /// A ledger row saying `failed` with error NULL is not diagnosable. MEASURED 2026-08-10:
    /// the document lane recorded failed at files_done=199/207, units_failed=0, error empty —
    /// twice — and the row was the only artifact that outlived the run.
    /// </summary>
    [Theory]
    [InlineData(0, 199, 207, "files_done 199 of 207 — 8 file(s) did not reach completion; "
                           + "their content is absent from the substrate")]
    [InlineData(0, 33, 14900, "files_done 33 of 14900 — 14867 file(s) did not reach completion; "
                            + "their content is absent from the substrate")]
    [InlineData(3, 10, 10, "3 unit(s) failed to apply")]
    [InlineData(0, 11, 1, "files_done 11 exceeds files_total 1 — "
                        + "the lane counted more completions than it declared inputs")]
    public void DescribeRunFailure_NamesTheReason(
        long unitsFailed, int filesDone, long filesTotal, string expected)
    {
        Assert.Equal(expected, IngestRunner.DescribeRunFailure(unitsFailed, filesDone, filesTotal));
    }

    /// <summary>Every input that derives `failed` must also derive a reason — no silent rows.</summary>
    [Theory]
    [InlineData(1, 9, 10)]
    [InlineData(0, 33, 14900)]
    [InlineData(0, 0, 10)]
    [InlineData(0, 11, 1)]
    [InlineData(2, 3, 10)]
    public void EveryFailedStatus_HasAReason(long unitsFailed, int filesDone, long filesTotal)
    {
        Assert.Equal("failed", IngestRunner.DeriveRunStatus(
            unitsFailed, emptySourceNoOp: false, capped: false, filesDone, filesTotal));
        Assert.False(string.IsNullOrWhiteSpace(
            IngestRunner.DescribeRunFailure(unitsFailed, filesDone, filesTotal)));
    }

    /// <summary>A clean run derives no reason — the column stays NULL on success.</summary>
    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(0, 0, 0)]
    public void SuccessfulRun_HasNoReason(long unitsFailed, int filesDone, long filesTotal)
    {
        Assert.Null(IngestRunner.DescribeRunFailure(unitsFailed, filesDone, filesTotal));
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

    [Theory]
    [InlineData(1226, 1226, "ok")]
    [InlineData(1225, 1226, "failed")]
    public void TheLedgerCarriesTheSameFileCountTheStatusWasDerivedFrom(
        int filesDone, long filesTotal, string expected)
    {
        // The ledger row is the only surviving artifact of a run
        // (NpgsqlIngestObservability, "MEASURED 2026-08-10 ... no diagnostic in the row").
        // files_done rode ONLY the periodic progress UPDATE, so a run whose last flush did
        // not land wrote a count BELOW the one its own status was derived from. Measured on
        // the live substrate 2026-08-16: OMWDecomposer status=ok, files_done 1225,
        // files_total 1226 — derived from 1226 in memory, recorded as 1225.
        var result = new IngestRunResult(
            SourceId: default,
            SourceName: "OMWDecomposer",
            UnitsAttempted: 1, UnitsApplied: 1, UnitsFailed: 0,
            EntitiesInserted: 0, PhysicalitiesInserted: 0, AttestationsInserted: 0,
            TotalRoundTrips: 0, WallClock: TimeSpan.Zero,
            Failures: [], FilesDone: filesDone);

        Assert.Equal(filesDone, result.FilesDone);
        Assert.Equal(expected, IngestRunner.DeriveRunStatus(
            unitsFailed: 0, emptySourceNoOp: false, capped: false,
            filesDone: result.FilesDone, filesTotal: filesTotal));
    }
}
