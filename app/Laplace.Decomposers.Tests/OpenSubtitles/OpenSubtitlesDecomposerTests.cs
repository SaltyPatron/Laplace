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
            var trajectories = new Dictionary<Hash128, double[]>();
            int intentStages = 0;
            var claims = new List<AttestationRow>();
            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default).WithoutWriter())
            {
                if (change.Metadata.SourceContentUnitName.StartsWith(
                        IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal))
                    continue;
                intentStages += change.IntentStages.Length;
                foreach (var e in StagedChangeRows.Entities(change)) entities[e.Id] = e;
                foreach (var form in StagedChangeRows.Forms(change))
                    if (form.TrajectoryXyzm is { } xyzm) trajectories[form.EntityId] = xyzm;
                claims.AddRange(StagedChangeRows.Attestations(change));
            }

            Assert.DoesNotContain(claims, a => a.TypeId == translationType || a.TypeId == languageType);
            Assert.True(intentStages > 0, "content witness batches should populate IntentStages");

            Hash128 enId = LanguageReference.Resolve("en");
            Hash128 esId = LanguageReference.Resolve("es");
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

            // Each alignment is the ordered pair of [language, sentence] utterances.
            // The zip name, line numbers and 512-record execution batch are packaging.
            Hash128 sequenceSchema =
                Hash128.OfCanonical("opensubtitles/sequence-block512/schema/v1");
            Assert.DoesNotContain(sequenceSchema, entities.Keys);
            Assert.Equal(2, entities.Values.Count(e => e.TypeId == EntityTypeRegistry.OpenSubtitlesAlignment));
            foreach (var (left, right) in new[] { (helloId!.Value, holaId!.Value), (whatId!.Value, comoId!.Value) })
            {
                Hash128 Utterance(Hash128 language, Hash128 sentence)
                {
                    byte tier = checked((byte)(Math.Max(entities[language].Tier, entities[sentence].Tier) + 1));
                    var id = Hash128.Merkle(tier, [language, sentence]);
                    Assert.Equal(EntityTypeRegistry.OpenSubtitlesSequence, entities[id].TypeId);
                    Assert.Equal(tier, entities[id].Tier);
                    Assert.Equal([language, sentence], Trajectory.Constituents(trajectories[id]));
                    return id;
                }
                Hash128 leftUtterance = Utterance(enId, left);
                Hash128 rightUtterance = Utterance(esId, right);
                byte floor = checked((byte)(Math.Max(entities[leftUtterance].Tier, entities[rightUtterance].Tier) + 1));
                Hash128 alignment = Hash128.Merkle(floor, [leftUtterance, rightUtterance]);
                Assert.Equal(EntityTypeRegistry.OpenSubtitlesAlignment, entities[alignment].TypeId);
                Assert.Equal(floor, entities[alignment].Tier);
                Assert.Equal([leftUtterance, rightUtterance], Trajectory.Constituents(trajectories[alignment]));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_Bootstraps_The_Source_And_No_Vocabulary_Rows()
    {
        var dec = new OpenSubtitlesDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(TestIngestPaths.OpenSubtitles, writer));

        Assert.NotEmpty(writer.Captured);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == OpenSubtitlesDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        // Type and relation ids are the content ids of their labels; vocabulary keys
        // are never entity rows.
        Assert.DoesNotContain(boot.Entities, e =>
            e.TypeId == BootstrapIntentBuilder.TypeMetaTypeId
            || e.TypeId == BootstrapIntentBuilder.RelationTypeMetaTypeId);
        Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;
        Assert.DoesNotContain(boot.Entities, e => e.Id == languageType);
    }

    // The published pair total is read from the OPUS OpenSubtitles release itself (hundreds of
    // GB of *.txt.zip moses pairs), so the estimate is checked only where that corpus is staged.
    [SkippableFact]
    public async Task Estimate_Reports_Published_Pair_Total()
    {
        Skip.IfNot(
            Directory.Exists(TestIngestPaths.OpenSubtitles)
                && Directory.EnumerateFiles(TestIngestPaths.OpenSubtitles, "*.txt.zip").Any(),
            $"OpenSubtitles corpus not staged at {TestIngestPaths.OpenSubtitles}");
        var dec = new OpenSubtitlesDecomposer();
        Assert.Equal(600_995_230L, await dec.EstimateUnitCountAsync(new FakeContext(TestIngestPaths.OpenSubtitles, new NullWriter())));
    }

}
