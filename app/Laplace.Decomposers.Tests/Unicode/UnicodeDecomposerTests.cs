using System.Collections.Immutable;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Unicode.Tests;

public sealed class UnicodeDecomposerTests
{
    static UnicodeDecomposerTests()
    {
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

    private static UnicodeDecomposer NewDecomposer() => new UnicodeDecomposer();

    private static IDecomposerContext Context(ISubstrateWriter writer) =>
        new FakeContext(TestIngestPaths.UcdLatest, writer);


    [Fact]
    public async Task Emits_All_Codepoints_As_T0_Entities_With_Content_Physicalities()
    {
        var dec = NewDecomposer();
        var ctx = Context(new NullWriter());

        await dec.EstimateUnitCountAsync(ctx);
        Hash128 aHash = Hash128.Blake3(new byte[] { 0x41 });

        var codepointEntities = new HashSet<Hash128>();
        long codepointPhysicalities = 0, passThreeEntities = 0;
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
                    codepointEntities.Add(e.Id);
                    if (e.Tier != 0) allTier0 = false;
                    if (e.FirstObservedBy != UnicodeDecomposer.Source) allFirstObserved = false;
                    if (aEntity is null && e.Id == aHash)
                    {
                        aEntity = e;
                        foreach (var ph in change.Physicalities)
                            if (ph.EntityId == aHash) { aPhys = ph; break; }
                    }
                }
                else
                {
                    passThreeEntities++;
                }
            }
            foreach (var ph in change.Physicalities)
                if (ph.Type == PhysicalityType.Content && ph.TrajectoryXyzm is null)
                    codepointPhysicalities++;
        }

        Assert.Equal(TotalCodepoints, codepointEntities.Count);
        Assert.True(codepointPhysicalities >= TotalCodepoints,
            "one CONTENT physicality per codepoint (pass-3 content adds more)");
        Assert.True(passThreeEntities > 0,
            "pass 3 must witness name aliases / confusable sequences as content");
        Assert.True(allTier0, "all codepoint entities are tier 0");
        Assert.True(allFirstObserved, "all codepoint entities first_observed_by UnicodeDecomposer");

        Assert.NotNull(aEntity);
        Assert.NotNull(aPhys);
        Assert.Equal(PhysicalityType.Content, aPhys!.Type);
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

        Assert.Equal(2, writer.Captured.Count);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == UnicodeDecomposer.CodepointType && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == UnicodeDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId
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

}
