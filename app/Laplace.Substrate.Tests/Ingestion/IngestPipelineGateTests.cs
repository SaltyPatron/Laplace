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
[Collection("GrammarPerfcache")]
public sealed class IngestPipelineGateTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public IngestPipelineGateTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record SyntheticUnit(
        byte[] Utf8, Hash128 Root, Hash128 Owner, Hash128 Recipe) : IIngestCompletionRecord
    {
        public IngestUnitCompletionKey? Completion => IngestUnitCompletion.Key(Root, Owner, 2, Recipe);
    }

    private sealed class DeferredContentSyntheticDecomposer : IDecomposer
    {
        private readonly int _unitCount;
        // Input storage is stable, but every enumeration scans and hashes all bytes.
        // No root or completion identity is precomputed outside the measured run.
        private readonly byte[][] _units;
        // The declared recipe is content: the composition of its own text.
        private readonly byte[] _recipeUtf8;
        private readonly Hash128 _recipe;
        private long _scannedBytes;
        private int _composedUnits;

        public long ScannedBytes => Interlocked.Read(ref _scannedBytes);

        /// <summary>The content this source states: every unit's root and its recipe.</summary>
        public Hash128[] Content() => _units
            .Select(static u => ContentTierSpine.ResolveRoot(u)!.Value)
            .Append(_recipe).Distinct().ToArray();
        public int ComposedUnits => Volatile.Read(ref _composedUnits);

        public DeferredContentSyntheticDecomposer(
            int unitCount, int bytesPerUnit, Hash128 sourceId, string? recipe = null)
        {
            _unitCount = unitCount;
            SourceId = sourceId;
            _recipeUtf8 = Encoding.UTF8.GetBytes(recipe ?? "test/deferred-content-synthetic/content-observations/v1");
            _recipe = ContentTierSpine.ResolveRoot(_recipeUtf8)
                ?? throw new InvalidOperationException("synthetic recipe has no content identity");
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
            TrustClassRegistry.Id("SubstrateMandate");

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
                (unit, b) =>
                {
                    if (!ContentTierSpine.TryStageIntoBuilder(b, unit.Utf8, SourceId, out var root)
                        || root != unit.Root)
                        throw new InvalidOperationException("synthetic unit did not compose its exact content");
                    if (!ContentTierSpine.TryStageIntoBuilder(b, _recipeUtf8, SourceId, out var recipe)
                        || recipe != unit.Recipe)
                        throw new InvalidOperationException("synthetic recipe did not compose its exact content");
                    // BuildAsync must finish deferred content before the control transaction
                    // can record this completion together with its physicality testimony.
                    IngestUnitCompletion.Emit(b, root, SourceId, LayerOrder, unit.Recipe);
                    Interlocked.Increment(ref _composedUnits);
                },
                SourceId,
                sourceTrust: 1.0,
                "synthetic",
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

        private async IAsyncEnumerable<SyntheticUnit> EnumerateUnits(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < _units.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var utf8 = _units[i];
                var root = ContentTierSpine.ResolveRoot(utf8)
                    ?? throw new InvalidOperationException("synthetic input has no content identity");
                Interlocked.Add(ref _scannedBytes, utf8.Length);
                yield return new SyntheticUnit(utf8, root, SourceId, _recipe);
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

        var srcId = SubstrateCanonicalIds.OfVersioned(
            "source", "test", "pipeline-warm-" + Guid.NewGuid().ToString("N"));
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
        Assert.Equal(unitCount, decomposer.ComposedUnits);
        Assert.Equal(inputBytes, decomposer.ScannedBytes);

        var warmOpts = coldOpts with { SkipSourceCompletion = true };
        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        var warm = await runner.RunAsync(decomposer, warmOpts);
        warmSw.Stop();

        Assert.Equal(0, warm.UnitsFailed);
        Assert.Equal(unitCount, warm.InputUnitsDone);
        Assert.Equal(2 * inputBytes, decomposer.ScannedBytes);
        Assert.Equal(unitCount, decomposer.ComposedUnits);
        Assert.Equal(0, warm.EntitiesInserted);
        Assert.Equal(0, warm.PhysicalitiesInserted);
        Assert.Equal(0, warm.AttestationsInserted);

        double mbPerSec = inputBytes / (1024.0 * 1024.0) / warmSw.Elapsed.TotalSeconds;
        Assert.True(warmSw.Elapsed.TotalSeconds <= maxSeconds,
            $"warm re-ingest took {warmSw.Elapsed.TotalSeconds:F2}s for {inputBytes:N0} input bytes "
          + $"(gate {maxSeconds:F2}s = {IngestBaselineGates.MaxSecondsPerGigabyte}s/GB, {mbPerSec:F1} MiB/s, "
          + $"round_trips={warm.TotalRoundTrips}, rows_new={warm.EntitiesInserted + warm.PhysicalitiesInserted + warm.AttestationsInserted:N0})");
        Assert.True(mbPerSec >= IngestBaselineGates.MinMegabytesPerSecond,
            $"warm scan {mbPerSec:F1} MiB/s is below {IngestBaselineGates.MinMegabytesPerSecond:F1} MiB/s gate");
    }


    [Fact]
    public async Task WarmCompletionRequiresTheExactOwnerAndRecipeAndPreservesAcceptedEvidence()
    {
        const int count = 4, bytes = 128;
        var peer = SubstrateCanonicalIds.Source("warm-peer-" + Guid.NewGuid().ToString("N"));
        var source = SubstrateCanonicalIds.Source("warm-owner-" + Guid.NewGuid().ToString("N"));
        var peerProducer = new DeferredContentSyntheticDecomposer(count, bytes, peer);
        var producer = new DeferredContentSyntheticDecomposer(count, bytes, source);
        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            SkipSourceCompletion = true,
            BatchSize = 4096,
            CommitRows = 250_000,
            DecomposerOptions = DecomposerOptions.ForWitness(producer.SourceName, batchSize: 4096),
        };

        // The other source has already deposited these exact canonical entities
        // and its own complete receipts. Neither proves this owner's completion.
        var peerRun = await NewRunner(_pg.DataSource).RunAsync(peerProducer, options);
        Assert.Equal(0, peerRun.UnitsFailed);
        Assert.Equal(count, peerProducer.ComposedUnits);
        var cold = await NewRunner(_pg.DataSource).RunAsync(producer, options);
        Assert.Equal(0, cold.UnitsFailed);
        Assert.Equal(count, producer.ComposedUnits);

        async Task<string> DurableStateAsync()
        {
            await using var command = _pg.DataSource.CreateCommand("""
                WITH owned AS MATERIALIZED (
                    SELECT a.* FROM laplace.attestations a
                    WHERE a.source_id=$1),
                referenced AS MATERIALIZED (
                    SELECT unnest($2::bytea[]) AS id
                    UNION SELECT subject_id FROM owned
                    UNION SELECT object_id FROM owned WHERE object_id IS NOT NULL
                    UNION SELECT context_id FROM owned WHERE context_id IS NOT NULL)
                SELECT jsonb_build_object(
                    'entities', COALESCE((
                        SELECT jsonb_agg(jsonb_build_array(encode(e.id,'hex'),e.tier,e.type_id)
                                         ORDER BY e.id,e.tier,e.type_id)
                        FROM laplace.entities e JOIN referenced r ON r.id=e.id), '[]'::jsonb),
                    'physicalities', COALESCE((
                        SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)
                        FROM laplace.physicalities p
                        JOIN referenced r ON r.id=p.entity_id), '[]'::jsonb),
                    'evidence', COALESCE((
                        SELECT jsonb_agg(to_jsonb(a) ORDER BY a.type_id,a.id)
                        FROM owned a), '[]'::jsonb),
                    'standing', COALESCE((
                        SELECT jsonb_agg(to_jsonb(c) ORDER BY c.type_id,c.id)
                        FROM laplace.consensus c
                        WHERE EXISTS (SELECT 1 FROM owned a
                            WHERE a.type_id=c.type_id AND a.subject_id=c.subject_id
                              AND a.object_id IS NOT DISTINCT FROM c.object_id)), '[]'::jsonb))::text
                """);
            command.Parameters.AddWithValue(source.ToBytes());
            command.Parameters.AddWithValue(producer.Content().Select(static id => id.ToBytes()).ToArray());
            return (string)(await command.ExecuteScalarAsync())!;
        }
        async Task<long> ReceiptCountAsync()
        {
            await using var command = _pg.DataSource.CreateCommand(
                "SELECT count(*) FROM laplace.ingest_unit_completion WHERE witness_id=$1 AND layer=2");
            command.Parameters.AddWithValue(source.ToBytes());
            return (long)(await command.ExecuteScalarAsync())!;
        }

        Assert.Equal(count, await ReceiptCountAsync());
        string accepted = await DurableStateAsync();
        using (var snapshot = System.Text.Json.JsonDocument.Parse(accepted))
        {
            Assert.True(snapshot.RootElement.GetProperty("entities").GetArrayLength() > 0);
            Assert.True(snapshot.RootElement.GetProperty("physicalities").GetArrayLength() > 0);
        }
        // A new reader and runner must obtain the durable receipt from PostgreSQL.
        var warm = await NewRunner(_pg.DataSource).RunAsync(producer, options);
        Assert.Equal(0, warm.UnitsFailed);
        Assert.Equal(count, warm.InputUnitsDone);
        Assert.Equal(count, producer.ComposedUnits);
        Assert.Equal(2L * count * bytes, producer.ScannedBytes);
        Assert.Equal(0, warm.EntitiesInserted);
        Assert.Equal(0, warm.PhysicalitiesInserted);
        Assert.Equal(0, warm.AttestationsInserted);
        Assert.Equal(accepted, await DurableStateAsync());
        Assert.Equal(count, await ReceiptCountAsync());

        // Identical content and owner with a different declared recipe must compose
        // again. Only that recipe's own receipts authorize its subsequent warm pass.
        var revised = new DeferredContentSyntheticDecomposer(
            count, bytes, source, "test/deferred-content-synthetic/recipe/v2");
        var revisedRun = await NewRunner(_pg.DataSource).RunAsync(revised, options);
        Assert.Equal(0, revisedRun.UnitsFailed);
        Assert.Equal(count, revised.ComposedUnits);
        Assert.Equal(2 * count, await ReceiptCountAsync());
        string revisedAccepted = await DurableStateAsync();
        var revisedWarm = await NewRunner(_pg.DataSource).RunAsync(revised, options);
        Assert.Equal(0, revisedWarm.UnitsFailed);
        Assert.Equal(count, revised.ComposedUnits);
        Assert.Equal(2L * count * bytes, revised.ScannedBytes);
        Assert.Equal(0, revisedWarm.EntitiesInserted);
        Assert.Equal(0, revisedWarm.PhysicalitiesInserted);
        Assert.Equal(0, revisedWarm.AttestationsInserted);
        Assert.Equal(revisedAccepted, await DurableStateAsync());
    }

    [Fact]
    public async Task ContentDescent_AllProven_SkipsDbRoundTrip()
    {
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var id = Hash128.Blake3(Encoding.UTF8.GetBytes("proven-trunk-gate"));
        var presenceScope = reader.CapturePresenceScope();
        reader.MarkProven([id], presenceScope);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bm = await reader.ContentDescentBitmapAsync([id], [-1]);
        sw.Stop();

        Assert.True(bm.Length > 0 && (bm[0] & 1) != 0);
        Assert.True(sw.Elapsed.TotalMilliseconds < 50,
            $"all-proven descent should be session-local, took {sw.Elapsed.TotalMilliseconds:F1}ms");
    }
}
