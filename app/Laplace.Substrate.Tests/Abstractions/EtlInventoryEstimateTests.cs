using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class EtlInventoryEstimateTests
{
    [Fact]
    public void EstimateNewlineCount_SmallFile_Exact()
    {
        string path = WriteTemp("a\nb\nc\n");
        try
        {
            Assert.Equal(3, EtlInventory.EstimateNewlineCount(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void EstimateNewlineCount_LargeFile_SamplesNotFullScan()
    {
        // Build a file larger than ExactScanThresholdBytes with a known density.
        string path = Path.Combine(Path.GetTempPath(), "laplace-est-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            const int lineLen = 100; // "x"*99 + '\n'
            long targetBytes = EtlInventory.ExactScanThresholdBytes + (8L << 20); // 72 MiB
            long expectedLines = targetBytes / lineLen;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 1 << 20))
            {
                var line = new byte[lineLen];
                line.AsSpan().Fill((byte)'x');
                line[^1] = (byte)'\n';
                long written = 0;
                while (written < targetBytes)
                {
                    fs.Write(line);
                    written += lineLen;
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long est = EtlInventory.EstimateNewlineCount(path);
            sw.Stop();

            // Sample estimate must finish well under a full-scan budget (72 MiB sequential
            // at ~100 MB/s ≈ 0.7s; sampled 64 MiB worst-case, but typically ≪ full).
            Assert.True(sw.ElapsedMilliseconds < 15_000,
                $"estimate took {sw.ElapsedMilliseconds}ms — looks like a full scan");

            // Within 5% of true line count (uniform lines → sample should be tight).
            double ratio = (double)est / expectedLines;
            Assert.InRange(ratio, 0.95, 1.05);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void EstimatePgnGameCount_CountsEventHeaders()
    {
        string path = WriteTemp(
            "[Event \"A\"]\n[White \"x\"]\n1. e4\n\n[Event \"B\"]\n1. d4\n");
        try
        {
            Assert.Equal(2, EtlInventory.EstimatePgnGameCount(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void EstimateConlluSentences_MatchesExactOnSmallFile()
    {
        string path = WriteTemp(
            "# sent\n1\tThe\t_\t_\t_\t_\t_\t_\t_\t_\n\n1\tA\t_\t_\t_\t_\t_\t_\t_\t_\n\n");
        try
        {
            long exact = EtlInventory.CountConlluSentences(path);
            long est = EtlInventory.EstimateConlluSentences(path);
            Assert.Equal(exact, est);
            Assert.Equal(2, est);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void EstimateNewlineCounts_ManySmallFiles_RespectsSharedBudget()
    {
        // Death-by-thousand-cuts: N files each under ExactScanThreshold must not
        // exact-scan N × threshold bytes. Shared MultiFileInventoryBudgetBytes caps IO.
        string dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "laplace-mf-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            const int fileCount = 80;
            const int bytesPerFile = 2 << 20; // 2 MiB → 160 MiB corpus > 64 MiB budget
            var paths = new List<string>(fileCount);
            var line = new byte[100];
            line.AsSpan().Fill((byte)'x');
            line[^1] = (byte)'\n';
            for (int i = 0; i < fileCount; i++)
            {
                string path = Path.Combine(dir, $"f{i:D3}.tsv");
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                long written = 0;
                while (written < bytesPerFile)
                {
                    fs.Write(line);
                    written += line.Length;
                }
                paths.Add(path);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long[] units = EtlInventory.EstimateNewlineCounts(paths);
            sw.Stop();

            Assert.Equal(fileCount, units.Length);
            Assert.All(units, u => Assert.True(u > 0));
            // Full exact-scan of 160 MiB at ~100 MB/s ≈ 1.6s; budgeted path must be faster
            // and finish well under a multi-file full-scan ceiling.
            Assert.True(sw.ElapsedMilliseconds < 15_000,
                $"multi-file estimate took {sw.ElapsedMilliseconds}ms — looks unbounded");

            long expectedPerFile = bytesPerFile / 100;
            double ratio = (double)units.Sum() / (expectedPerFile * fileCount);
            Assert.InRange(ratio, 0.90, 1.10);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConceptNet_DescribeInput_Uncapped_DoesNotRequireFullRead()
    {
        // Cap path already tested elsewhere; uncapped must call EstimateNewlineCount
        // (sample) — prove a multi-threshold file returns promptly with a sane count.
        string dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "laplace-cn-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string csv = Path.Combine(dir, "assertions.csv");
            // Just over threshold so sample path engages.
            long target = EtlInventory.ExactScanThresholdBytes + (1L << 20);
            using (var fs = new FileStream(csv, FileMode.Create, FileAccess.Write))
            {
                var line = System.Text.Encoding.UTF8.GetBytes(
                    "uri\t/r/RelatedTo\t/c/en/a\t/c/en/b\t{}\n");
                long written = 0;
                while (written < target)
                {
                    fs.Write(line);
                    written += line.Length;
                }
            }

            var dec = new Laplace.Decomposers.ConceptNet.ConceptNetDecomposer();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var inv = await dec.DescribeInputAsync(
                new TempCtx(dir), DecomposerOptions.ForWitness("ConceptNetDecomposer"));
            sw.Stop();

            Assert.NotNull(inv);
            Assert.True(inv!.TotalInputUnits > 0);
            Assert.True(sw.ElapsedMilliseconds < 15_000,
                $"ConceptNet DescribeInput took {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "laplace-inv-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class TempCtx(string ecosystemPath) : IDecomposerContext
    {
        public string EcosystemPath => ecosystemPath;
        public Laplace.SubstrateCRUD.ISubstrateWriter Writer => throw new NotSupportedException();
        public Laplace.SubstrateCRUD.ISubstrateReader Reader => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
