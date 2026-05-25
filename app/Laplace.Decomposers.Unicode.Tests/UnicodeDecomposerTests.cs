using System.Collections.Immutable;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>
/// Verifies the T0 codepoint seed against the engine-built perf-cache blob.
/// Needs liblaplace_core.so on the load path + the blob (located the same way
/// the engine tests do); fails loud if absent.
/// </summary>
public sealed class UnicodeDecomposerTests
{
    private const int TotalCodepoints = 1_114_112;

    private static string LocateBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException(
            "perf-cache blob not found; build the engine (laplace_t0_perfcache) or set LAPLACE_PERFCACHE_BIN.");
    }

    private static UnicodeDecomposer NewDecomposer() => new(LocateBlob());

    private static IDecomposerContext Context(ISubstrateWriter writer) =>
        new FakeContext(writer);

    [Fact]
    public async Task Emits_All_Codepoints_As_T0_Entities_With_Content_Physicalities()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        // Ensure the perf-cache is loaded, then snapshot the expected 'A'
        // (U+0041) values into plain locals — a ref into the span cannot
        // cross the await boundary below.
        await dec.EstimateUnitCountAsync(ctx);
        Hash128 aHash;
        double ax, ay, az, am;
        {
            var rec = CodepointPerfcache.Records[0x41];
            aHash = rec.Hash; ax = rec.CoordX; ay = rec.CoordY; az = rec.CoordZ; am = rec.CoordM;
        }

        long entities = 0, physicalities = 0;
        bool allTier0 = true, allCodepointType = true, allFirstObserved = true, countsMatch = true;
        EntityRow? aEntity = null;
        PhysicalityRow? aPhys = null;

        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            entities += change.Entities.Length;
            physicalities += change.Physicalities.Length;
            if (change.Entities.Length != change.Physicalities.Length) countsMatch = false;

            for (int i = 0; i < change.Entities.Length; i++)
            {
                var e = change.Entities[i];
                if (e.Tier != 0) allTier0 = false;
                if (e.TypeId != UnicodeDecomposer.CodepointType) allCodepointType = false;
                if (e.FirstObservedBy != UnicodeDecomposer.Source) allFirstObserved = false;
                if (aEntity is null && e.Id == aHash)
                {
                    aEntity = e;
                    aPhys = change.Physicalities[i];
                }
            }
        }

        Assert.Equal(TotalCodepoints, entities);
        Assert.Equal(TotalCodepoints, physicalities);
        Assert.True(countsMatch, "every intent must carry one CONTENT physicality per entity");
        Assert.True(allTier0, "all codepoint entities are tier 0");
        Assert.True(allCodepointType, "all codepoint entities are typed Codepoint");
        Assert.True(allFirstObserved, "all codepoint entities first_observed_by UnicodeDecomposer");

        // 'A' carries the perf-cache values exactly (entity id = UTF-8 hash;
        // CONTENT coord = super-Fibonacci placement; T0 ⇒ no trajectory).
        Assert.NotNull(aEntity);
        Assert.NotNull(aPhys);
        Assert.Equal(PhysicalityKind.Content, aPhys!.Kind);
        Assert.Equal(ax, aPhys.CoordX);
        Assert.Equal(ay, aPhys.CoordY);
        Assert.Equal(az, aPhys.CoordZ);
        Assert.Equal(am, aPhys.CoordM);
        Assert.Null(aPhys.TrajectoryXyzm);
        Assert.Equal(0, aPhys.NConstituents);
        Assert.Equal(0, aPhys.ObservedAtUnixUs);
        Assert.Equal(UnicodeDecomposer.Source, aPhys.SourceId);
        Assert.Equal(aEntity!.Id, aPhys.EntityId);
    }

    [Fact]
    public async Task Initialize_Bootstraps_Source_Codepoint_Type_And_TrustClass()
    {
        var dec = NewDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(Context(writer));

        Assert.Single(writer.Captured);
        var boot = writer.Captured[0];

        // Source entity declared as a Source-typed entity.
        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        // Codepoint type registered as a Type-typed entity.
        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.CodepointType && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        // HAS_TRUST_CLASS attestation: source -> StandardsDerived.
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == UnicodeDecomposer.Source
            && a.KindId == BootstrapIntentBuilder.HasTrustClassKindId
            && a.ObjectId == UnicodeDecomposer.TrustClass);
    }

    [Fact]
    public async Task Deterministic_Intent_Ids_Across_Runs()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        var first = new List<Hash128>();
        await foreach (var c in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            first.Add(c.Metadata.IntentId);

        var second = new List<Hash128>();
        await foreach (var c in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            second.Add(c.Metadata.IntentId);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }

    [Fact]
    public async Task Estimate_Reports_Full_Codepoint_Space()
    {
        var dec = NewDecomposer();
        Assert.Equal(TotalCodepoints, await dec.EstimateUnitCountAsync(Context(new NullWriter())));
    }

    // === fakes ===

    private sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath => "/vault/Data/Unicode";
        public ISubstrateWriter Writer { get; } = writer;
        public ISubstrateReader Reader { get; } = new NullReader();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public string SubstrateVersion => "test";
    }

    private sealed class NullWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
            => Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
    }

    private sealed class CapturingWriter : ISubstrateWriter
    {
        public List<SubstrateChange> Captured { get; } = new();
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            Captured.Add(change);
            return Task.FromResult(new ApplyResult(
                change.Entities.Length, change.Entities.Length,
                change.Physicalities.Length, change.Physicalities.Length,
                change.Attestations.Length, change.Attestations.Length, 4, TimeSpan.Zero, false));
        }
    }

    private sealed class NullReader : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
