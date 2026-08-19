using System.IO.Compression;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.OpenSubtitles.Tests;

public sealed class OpenSubtitlesDecomposerTests
{
    static OpenSubtitlesDecomposerTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(ResolvePerfcacheBlob());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    private static string ResolvePerfcacheBlob() => TestInstall.ResolvePerfcacheOrThrow();

    private static readonly string[] En = { "Hello there.", "What is your name?", "" };
    private static readonly string[] Es = { "Hola allí.", "¿Cómo te llamas?", "" };

    [Fact]
    public async Task DescribeInput_Includes_All_Pairs_By_Default()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-opensub-pairs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteFixtureZip(dir, "en-es");
            WriteFixtureZip(dir, "en-fr");

            var dec = new OpenSubtitlesDecomposer();
            var inv = await dec.DescribeInputAsync(new FakeContext(dir, new NullWriter()), DecomposerOptions.Default);
            Assert.NotNull(inv);
            Assert.Equal(2, inv!.Files.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string WriteFixtureZip(string dir, string pair)
    {
        string zipPath = Path.Combine(dir, pair + ".txt.zip");
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, $"OpenSubtitles.{pair}.en", En);
        WriteEntry(zip, $"OpenSubtitles.{pair}.es", Es);
        return zipPath;
    }

    private static string WriteFixtureZip(string dir)
    {
        string zipPath = Path.Combine(dir, "en-es.txt.zip");
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "OpenSubtitles.en-es.en", En);
        WriteEntry(zip, "OpenSubtitles.en-es.es", Es);
        WriteEntry(zip, "README", new[] { "a corpus" });
        WriteEntry(zip, "LICENSE", new[] { "terms" });
        return zipPath;
    }

    private static void WriteEntry(ZipArchive zip, string name, string[] lines)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        foreach (var line in lines) w.WriteLine(line);
    }

    [Fact]
    public async Task Emits_Aligned_Sequence_Structure_Without_Pairwise_Consensus()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-opensub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteFixtureZip(dir);

            var dec = new OpenSubtitlesDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());

            var entities = new Dictionary<Hash128, EntityRow>();
            var physicalities = new Dictionary<Hash128, PhysicalityRow>();
            int translationEdges = 0, languageEdges = 0, intentStages = 0;
            var langObjects = new HashSet<Hash128>();
            var languageSubjects = new HashSet<Hash128>();
            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                if (change.Metadata.SourceContentUnitName.StartsWith(
                        IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    continue;
                intentStages += change.IntentStages.Length;
                foreach (var e in change.Entities) entities[e.Id] = e;
                foreach (var p in change.Physicalities) physicalities[p.EntityId] = p;
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == translationType)
                    {
                        translationEdges++;
                    }
                    else if (a.TypeId == languageType)
                    {
                        languageEdges++;
                        languageSubjects.Add(a.SubjectId);
                        if (a.ObjectId is { } o) langObjects.Add(o);
                    }
                    Assert.Equal(languageType, a.TypeId);
                }
            }

            Assert.Equal(0, translationEdges);
            Assert.Equal(2, languageEdges);
            Assert.True(intentStages > 0, "content witness batches should populate IntentStages");

            Hash128 enId = LanguageReference.Resolve("en");
            Hash128 esId = LanguageReference.Resolve("es");
            Assert.Contains(enId, langObjects);
            Assert.Contains(esId, langObjects);

            Assert.Contains(enId, entities.Keys);
            Assert.Contains(esId, entities.Keys);

            Hash128? helloId = ContentEmitter.RootId("Hello there.");
            Hash128? holaId = ContentEmitter.RootId("Hola allí.");
            Hash128? whatId = ContentEmitter.RootId("What is your name?");
            Hash128? comoId = ContentEmitter.RootId("¿Cómo te llamas?");
            Assert.NotNull(helloId);
            Assert.NotNull(holaId);
            Assert.NotNull(whatId);
            Assert.NotNull(comoId);

            Hash128 sequenceSchema =
                Hash128.OfCanonical("opensubtitles/sequence-block512/schema/v1");
            Hash128 pairReference =
                Hash128.OfCanonical("opensubtitles/language-pair/en-es/v1");
            Hash128 leftSequence = Hash128.Merkle(
                EntityTier.Document, [sequenceSchema, helloId!.Value, whatId!.Value]);
            Hash128 rightSequence = Hash128.Merkle(
                EntityTier.Document, [sequenceSchema, holaId!.Value, comoId!.Value]);

            Assert.Equal(EntityTypeRegistry.OpenSubtitlesSequence, entities[leftSequence].TypeId);
            Assert.Equal(EntityTypeRegistry.OpenSubtitlesSequence, entities[rightSequence].TypeId);
            Assert.Equal(
                [sequenceSchema, helloId.Value, whatId.Value],
                Trajectory.Constituents(physicalities[leftSequence].TrajectoryXyzm!));
            Assert.Equal(
                [sequenceSchema, holaId.Value, comoId.Value],
                Trajectory.Constituents(physicalities[rightSequence].TrajectoryXyzm!));
            Assert.True(languageSubjects.SetEquals([leftSequence, rightSequence]));

            Hash128 start = Hash128.OfCanonical("opensubtitles/source-ordinal/1/v1");
            Hash128 end = Hash128.OfCanonical("opensubtitles/source-ordinal/2/v1");
            Hash128 alignmentSchema =
                Hash128.OfCanonical("opensubtitles/alignment-block512/schema/v1");
            Hash128[] alignmentMembers =
            [
                alignmentSchema, pairReference,
                enId, leftSequence, esId, rightSequence, start, end,
            ];
            Hash128 alignment = Hash128.Merkle(EntityTier.Document, alignmentMembers);
            Assert.Equal(EntityTypeRegistry.OpenSubtitlesAlignment, entities[alignment].TypeId);
            Assert.Equal(
                alignmentMembers,
                Trajectory.Constituents(physicalities[alignment].TrajectoryXyzm!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_Bootstraps_Source_Alignment_Types_And_Language_Relation()
    {
        var dec = new OpenSubtitlesDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(TestIngestPaths.OpenSubtitles, writer));

        Assert.NotEmpty(writer.Captured);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == OpenSubtitlesDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.OpenSubtitlesSequence
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Contains(boot.Entities, e =>
            e.Id == EntityTypeRegistry.OpenSubtitlesAlignment
            && e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId);
        Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        Assert.Contains(boot.Entities, e => e.Id == languageType);
    }

    [Fact]
    public async Task Estimate_Reports_Published_Pair_Total()
    {
        var dec = new OpenSubtitlesDecomposer();
        Assert.Equal(600_995_230L, await dec.EstimateUnitCountAsync(new FakeContext(TestIngestPaths.OpenSubtitles, new NullWriter())));
    }

}
