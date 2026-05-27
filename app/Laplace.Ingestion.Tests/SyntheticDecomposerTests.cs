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

/// <summary>
/// Synthetic end-to-end probe per Story F.4. Builds a tiny decomposer that
/// emits N intents of fixed shape, pipes them through IngestRunner against
/// the live local PG, and verifies row counts + idempotency on re-run.
///
/// Proves the full framework wiring: IDecomposer → IngestRunner → ISubstrateWriter
/// → engine IntentStage → PG binary COPY → laplace.entities table. No
/// concrete decomposer needed — the framework is exercised in isolation.
/// </summary>
public class SyntheticDecomposerTests : IClassFixture<LocalPgFixture>, IAsyncLifetime
{
    private readonly LocalPgFixture _pg;
    private string _ckptDir = "";

    public SyntheticDecomposerTests(LocalPgFixture pg) => _pg = pg;

    public Task InitializeAsync()
    {
        _ckptDir = Path.Combine(Path.GetTempPath(), $"laplace-ingest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ckptDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_ckptDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private sealed class SyntheticDecomposer : IDecomposer
    {
        private readonly int _unitCount;

        public SyntheticDecomposer(int unitCount, Hash128 sourceId)
        {
            _unitCount = unitCount;
            SourceId = sourceId;
        }

        public Hash128 SourceId { get; }
        public string SourceName => "SyntheticTest";
        public int LayerOrder => 0; // probe layer
        public Hash128 TrustClassId =>
            Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            // Idempotency probe: if the source entity is already in the
            // substrate, bootstrap already ran — skip re-emission so the
            // re-run doesn't trip duplicate-pkey on the HAS_TRUST_CLASS
            // attestation. (Production decomposers use the same pattern;
            // ADR 0042 install-time bootstrap is idempotent by design.)
            var bitmap = await context.Reader.EntitiesExistBitmapAsync(new[] { SourceId }, ct);
            if (bitmap.Length > 0 && (bitmap[0] & 1) != 0) return;

            // Pre-bootstrap: in production these meta-entities land at
            // ADR 0042 Stages 0 + 3.5 (Type/Kind/Source meta-types + trust
            // classes) before any decomposer runs. The synthetic test
            // stands in for that install-time seeding so the
            // BootstrapIntentBuilder's FKs resolve.
            var metaSeed = new SubstrateChangeBuilder(SourceId, "meta-seed")
                .AddEntity(BootstrapIntentBuilder.SourceTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,    // self-typed
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.TypeMetaTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.KindMetaTypeId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(TrustClassId, 0,
                           BootstrapIntentBuilder.SourceTypeId,
                           firstObservedBy: null)
                .AddEntity(BootstrapIntentBuilder.HasTrustClassKindId, 0,
                           BootstrapIntentBuilder.KindMetaTypeId,
                           firstObservedBy: null)
                .Build();
            await context.Writer.ApplyAsync(metaSeed, ct);

            // Bootstrap: register the source entity + types + kinds + trust attestation.
            var b = new BootstrapIntentBuilder(SourceId, SourceName, TrustClassId);
            await context.Writer.ApplyAsync(b.Build(), ct);
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Heap-allocated reusable buffers — stackalloc'd Spans can't survive
            // across `yield return` / `await` boundaries in an async iterator,
            // and CA2014 forbids per-iteration stackalloc (potential stack
            // overflow on large _unitCount).
            var children = new Hash128[3];
            var seed     = new byte[12];
            for (int i = 0; i < _unitCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var builder = new SubstrateChangeBuilder(SourceId, $"unit-{i}");
                // 3 leaves per unit + 1 composed entity. Seed includes SourceId.Lo
                // so atoms are unique per source — keeps tests independent of
                // ordering even when they share the same PG fixture.
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
            CheckpointPathOverride = Path.Combine(_ckptDir, "ckpt.bin"),
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(10, result.UnitsAttempted);
        Assert.Equal(10, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
        // 10 units * (3 leaves + 1 parent) + bootstrap (source entity) = 41 entities total
        // But "EntitiesInserted" is only the novel-on-this-run count, which is what we just ran.
        // Bootstrap adds the source entity (1).
        // Decompose yields 10 units, each emitting 4 entities (3 leaves + 1 parent).
        // Some leaves repeat across units? With distinct seeds (i*100+k), all 4*10 = 40 are unique.
        Assert.True(result.EntitiesInserted >= 40);
    }

    [Fact]
    public async Task RerunSkipsAlreadyAppliedIntents()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticResume/v1");
        var decomposer = new SyntheticDecomposer(unitCount: 5, sourceId: srcId);

        var ckpt = Path.Combine(_ckptDir, "resume.bin");
        var options = IngestRunOptions.Default with
        {
            SkipLayerOrderingCheck = true,
            CheckpointPathOverride = ckpt,
        };

        var first = await runner.RunAsync(decomposer, options);
        Assert.Equal(5, first.UnitsApplied);
        Assert.Equal(0, first.UnitsSkippedFromCheckpoint);

        // Re-create decomposer to reset internal state and run again with
        // same checkpoint file — every intent should be skipped (deterministic
        // IntentId per unit name + source).
        var decomposer2 = new SyntheticDecomposer(unitCount: 5, sourceId: srcId);
        var second = await runner.RunAsync(decomposer2, options);
        Assert.Equal(5, second.UnitsAttempted);
        Assert.Equal(5, second.UnitsSkippedFromCheckpoint);
        Assert.Equal(0, second.UnitsApplied);
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
            CheckpointPathOverride = Path.Combine(_ckptDir, "parallel.bin"),
        };

        var result = await runner.RunAsync(decomposer, options);
        Assert.Equal(20, result.UnitsApplied);
        Assert.Equal(0, result.UnitsFailed);
    }

    [Fact]
    public async Task LayerOrderingEnforced_RejectsLayerNWithoutPrereq()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var reader = new NpgsqlSubstrateReader(_pg.DataSource);
        var runner = new IngestRunner(writer, reader);
        var srcId = Hash128.OfCanonical("substrate/source/SyntheticLayer/v1");
        // Build a fake Layer-5 decomposer that should be refused
        var decomposer = new HighLayerDecomposer(srcId);

        var options = IngestRunOptions.Default with
        {
            CheckpointPathOverride = Path.Combine(_ckptDir, "layer.bin"),
        };

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

// Reuse LocalPgFixture from the SubstrateCRUD.Tests by copy. Cleaner long-term:
// promote it to a shared test-utils library. For now, a local class keeps
// Ingestion.Tests self-contained.
public sealed class LocalPgFixture : IAsyncLifetime
{
    public const string DatabaseName = "laplace_ingest_test";
    public const string PgUser = "laplace_admin";

    private NpgsqlDataSource? _ds;
    public NpgsqlDataSource DataSource =>
        _ds ?? throw new InvalidOperationException("Fixture not initialized");
    public string ConnectionString =>
        $"Host=/var/run/postgresql;Username={PgUser};Database={DatabaseName}";

    public async Task InitializeAsync()
    {
        await RunAdminAsync("dropdb",   $"-U {PgUser} --if-exists {DatabaseName}");
        await RunAdminAsync("createdb", $"-U {PgUser} -O {PgUser} {DatabaseName}");
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
        await RunAdminAsync("dropdb", $"-U {PgUser} --if-exists {DatabaseName}");
    }

    private static async Task RunAdminAsync(string program, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = program, Arguments = args,
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"{program} {args} exited {p.ExitCode}: {stderr}");
        }
    }
}
