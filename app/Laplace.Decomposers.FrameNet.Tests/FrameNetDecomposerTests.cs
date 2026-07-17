using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.FrameNet.Tests;

/// <summary>
/// Verifies FrameNetDecomposer's frame-file extraction against a tiny inline XML fixture:
/// the right attestation kinds are present and every attestation/entity is registry-routed
/// (canonical kind ids, no raw rows). ContentEmitter routes lemmas/definitions/examples
/// through the T0 perf-cache, so the static ctor loads it (same host precondition every
/// content-bearing decomposer has).
/// </summary>
public sealed class FrameNetDecomposerTests
{
    static FrameNetDecomposerTests()
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

    // A minimal but faithful frame/*.xml: one frame, one Core + one Peripheral FE (each
    // with a def-root definition + an inline <ex> example), two embedded LUs (a verb and a
    // noun), and three directional relations (Inherits from → IS_A; Uses → FRAME_USES;
    // Subframe of → HAS_SUBEVENT-flipped). The inverse-direction "Is Inherited by" is
    // present and MUST be skipped (one arena, the registry flips for the reverse query).
    private const string FrameXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<frame name="Giving" ID="139" xmlns="http://framenet.icsi.berkeley.edu">
    <definition>&lt;def-root&gt;A &lt;fex name="Donor"&gt;Donor&lt;/fex&gt; transfers a &lt;fen&gt;Theme&lt;/fen&gt; to a Recipient. &lt;ex&gt;&lt;fex name="Donor"&gt;She&lt;/fex&gt; &lt;t&gt;gave&lt;/t&gt; him a book.&lt;/ex&gt;&lt;/def-root&gt;</definition>
    <FE coreType="Core" name="Donor" ID="1">
        <definition>&lt;def-root&gt;The person that gives the &lt;fen&gt;Theme&lt;/fen&gt;.&lt;/def-root&gt;</definition>
    </FE>
    <FE coreType="Peripheral" name="Place" ID="2">
        <definition>&lt;def-root&gt;Where the giving happens.&lt;/def-root&gt;</definition>
    </FE>
    <lexUnit status="Finished_Initial" POS="V" name="give.v" ID="4344"/>
    <lexUnit status="Finished_Initial" POS="N" name="donation.n" ID="5345"/>
    <frameRelation type="Inherits from">
        <relatedFrame ID="206">Transfer</relatedFrame>
    </frameRelation>
    <frameRelation type="Uses">
        <relatedFrame ID="198">Intentionally_act</relatedFrame>
    </frameRelation>
    <frameRelation type="Subframe of">
        <relatedFrame ID="300">Commerce_scenario</relatedFrame>
    </frameRelation>
    <frameRelation type="Is Inherited by">
        <relatedFrame ID="999">Donating</relatedFrame>
    </frameRelation>
</frame>
""";

    private static FrameNetDecomposer.Frame ParseFixture() =>
        FrameNetDecomposer.ParseFrame(XDocument.Parse(FrameXml))
        ?? throw new InvalidOperationException("fixture failed to parse");

    [Fact]
    public void ParseFrame_Extracts_Frame_FEs_LUs_And_CanonicalDirectionRelations_Only()
    {
        var f = ParseFixture();

        Assert.Equal("Giving", f.Name);
        Assert.Contains("transfers a Theme to a Recipient", f.Definition);
        // <ex> is split out of the definition body, not left inline.
        Assert.DoesNotContain("gave him a book", f.Definition);
        Assert.Contains(f.Examples, e => e.Contains("gave him a book"));

        Assert.Equal(2, f.Elements.Count);
        Assert.Contains(f.Elements, fe => fe.Name == "Donor" && fe.CoreType == "Core");
        Assert.Contains(f.Elements, fe => fe.Name == "Place" && fe.CoreType == "Peripheral");
        Assert.Contains(f.Elements, fe => fe.Name == "Donor" && fe.Definition.Contains("person that gives"));

        Assert.Equal(2, f.LexUnits.Count);
        Assert.Contains(f.LexUnits, lu => lu.Lemma == "give" && lu.Pos == "V");
        Assert.Contains(f.LexUnits, lu => lu.Lemma == "donation" && lu.Pos == "N");

        // Only the three canonical-direction relations; "Is Inherited by" is skipped.
        Assert.Equal(3, f.Relations.Count);
        Assert.Contains(f.Relations, r => r.Type == "Inherits from" && r.TargetFrame == "Transfer");
        Assert.Contains(f.Relations, r => r.Type == "Uses" && r.TargetFrame == "Intentionally_act");
        Assert.Contains(f.Relations, r => r.Type == "Subframe of" && r.TargetFrame == "Commerce_scenario");
        Assert.DoesNotContain(f.Relations, r => r.Type == "Is Inherited by");
    }

    [Fact]
    public async Task Attestations_Use_RegistryRouted_Canonical_Kind_Ids()
    {
        var atts = await CollectAttestationsAsync();

        // Every kind id on every attestation is a canonical-registry kind id (registry-routed,
        // never a raw row / hand-built "substrate/kind/..." hash).
        var canonical = new HashSet<Hash128>(KindRegistry.AllCanonical().Select(k => k.Id));
        Assert.All(atts, a => Assert.Contains(a.TypeId, canonical));

        // The load-bearing arenas are present.
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("EVOKES_FRAME"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("HAS_FRAME_ELEMENT"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("HAS_DEFINITION"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("HAS_POS"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("HAS_EXAMPLE"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("FRAME_USES"));

        // Inherits from resolves to IS_A; Subframe of resolves to HAS_SUBEVENT (aliases).
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("IS_A"));
        Assert.Contains(atts, a => a.TypeId == KindRegistry.KindId("HAS_SUBEVENT"));
    }

    [Fact]
    public async Task EvokesFrame_Targets_Frame_Meta_And_CoreType_Rides_Context()
    {
        var atts = await CollectAttestationsAsync();
        var b = new SubstrateChangeBuilder(FrameNetDecomposer.Source, "fixture", null);
        // "give" wordform → EVOKES_FRAME → Giving frame meta entity.
        var giveId = ContentEmitter.Emit(b, "give", FrameNetDecomposer.Source);
        var frameId = Hash128.OfCanonical("framenet/frame/Giving");
        Assert.NotNull(giveId);
        Assert.Contains(atts, a =>
            a.TypeId == KindRegistry.KindId("EVOKES_FRAME")
            && a.SubjectId == giveId!.Value
            && a.ObjectId == frameId);

        // HAS_FRAME_ELEMENT carries the coreness classifier as context_id (Core for Donor).
        var coreCtx = Hash128.OfCanonical("framenet/coreness/Core");
        Assert.Contains(atts, a =>
            a.TypeId == KindRegistry.KindId("HAS_FRAME_ELEMENT") && a.ContextId == coreCtx);
    }

    [Fact]
    public async Task Bootstrap_Registers_Source_Types_And_KindEntities()
    {
        var dec = new FrameNetDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(writer));

        // Two intents: (1) source/type/kind bootstrap, (2) POS + coreness classifier seed.
        Assert.Equal(2, writer.Captured.Count);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == FrameNetDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == Hash128.OfCanonical("substrate/type/FrameNet_Frame/v1")
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        // EVOKES_FRAME kind entity is registered (FK floor) with the same id the registry uses.
        Assert.Contains(boot.Entities, e => e.Id == KindRegistry.KindId("EVOKES_FRAME"));
        // HAS_TRUST_CLASS: source → AcademicCurated.
        Assert.Contains(boot.Attestations, a =>
            a.SubjectId == FrameNetDecomposer.Source
            && a.TypeId == BootstrapIntentBuilder.HasTrustClassKindId
            && a.ObjectId == FrameNetDecomposer.TrustClass);

        // The coreness classifier entities are seeded in the second intent.
        Assert.Contains(writer.Captured[1].Entities, e =>
            e.Id == Hash128.OfCanonical("framenet/coreness/Core"));
    }

    // Run the fixture frame through the decomposer's attestation emitter via a one-frame dir.
    private static async Task<List<AttestationRow>> CollectAttestationsAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "frame"));
        await File.WriteAllTextAsync(Path.Combine(dir, "frame", "Giving.xml"), FrameXml);
        try
        {
            var dec = new FrameNetDecomposer();
            var ctx = new FakeContext(new NullWriter()) { EcosystemPath = dir };
            var atts = new List<AttestationRow>();
            // Drain both passes (entities then attestations); collect the attestation rows.
            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
                atts.AddRange(change.Attestations.ToArray());
            return atts;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // === fakes ===

    private sealed class FakeContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath { get; init; } = "/vault/Data/FrameNet/framenet_v17";
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
