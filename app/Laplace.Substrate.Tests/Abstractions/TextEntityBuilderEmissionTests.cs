using System.Collections.Generic;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class TextEntityBuilderEmissionTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "TextEmission");

    [Theory]
    [InlineData("foo.\r\n\r\n")]
    [InlineData(" to hear about new eBooks.\r\n\r\n")]
    public void TrailingDoubleNewline_RoundtripsFromPhysicalities(string text)
        => AssertRoundtrip(Encoding.UTF8.GetBytes(text));

    private static string GalileoPath()
    {
        try { return Path.Combine(LaplaceInstall.ResolveIngestRoot(), "test-data", "text", "galileo.txt"); }
        catch (InvalidOperationException)
        {
            return OperatingSystem.IsWindows()
                ? @"D:\Data\Ingest\test-data\text\galileo.txt"
                : "/vault/Data/test-data/text/galileo.txt";
        }
    }

    [Fact]
    public void GeneratedLargeDocument_InMemory_RoundtripsFromPhysicalities()
    {
        // Keep the large-document DAG/reconstruction path in every DEV/BAT run.
        // The physical Galileo corpus is useful additional evidence when mounted,
        // but a developer/CI machine must not lose this law merely because that
        // external corpus is absent. Diverse separators, Unicode, paragraph
        // boundaries, repeated phrases and unique ordinals exercise the same
        // document/sentence/word/grapheme composition and trajectory reconstruction.
        var text = new StringBuilder(180_000);
        for (int i = 0; i < 768; i++)
        {
            text.Append("Observation ").Append(i)
                .Append(": Galileo measured motion — café, κόσμος, 狼, and stars. ")
                .Append("Repeated evidence remains content-addressed; order remains trajectory geometry.\r\n")
                .Append("Second sentence ").Append(i).Append(" keeps punctuation (A/B), numbers ")
                .Append(i * 17).Append(", and tabs\tinside the same paragraph.\r\n\r\n");
        }

        AssertRoundtrip(Encoding.UTF8.GetBytes(text.ToString()));
    }

    [SkippableFact]
    public void FullGalileo_InMemory_RoundtripsFromPhysicalities()
    {
        string path = GalileoPath();
        Skip.IfNot(File.Exists(path), $"external document corpus not present: {path}");
        AssertRoundtrip(File.ReadAllBytes(path));
    }

    private static void AssertRoundtrip(byte[] bytes)
    {
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out var phys, out var rootId, out _));
        byte[] rebuilt = ReconstructFromPhysicalities(phys, rootId);
        Assert.Equal(Nfc(bytes), rebuilt);
    }

    private static unsafe byte[] Nfc(byte[] utf8)
    {
        if (utf8.Length == 0) return utf8;
        byte* outPtr = null;
        nuint outLen = 0;
        fixed (byte* p = utf8)
        {
            if (NativeInterop.NormalizeNfcUtf8(p, (nuint)utf8.Length, &outPtr, &outLen) != 0)
                return utf8;
        }
        try { return new ReadOnlySpan<byte>(outPtr, (int)outLen).ToArray(); }
        finally { if (outPtr != null) System.Runtime.InteropServices.NativeMemory.Free(outPtr); }
    }

    private static byte[] ReconstructFromPhysicalities(
        System.Collections.Immutable.ImmutableArray<PhysicalityRow> phys, Hash128 rootId)
    {
        var idToCp = new Dictionary<Hash128, uint>(1_114_112);
        ReadOnlySpan<CodepointRecord> recs = CodepointPerfcache.Records;
        for (int i = 0; i < recs.Length; i++) idToCp[recs[i].Hash] = recs[i].Codepoint;

        var children = new Dictionary<Hash128, Hash128[]>();
        foreach (var p in phys)
        {
            if (p.TrajectoryXyzm is not { Length: > 0 } xyzm) continue;
            children[p.EntityId] = Trajectory.Constituents(xyzm);
        }

        var sb = new StringBuilder();
        Emit(rootId, children, idToCp, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Emit(Hash128 id, Dictionary<Hash128, Hash128[]> children,
                             Dictionary<Hash128, uint> idToCp, StringBuilder sb)
    {
        if (idToCp.TryGetValue(id, out uint cp)) { sb.Append(char.ConvertFromUtf32((int)cp)); return; }
        if (children.TryGetValue(id, out var kids))
            foreach (var k in kids) Emit(k, children, idToCp, sb);
    }

    [Fact]
    public void SingleWord_Suppresses_Document_And_Sentence_Wrappers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("dog");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out var rootId, out var rootTier));

        Assert.Equal(EntityTier.Word, rootTier);
        Assert.DoesNotContain(ents, e => e.Tier >= EntityTier.Sentence);

        var root = Assert.Single(ents, e => e.Id.EqualsBytewise(rootId));
        Assert.Equal(EntityTier.Word, root.Tier);
        Assert.Equal(TextEntityBuilder.WordTypeId, root.TypeId);
    }

    [Fact]
    public void MultiSentence_Keeps_Structural_Tiers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Hello world. Second sentence.");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out _, out _));
        Assert.Contains(ents, e => e.Tier == EntityTier.Sentence);
    }

    [Fact]
    public void ContentWitness_Builds_DAG_Without_Distributional_Attestations()
    {
        // Pillar-3a: text emits NO PRECEDES/CONTAINS distributional attestations — sequence is the
        // trajectory geometry and containment is containers_of; PRECEDES is a MODEL relation. The
        // content DAG (entities + physicalities) is still built; the distributional stream is empty.
        byte[] bytes = Encoding.UTF8.GetBytes("Brave whales chase tiny boats. Second sentence holds more words.");
        Assert.True(TextEntityBuilder.TryBuildContentWitness(bytes, Src, 1.0,
            out var ents, out _, out var atts, out var rootId, out _));

        Assert.Empty(atts);
        Assert.Contains(ents, e => e.Id.EqualsBytewise(rootId));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData(".")]
    public void SingleAtomRetainsItsActualFloorPhysicalityWithoutAWrapper(string text)
    {
        Assert.True(TextEntityBuilder.TryBuildRows(Encoding.UTF8.GetBytes(text), Src,
            out var entities, out var physicalities, out var root, out var tier));
        ref readonly var atom = ref CodepointPerfcache.Records[text[0]];
        Assert.Equal(atom.Hash, root);
        Assert.Equal((byte)0, tier);
        Assert.Empty(entities);
        var physicality = Assert.Single(physicalities);
        Assert.Equal(root, physicality.EntityId);
        Assert.Equal(PhysicalityId.Compute(root, PhysicalityType.Content), physicality.Id);
        Assert.Equal(Src, physicality.SourceId);
        Assert.Equal(atom.CoordX, physicality.CoordX);
        Assert.Equal(atom.CoordY, physicality.CoordY);
        Assert.Equal(atom.CoordZ, physicality.CoordZ);
        Assert.Equal(atom.CoordM, physicality.CoordM);
        Assert.Equal(atom.Hilbert, physicality.HilbertIndex);
        Assert.Null(physicality.TrajectoryXyzm);
        Assert.Equal(0, physicality.NConstituents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void ManagedRowsExactlyMatchNativeEmissionIncludingKnownRawForms(bool known)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("alpha alpha. alpha beta.");
        using var tree = TextDecomposer.Run(bytes);
        HashComposer.Run(tree, &TextEntityBuilder.Resolver);
        byte[]? bitmap = known ? Enumerable.Repeat((byte)255, (tree.NodeCount + 7) / 8).ToArray() : null;
        using var expected = IntentStage.New(tree.NodeCount);
        Assert.True(expected.EmitContentTree(tree, Src, bitmap, out var expectedRoot));
        var (entities, physicalities) = new TextEntityBuilder(tree, Src, bitmap).Build();
        if (known) Assert.Empty(entities);
        Assert.NotEmpty(physicalities);
        Assert.True(physicalities.Length > physicalities.Select(p => p.EntityId).Distinct().Count(),
            "Repeated content must retain each actual native raw occurrence.");
        using var transported = IntentStage.New(tree.NodeCount);
        foreach (var entity in entities)
            transported.AddEntity(entity.Id, entity.Tier, entity.TypeId, entity.FirstObservedBy);
        foreach (var p in physicalities)
        {
            Assert.Equal(Src, p.SourceId);
            transported.AddPhysicality(p.Id, p.EntityId, (short)p.Type,
                new double[] { p.CoordX, p.CoordY, p.CoordZ, p.CoordM }, p.HilbertIndex,
                p.TrajectoryXyzm, p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
        }
        Assert.Equal(expected.EmitCopyBinary(IntentStageTable.Entities),
            transported.EmitCopyBinary(IntentStageTable.Entities));
        Assert.Equal(expected.EmitCopyBinary(IntentStageTable.Physicalities),
            transported.EmitCopyBinary(IntentStageTable.Physicalities));
        Assert.Equal(bytes, ReconstructFromPhysicalities(physicalities, expectedRoot));
    }

    [Fact]
    public void PromptSourceUnitsRetainExplicitPriorAndAnAtomicObservationEach()
    {
        Assert.True(UserPromptContent.TryBuildWitnessChange(Encoding.UTF8.GetBytes("A"), "prompt-one",
            out var first, out var firstRoot));
        Assert.True(UserPromptContent.TryBuildWitnessChange(Encoding.UTF8.GetBytes("A"), "prompt-two",
            out var second, out var secondRoot));
        Assert.Equal(firstRoot, secondRoot);
        Assert.NotEqual(first.Metadata.IntentId, second.Metadata.IntentId);
        foreach (var change in new[] { first, second })
        {
            var physicality = Assert.Single(change.PhysicalityObservations);
            Assert.Equal(firstRoot, physicality.EntityId);
            Assert.Equal(UserPromptContent.Source, physicality.SourceId);
            Assert.Equal(SourceTrust.UserPrompt, change.RequireSourcePrior(physicality.SourceId));
            Assert.Empty(change.Attestations); // HAS evidence is the normal writer's later native admission.
        }
    }

    [Fact]
    public unsafe void ExistingBitmapMustCoverTheActualNativeTree()
    {
        using var tree = TextDecomposer.Run(Encoding.UTF8.GetBytes("a longer tree with many source nodes"));
        HashComposer.Run(tree, &TextEntityBuilder.Resolver);
        Assert.True(tree.NodeCount > 8);
        var error = Assert.Throws<ArgumentException>(() => new TextEntityBuilder(tree, Src, [255]).Build());
        Assert.Contains("cover every source tree node", error.Message);
    }
}
