using System.Collections.Immutable;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

/// <summary>
/// Verifies the T0 codepoint seed via <see cref="UnicodeSeed.Compute"/> on UCD/DUCET
/// (sibling of the perf-cache blob — seed does not mmap the blob).
/// Needs liblaplace_core.so + <c>/vault/Data/Unicode</c> paths on the test host.
/// </summary>
public sealed class UnicodeDecomposerTests
{
    static UnicodeDecomposerTests()
    {
        // Pass 3 (name aliases / confusables, 2026-06-05 completeness) routes
        // text through ContentEmitter — the documented host precondition is a
        // loaded T0 perf-cache (same as every content-bearing decomposer).
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
    }

    private static string ResolvePerfcacheBlob()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                                                   SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        throw new InvalidOperationException("perf-cache blob not found; build the engine or set LAPLACE_PERFCACHE_BIN.");
    }

    private const int TotalCodepoints = 1_114_112;

    // Use the same path discipline as production: the decomposer reads source
    // paths from IDecomposerContext.EcosystemPath. The test context below
    // supplies it; the decomposer ctor takes no path args here.
    private static UnicodeDecomposer NewDecomposer() => new UnicodeDecomposer();

    private static IDecomposerContext Context(ISubstrateWriter writer) =>
        new FakeContext(writer);


    [Fact]
    public async Task Emits_All_Codepoints_As_T0_Entities_With_Content_Physicalities()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        await dec.EstimateUnitCountAsync(ctx);
        // 'A' = U+0041, entity id = BLAKE3-128 of single byte 0x41.
        Hash128 aHash = Hash128.Blake3(new byte[] { 0x41 });

        // Pass 1+2 emit the 1,114,112 codepoints; pass 3 (2026-06-05
        // completeness) adds name-alias / confusable-sequence CONTENT — so the
        // CODEPOINT-typed count is exact and the extras are pinned > 0.
        long codepointEntities = 0, codepointPhysicalities = 0, passThreeEntities = 0;
        bool allTier0 = true, allFirstObserved = true;
        EntityRow? aEntity = null;
        PhysicalityRow? aPhys = null;

        await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
        {
            for (int i = 0; i < change.Entities.Length; i++)
            {
                var e = change.Entities[i];
                if (e.TypeId == UnicodeDecomposer.CodepointType)
                {
                    codepointEntities++;
                    if (e.Tier != 0) allTier0 = false;
                    if (e.FirstObservedBy != UnicodeDecomposer.Source) allFirstObserved = false;
                    if (aEntity is null && e.Id == aHash && change.Physicalities.Length > i)
                    {
                        aEntity = e;
                        aPhys = change.Physicalities[i];
                    }
                }
                else
                {
                    passThreeEntities++;
                }
            }
            foreach (var ph in change.Physicalities)
                if (ph.Kind == PhysicalityKind.Content && ph.TrajectoryXyzm is null)
                    codepointPhysicalities++;
        }

        Assert.Equal(TotalCodepoints, codepointEntities);
        Assert.True(codepointPhysicalities >= TotalCodepoints,
            "one CONTENT physicality per codepoint (pass-3 content adds more)");
        Assert.True(passThreeEntities > 0,
            "pass 3 must witness name aliases / confusable sequences as content");
        Assert.True(allTier0, "all codepoint entities are tier 0");
        Assert.True(allFirstObserved, "all codepoint entities first_observed_by UnicodeDecomposer");

        // 'A' is emitted as a tier-0 Codepoint entity with one CONTENT physicality
        // on the unit glome (super-Fibonacci ⇒ x²+y²+z²+m² = 1).
        Assert.NotNull(aEntity);
        Assert.NotNull(aPhys);
        Assert.Equal(PhysicalityKind.Content, aPhys!.Kind);
        double r2 = aPhys.CoordX * aPhys.CoordX + aPhys.CoordY * aPhys.CoordY
                  + aPhys.CoordZ * aPhys.CoordZ + aPhys.CoordM * aPhys.CoordM;
        Assert.InRange(Math.Sqrt(r2), 1.0 - 1e-9, 1.0 + 1e-9);
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

        // Two intents: (1) type/kind bootstrap, (2) classifier entities
        Assert.Equal(2, writer.Captured.Count);
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
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
