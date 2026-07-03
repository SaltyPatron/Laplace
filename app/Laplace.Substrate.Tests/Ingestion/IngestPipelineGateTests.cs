using System.Runtime.CompilerServices;
using System.Text;
using global::Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Ingestion.Tests;

[Trait("Tier", "perf")]
public sealed class IngestPipelineGateTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public IngestPipelineGateTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class DeferredContentSyntheticDecomposer : IDecomposer
    {
        private readonly int _unitCount;
        private readonly int _bytesPerUnit;

        public DeferredContentSyntheticDecomposer(int unitCount, int bytesPerUnit, Hash128 sourceId)
        {
            _unitCount = unitCount;
            _bytesPerUnit = bytesPerUnit;
            SourceId = sourceId;
        }

        public Hash128 SourceId { get; }
        public string SourceName => "DeferredContentSynthetic";
        public int LayerOrder => 2;
        public Hash128 TrustClassId =>
            Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var records = GenerateRecords(_unitCount, _bytesPerUnit);
            await foreach (var change in DecomposerBatch.RunAsync(
                records,
                (text, b) =>
                {
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    ContentWitnessBatch.TryAppendToBuilder(b, utf8, SourceId, out _);
                },
                SourceId,
                "synthetic",
                options.BatchSize,
                context.Reader,
                options,
                ct))
            {
                yield return change;
            }
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(_unitCount);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<string> GenerateRecords(
            int count, int bytesPerUnit, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var sb = new StringBuilder(bytesPerUnit);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                sb.Clear();
                sb.Append("unit-");
                sb.Append(i);
                while (sb.Length < bytesPerUnit)
                    sb.Append((char)('a' + (i % 26)));
                yield return sb.ToString();
                await Task.Yield();
            }
        }
    }

    private static IngestRunner NewRunner(NpgsqlDataSource ds)
    {
        Environment.SetEnvironmentVariable("LAPLACE_APPLY_PARTITIONS", "1");
        IngestTopology.EnsureReady();
        var reader = new NpgsqlSubstrateReader(ds);
        var writer = new NpgsqlSubstrateWriter(ds);
        return new IngestRunner(writer, reader, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task WarmReingest_Meets_30SecondsPerGigabyte_InputScanGate()
    {
        const int unitCount = 16_384;
        const int bytesPerUnit = 256;
        long inputBytes = (long)unitCount * bytesPerUnit;
        double maxSeconds = IngestBaselineGates.MaxSecondsForBytes(inputBytes);

        var srcId = Hash128.OfCanonical("substrate/source/test/pipeline-warm/v1");
        var decomposer = new DeferredContentSyntheticDecomposer(unitCount, bytesPerUnit, srcId);
        var runner = NewRunner(_pg.DataSource);

        var coldOpts = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            SkipSourceCompletion = true,
            BatchSize = 4096,
            CommitRows = 250_000,
            DecomposerOptions = DecomposerOptions.ForWitness(
                decomposer.SourceName, batchSize: 4096),
        };

        var cold = await runner.RunAsync(decomposer, coldOpts);
        Assert.Equal(0, cold.UnitsFailed);
        Assert.True(cold.UnitsApplied > 0);

        var warmOpts = coldOpts with { SkipSourceCompletion = true };
        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        var warm = await runner.RunAsync(decomposer, warmOpts);
        warmSw.Stop();

        Assert.Equal(0, warm.UnitsFailed);
        Assert.True(warm.UnitsApplied > 0);

        double mbPerSec = inputBytes / (1024.0 * 1024.0) / warmSw.Elapsed.TotalSeconds;
        Assert.True(warmSw.Elapsed.TotalSeconds <= maxSeconds * 1.15,
            $"warm re-ingest took {warmSw.Elapsed.TotalSeconds:F2}s for {inputBytes:N0} input bytes "
          + $"(gate {maxSeconds:F2}s = {IngestBaselineGates.MaxSecondsPerGigabyte}s/GB, {mbPerSec:F1} MiB/s, "
          + $"round_trips={warm.TotalRoundTrips}, rows_new={warm.EntitiesInserted + warm.PhysicalitiesInserted + warm.AttestationsInserted:N0})");
        Assert.True(mbPerSec >= IngestBaselineGates.MinMegabytesPerSecond * 0.85,
            $"warm scan {mbPerSec:F1} MiB/s is below {IngestBaselineGates.MinMegabytesPerSecond:F1} MiB/s gate");
    }

    [Fact]
    public async Task ContentDescent_AllProven_SkipsDbRoundTrip()
    {
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var id = Hash128.Blake3(Encoding.UTF8.GetBytes("proven-trunk-gate"));
        reader.MarkProven([id]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bm = await reader.ContentDescentBitmapAsync([id], [-1]);
        sw.Stop();

        Assert.True(bm.Length > 0 && (bm[0] & 1) != 0);
        Assert.True(sw.Elapsed.TotalMilliseconds < 50,
            $"all-proven descent should be session-local, took {sw.Elapsed.TotalMilliseconds:F1}ms");
    }
}
