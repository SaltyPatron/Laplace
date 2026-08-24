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
[Trait("Tier", "db")]
// SERIALIZED WITH THE LEDGER TESTS. LocalPgFixture.InitializeAsync calls
// ContentLadderLedger.Reset() -- process-global state -- on first ref. Run in
// parallel with ContentLadderLedgerTests that reset lands mid-test and clears
// membership the test is asserting survives End() (main went red on
// End_disarms_but_Begin_keeps_membership_for_warm_reingest). These tests never
// ran together until #1316 stopped excluding Tier=db, which is why it surfaced
// only now. GrammarPerfcache is the collection those ledger tests already use.
[Collection("GrammarPerfcache")]
public sealed class IngestPipelineGateTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public IngestPipelineGateTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class DeferredContentSyntheticDecomposer : IDecomposer
    {
        private readonly int _unitCount;
        // Prebuilt UTF-8 surfaces — warm re-ingest must measure ledger/skip + apply,
        // not re-allocate the corpus on every DecomposeAsync.
        private readonly byte[][] _units;

        public DeferredContentSyntheticDecomposer(int unitCount, int bytesPerUnit, Hash128 sourceId)
        {
            _unitCount = unitCount;
            SourceId = sourceId;
            _units = new byte[unitCount][];
            var sb = new StringBuilder(bytesPerUnit);
            for (int i = 0; i < unitCount; i++)
            {
                sb.Clear();
                sb.Append("unit-");
                sb.Append(i);
                while (sb.Length < bytesPerUnit)
                    sb.Append((char)('a' + (i % 26)));
                _units[i] = Encoding.UTF8.GetBytes(sb.ToString());
            }
        }

        public Hash128 SourceId { get; }
        public string SourceName => "DeferredContentSynthetic";
        public int LayerOrder => 2;
        public Hash128 TrustClassId =>
            SubstrateCanonicalIds.TrustClass("SubstrateMandate");

        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var records = EnumerateUnits(ct);
            await foreach (var change in IngestComposePipeline.RunAsync(
                records,
                (utf8, b) => ContentTierSpine.TryStageIntoBuilder(b, utf8, SourceId, out _),
                SourceId,
                "synthetic",
                context.Reader,
                options,
                ct,
                trunkShortcircuit: utf8 =>
                    ContentLadderLedger.Armed
                    && ContentTierSpine.ResolveRoot(utf8) is { } root
                    && ContentLadderLedger.IsPersisted(root)))
            {
                yield return change;
            }
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(_unitCount);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<byte[]> EnumerateUnits(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < _units.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return _units[i];
            }
            await Task.CompletedTask;
        }
    }

    private static IngestRunner NewRunner(NpgsqlDataSource ds)
    {
        IngestTopology.EnsureReady();
        var reader = new NpgsqlSubstrateReader(ds);
        var writer = new NpgsqlSubstrateWriter(ds);
        return new IngestRunner(writer, reader, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task WarmReingest_Meets_30SecondsPerGigabyte_InputScanGate()
    {
        // Same ~4 MiB input budget as before, but fewer larger surfaces: 16k×256B
        // made the gate a per-call overhead tax (ResolveRoot × N), not an input-scan
        // measurement. Real corpora are not 256-byte units.
        const int unitCount = 1_024;
        const int bytesPerUnit = 4_096;
        long inputBytes = (long)unitCount * bytesPerUnit;
        double maxSeconds = IngestBaselineGates.MaxSecondsForBytes(inputBytes);

        var srcId = SubstrateCanonicalIds.OfVersioned("source", "test", "pipeline-warm");
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
        Assert.True(warmSw.Elapsed.TotalSeconds <= maxSeconds,
            $"warm re-ingest took {warmSw.Elapsed.TotalSeconds:F2}s for {inputBytes:N0} input bytes "
          + $"(gate {maxSeconds:F2}s = {IngestBaselineGates.MaxSecondsPerGigabyte}s/GB, {mbPerSec:F1} MiB/s, "
          + $"round_trips={warm.TotalRoundTrips}, rows_new={warm.EntitiesInserted + warm.PhysicalitiesInserted + warm.AttestationsInserted:N0})");
        Assert.True(mbPerSec >= IngestBaselineGates.MinMegabytesPerSecond,
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
