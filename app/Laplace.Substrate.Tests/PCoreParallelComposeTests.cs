using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests;

public class PCoreParallelComposeTests
{
    private static async IAsyncEnumerable<int> Records(int n)
    {
        for (int i = 0; i < n; i++) { yield return i; await Task.Yield(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task RunAsync_TrivialCompose_CompletesAndCoversAllRecords()
    {
        var src = Hash128.OfCanonical("pcore-test/source");
        long composed = 0;
        var changes = new List<SubstrateChange>();
        await foreach (var c in PCoreParallelCompose.RunAsync(
            Records(10_000), workerCount: 4, recordsPerChange: 1_000,
            () => new SubstrateChangeBuilder(src, "pcore-test"),
            (b, i) => { Interlocked.Increment(ref composed); },
            CancellationToken.None))
        {
            changes.Add(c);
        }
        Assert.Equal(10_000, Interlocked.Read(ref composed));
        Assert.Equal(10_000, changes.Sum(c => c.Metadata.InputUnitsConsumed));
    }
}
