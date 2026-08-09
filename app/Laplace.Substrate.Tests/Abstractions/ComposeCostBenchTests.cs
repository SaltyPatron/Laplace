using System.Diagnostics;
using System.Text;
using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Measures the per-surface cost of the compose-stage staging path
/// (ContentTierSpine.TryStageIntoBuilder) at the sizes Wiktionary actually
/// stages: word, gloss, etymology, document. Each iteration stages a UNIQUE
/// surface into a fresh builder — the same shape as the full multilingual
/// corpus, whose glosses/examples/translations are near-unique and miss every
/// cache. Diagnostic for the 77 records/s full-file compose wall
/// (build-logs/wiktionary-21gb-20260806-201419.log).
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class ComposeCostBenchTests
{
    private readonly ITestOutputHelper _out;

    public ComposeCostBenchTests(ITestOutputHelper output) => _out = output;

    private static string MakeSurface(int targetBytes, int salt)
    {
        var sb = new StringBuilder(targetBytes + 16);
        sb.Append("u").Append(salt).Append(' ');
        int w = 0;
        while (sb.Length < targetBytes)
            sb.Append("word").Append(w++ % 97).Append(' ');
        return sb.ToString(0, targetBytes);
    }

    [Theory]
    [InlineData(24, 400)]
    [InlineData(200, 400)]
    [InlineData(1000, 200)]
    [InlineData(5000, 100)]
    public void StageCost_PerUniqueSurface(int bytes, int iters)
    {
        CodepointPerfcache.LoadDefault();

        // Warmup: JIT + native tables.
        for (int i = 0; i < 10; i++)
        {
            var wb = new SubstrateChangeBuilder(
                WiktionaryDecomposer.Source, $"bench/warm/{bytes}/{i}", null,
                entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(
                wb, Encoding.UTF8.GetBytes(MakeSurface(bytes, 1_000_000 + i)),
                WiktionaryDecomposer.Source, out _));
        }

        var sw = Stopwatch.StartNew();
        long emitted = 0;
        for (int i = 0; i < iters; i++)
        {
            var b = new SubstrateChangeBuilder(
                WiktionaryDecomposer.Source, $"bench/{bytes}/{i}", null,
                entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
            Assert.True(ContentTierSpine.TryStageIntoBuilder(
                b, Encoding.UTF8.GetBytes(MakeSurface(bytes, i)),
                WiktionaryDecomposer.Source, out var root));
            Assert.NotEqual(default, root);
            emitted++;
        }
        sw.Stop();

        double usPerCall = sw.Elapsed.TotalMicroseconds / emitted;
        double mbPerSec = bytes * emitted / sw.Elapsed.TotalSeconds / 1e6;
        _out.WriteLine(
            $"BENCH stage {bytes,5}B x{emitted}: {usPerCall,10:F1} us/call  {mbPerSec,8:F2} MB/s");
    }

    /// <summary>
    /// 11 threads staging unique surfaces concurrently, per-thread builders — the
    /// compose fan's claimed shape. Linear scaling here means the 77 rec/s wall is
    /// pipeline wiring; a collapse means shared native state serializes the fan.
    /// </summary>
    [Theory]
    [InlineData(1000, 11, 200)]
    public async Task StageCost_Parallel(int bytes, int threads, int itersPerThread)
    {
        CodepointPerfcache.LoadDefault();

        var wb = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "bench/pwarm", null,
            entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
        Assert.True(ContentTierSpine.TryStageIntoBuilder(
            wb, Encoding.UTF8.GetBytes(MakeSurface(bytes, 9_999_999)),
            WiktionaryDecomposer.Source, out _));

        var sw = Stopwatch.StartNew();
        var tasks = new List<Task>();
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < itersPerThread; i++)
                {
                    var b = new SubstrateChangeBuilder(
                        WiktionaryDecomposer.Source, $"bench/p/{tid}/{i}", null,
                        entityCapacity: 256, physicalityCapacity: 256, attestationCapacity: 256);
                    Assert.True(ContentTierSpine.TryStageIntoBuilder(
                        b, Encoding.UTF8.GetBytes(MakeSurface(bytes, tid * 1_000_000 + i)),
                        WiktionaryDecomposer.Source, out _));
                }
            }));
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        long total = (long)threads * itersPerThread;
        double usPerCall = sw.Elapsed.TotalMicroseconds / total;
        double mbPerSec = (double)bytes * total / sw.Elapsed.TotalSeconds / 1e6;
        _out.WriteLine(
            $"BENCH par   {bytes,5}B x{total} on {threads}T: {usPerCall,8:F1} us/call-wall  {mbPerSec,8:F2} MB/s aggregate");
    }

    /// <summary>Same sizes through BuildTree alone (derivation without emission).</summary>
    [Theory]
    [InlineData(24, 400)]
    [InlineData(200, 400)]
    [InlineData(1000, 200)]
    [InlineData(5000, 100)]
    public void BuildTreeCost_PerUniqueSurface(int bytes, int iters)
    {
        CodepointPerfcache.LoadDefault();

        for (int i = 0; i < 10; i++)
            ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(MakeSurface(bytes, 2_000_000 + i)))?.Dispose();

        var sw = Stopwatch.StartNew();
        long n = 0;
        for (int i = 0; i < iters; i++)
        {
            var tree = ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(MakeSurface(bytes, i)));
            Assert.NotNull(tree);
            tree!.Dispose();
            n++;
        }
        sw.Stop();

        double usPerCall = sw.Elapsed.TotalMicroseconds / n;
        double mbPerSec = bytes * n / sw.Elapsed.TotalSeconds / 1e6;
        _out.WriteLine(
            $"BENCH build {bytes,5}B x{n}: {usPerCall,10:F1} us/call  {mbPerSec,8:F2} MB/s");
    }
}
