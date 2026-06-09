using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using global::Npgsql;
using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Ingestion.Tests;

public class SyntheticDecomposerTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;

    public SyntheticDecomposerTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class SyntheticDecomposer : IDecomposer, IIngestCommitPolicy
    {
        private readonly int _unitCount;

        public SyntheticDecomposer(int unitCount, Hash128 sourceId)
        {
            _unitCount = unitCount;
            SourceId = sourceId;
        }

        public Hash128 SourceId { get; }
        public string SourceName => "SyntheticTest";
        public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.Unordered;
        public int LayerOrder => 0;
        public Hash128 TrustClassId =>
            Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            var bitmap = await context.Reader.EntitiesExistBitmapAsync(new[] { SourceId }, ct);
            if (bitmap.Length > 0 && (bitmap[0] & 1) != 0) return;

            var metaSeed = new SubstrateChangeBuilder(SourceId, "meta-seed")
                .AddEntity(BootstrapIntentBuilder.SourceTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.TypeMetaTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.RelationTypeMetaTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(TrustClassId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.HasTrustClassTypeId, 0,
                           BootstrapIntentBuilder.RelationTypeMetaTypeId,
                           firstObservedBy: null)
                .Build();
            await context.Writer.ApplyAsync(metaSeed, ct);

            var b = new BootstrapIntentBuilder(SourceId, SourceName, TrustClassId);
            await context.Writer.ApplyAsync(b.Build(), ct);
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var children = new Hash128[3];
            var seed     = new byte[12];
            for (int i = 0; i < _unitCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var builder = new SubstrateChangeBuilder(SourceId, $"unit-{i}");
                for (int k = 0; k < 3; k++)
                {
                    BitConverter.TryWriteBytes(seed.AsSpan(0, 8), SourceId.Lo);
                    BitConverter.TryWriteBytes(seed.AsSpan(8, 4), i * 100 + k);
                    var leaf = Hash128.Blake3(seed);
                    children[k] = leaf;
                    builder.AddEntity(leaf, 0, BootstrapIntentBuilder.SourceTypeId);
                }
                var parent = Hash128.Merkle(1, children);
                builder.AddEntity(parent, 1, BootstrapIntentBuilder.SourceTypeId, SourceId);
                yield return builder.Build();
                await Task.Yield();
            }
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(_unitCount);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FullEndToEndRun_InsertsExpectedEntityCount()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticEnd2End/v1");
        var decomposer = new SyntheticDecomposer(unitCount: 10, sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(10, result.UnitsAttempted);
        Assert.Equal(10, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
        Assert.True(result.EntitiesInserted >= 40);
    }

    [Fact]
    public async Task Rerun_IsIdempotent_ZeroNovelRowsSecondPass()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticResume/v1");

        var options = IngestRunOptions.Default with { SkipLayerOrderingCheck = true };

        var first = await runner.RunAsync(new SyntheticDecomposer(unitCount: 5, sourceId: srcId), options);
        Assert.Equal(5, first.UnitsApplied);
        Assert.Equal(0, first.UnitsFailed);
        Assert.True(first.EntitiesInserted >= 20);

        var second = await runner.RunAsync(new SyntheticDecomposer(unitCount: 5, sourceId: srcId), options);
        Assert.Equal(0, second.UnitsAttempted);
        Assert.Equal(0, second.UnitsApplied);
        Assert.Equal(0, second.UnitsFailed);
        Assert.Equal(0, second.EntitiesInserted);
    }

    [Fact]
    public async Task ParallelWorkers_ConvergesUnderRaceWithOverlap()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticParallel/v1");
        var decomposer = new SyntheticDecomposer(unitCount: 20, sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            ParallelWorkers = 4,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(20, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
    }

    [Fact]
    public async Task BatchedRun_AppliesEveryIntentAndAllNovelEntities()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticBatched/v1");
        var decomposer = new SyntheticDecomposer(unitCount: 10, sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            BatchSize              = 4,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(10, result.UnitsAttempted);
        Assert.Equal(10, result.UnitsApplied);
        Assert.Equal(0,  result.UnitsFailed);
        Assert.True(result.EntitiesInserted >= 40);
    }

    [Fact]
    public async Task BatchedRerun_IsIdempotent()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticBatchedResume/v1");

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            BatchSize              = 4,
        };

        var first = await runner.RunAsync(new SyntheticDecomposer(7, srcId), options);
        Assert.Equal(7, first.UnitsApplied);
        Assert.Equal(0, first.UnitsFailed);

        var second = await runner.RunAsync(new SyntheticDecomposer(7, srcId), options);
        Assert.Equal(0, second.UnitsAttempted);
        Assert.Equal(0, second.UnitsApplied);
        Assert.Equal(0, second.UnitsFailed);
        Assert.Equal(0, second.EntitiesInserted);
    }

    [Fact]
    public async Task BatchedRun_DedupsEntityRepeatedAcrossIntentsInSameBatch()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticOverlap/v1");
        var decomposer = new OverlapDecomposer(unitCount: 8, sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            BatchSize              = 8,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(8, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
        Assert.Equal(9, result.EntitiesInserted);
    }

    [Fact]
    public async Task EpochBarrier_PhasedEntitiesThenAttestations_ParallelCommits()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticPhased/v1");
        var decomposer = new PhasedDecomposer(sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            ParallelWorkers = 4,
            BatchSize = 2,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(6, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
        Assert.True(result.EntitiesInserted >= 3);
        Assert.True(result.AttestationsInserted >= 3);
    }

    [Fact]
    public async Task ParallelBatched_ConvergesUnderCrossWorkerIdOverlap()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticParallelOverlap/v1");
        var decomposer = new OverlapDecomposer(unitCount: 40, sourceId: srcId);

        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            ParallelWorkers        = 4,
            BatchSize              = 4,
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(40, result.UnitsApplied);
        Assert.Equal(0,  result.UnitsFailed);
        Assert.Equal(41, result.EntitiesInserted);
    }

    private sealed class PhasedDecomposer : IDecomposer
    {
        public PhasedDecomposer(Hash128 sourceId) => SourceId = sourceId;

        public Hash128 SourceId { get; }
        public string SourceName => "SyntheticPhased";
        public int LayerOrder => 0;
        public Hash128 TrustClassId =>
            Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            var bitmap = await context.Reader.EntitiesExistBitmapAsync(new[] { SourceId }, ct);
            if (bitmap.Length > 0 && (bitmap[0] & 1) != 0) return;
            await context.Writer.ApplyAsync(
                new BootstrapIntentBuilder(SourceId, SourceName, TrustClassId).Build(), ct);
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var ids = new Hash128[3];
            for (int i = 0; i < 3; i++)
            {
                var seed = new byte[12];
                BitConverter.TryWriteBytes(seed.AsSpan(0, 8), SourceId.Lo);
                BitConverter.TryWriteBytes(seed.AsSpan(8, 4), i);
                ids[i] = Hash128.Blake3(seed);
            }

            for (int i = 0; i < 3; i++)
            {
                yield return new SubstrateChangeBuilder(SourceId, $"ent-{i}")
                    .SetCommitEpoch(0)
                    .AddEntity(ids[i], 0, BootstrapIntentBuilder.SourceTypeId)
                    .Build();
                await Task.Yield();
            }

            var rel = RelationTypeRegistry.RelationTypeId("PRECEDES");
            for (int i = 0; i < 3; i++)
            {
                yield return new SubstrateChangeBuilder(SourceId, $"att-{i}")
                    .SetCommitEpoch(1)
                    .AddAttestation(AttestationFactory.CreateAggregated(
                        ids[i], rel, ids[(i + 1) % 3], SourceId, null, 1, Glicko2.FpScale, 1.0))
                    .Build();
                await Task.Yield();
            }
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(6);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OverlapDecomposer : IDecomposer, IIngestCommitPolicy
    {
        private readonly int _unitCount;
        private readonly Hash128 _shared;

        public OverlapDecomposer(int unitCount, Hash128 sourceId)
        {
            _unitCount = unitCount;
            SourceId = sourceId;
            var seed = new byte[12];
            BitConverter.TryWriteBytes(seed.AsSpan(0, 8), sourceId.Lo);
            BitConverter.TryWriteBytes(seed.AsSpan(8, 4), -1);
            _shared = Hash128.Blake3(seed);
        }

        public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.Unordered;

        public Hash128 SourceId { get; }
        public string SourceName => "SyntheticOverlap";
        public int LayerOrder => 0;
        public Hash128 TrustClassId =>
            Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            var bitmap = await context.Reader.EntitiesExistBitmapAsync(new[] { SourceId }, ct);
            if (bitmap.Length > 0 && (bitmap[0] & 1) != 0) return;

            var metaSeed = new SubstrateChangeBuilder(SourceId, "meta-seed")
                .AddEntity(BootstrapIntentBuilder.SourceTypeId, 0, BootstrapIntentBuilder.SourceTypeId, null)
                .AddEntity(BootstrapIntentBuilder.TypeMetaTypeId, 0, BootstrapIntentBuilder.SourceTypeId, null)
                .AddEntity(BootstrapIntentBuilder.RelationTypeMetaTypeId, 0, BootstrapIntentBuilder.SourceTypeId, null)
                .AddEntity(TrustClassId, 0, BootstrapIntentBuilder.SourceTypeId, null)
                .AddEntity(BootstrapIntentBuilder.HasTrustClassTypeId, 0, BootstrapIntentBuilder.RelationTypeMetaTypeId, null)
                .Build();
            await context.Writer.ApplyAsync(metaSeed, ct);
            await context.Writer.ApplyAsync(
                new BootstrapIntentBuilder(SourceId, SourceName, TrustClassId).Build(), ct);
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var seed = new byte[12];
            for (int i = 0; i < _unitCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                BitConverter.TryWriteBytes(seed.AsSpan(0, 8), SourceId.Lo);
                BitConverter.TryWriteBytes(seed.AsSpan(8, 4), i);
                var uniq = Hash128.Blake3(seed);
                var builder = new SubstrateChangeBuilder(SourceId, $"ov-{i}")
                    .AddEntity(_shared, 0, BootstrapIntentBuilder.SourceTypeId)
                    .AddEntity(uniq,    0, BootstrapIntentBuilder.SourceTypeId);
                yield return builder.Build();
                await Task.Yield();
            }
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(_unitCount);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LayerOrderingEnforced_RejectsLayerNWithoutPrereq()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticLayer/v1");
        var decomposer = new HighLayerDecomposer(srcId);

        var options = IngestRunOptions.Default;

        await Assert.ThrowsAsync<LayerOrderingViolationException>(
            () => runner.RunAsync(decomposer, options));
    }

    private sealed class HighLayerDecomposer : IDecomposer
    {
        public HighLayerDecomposer(Hash128 sid) => SourceId = sid;
        public Hash128 SourceId { get; }
        public string SourceName => "HighLayer";
        public int LayerOrder => 5;
        public Hash128 TrustClassId => Hash128.Zero;
        public Task InitializeAsync(IDecomposerContext c, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext c, DecomposerOptions o,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<long?> EstimateUnitCountAsync(IDecomposerContext c, CancellationToken ct = default)
            => Task.FromResult<long?>(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class LocalPgFixture : IAsyncLifetime
{
    public const string DatabaseName = "laplace_ingest_test";

    public static readonly string PgHost =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGHOST")
        ?? (OperatingSystem.IsWindows() ? "localhost" : "/var/run/postgresql");

    public static readonly string PgUser =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGUSER")
        ?? (OperatingSystem.IsWindows() ? "postgres" : "laplace_admin");

    public static readonly string? PgPassword =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_PGPASSWORD")
        ?? (OperatingSystem.IsWindows() ? "postgres" : null);

    private NpgsqlDataSource? _ds;
    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");
    public string ConnectionString =>
        $"Host={PgHost};Username={PgUser};Database={DatabaseName}"
        + (PgPassword is null ? "" : $";Password={PgPassword}");

    public async Task InitializeAsync()
    {
        await RunAdminAsync("dropdb",   $"-h {PgHost} -U {PgUser} --if-exists {DatabaseName}");
        await RunAdminAsync("createdb", $"-h {PgHost} -U {PgUser} -O {PgUser} {DatabaseName}");
        var dsb = new NpgsqlDataSourceBuilder(ConnectionString);
        _ds = dsb.Build();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE EXTENSION IF NOT EXISTS laplace_geom;
            CREATE EXTENSION IF NOT EXISTS laplace_substrate;
            SET search_path TO laplace, public;
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_ds is not null) { await _ds.DisposeAsync(); _ds = null; }
        await RunAdminAsync("dropdb", $"-h {PgHost} -U {PgUser} --if-exists {DatabaseName}");
    }

    private static async Task RunAdminAsync(string program, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ResolvePgTool(program), Arguments = args,
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (PgPassword is not null) psi.Environment["PGPASSWORD"] = PgPassword;
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"{program} {args} exited {p.ExitCode}: {stderr}");
        }
    }

    private static string ResolvePgTool(string program)
    {
        if (!OperatingSystem.IsWindows()) return program;
        string exe = Path.Combine(
            Environment.GetEnvironmentVariable("PGBIN") ?? @"C:\Program Files\PostgreSQL\18\bin",
            program + ".exe");
        return File.Exists(exe) ? exe : program;
    }
}
