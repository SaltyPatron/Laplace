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
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
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
    public async Task Emits_Content_Translation_And_Language_For_Aligned_Pairs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-opensub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteFixtureZip(dir);

            var dec = new OpenSubtitlesDecomposer();
            var ctx = new FakeContext(dir, new NullWriter());

            var entities = new HashSet<Hash128>();
            int translationEdges = 0, languageEdges = 0, intentStages = 0;
            var langObjects = new HashSet<Hash128>();
            var translationSubjects = new HashSet<Hash128>();
            var translationObjects = new HashSet<Hash128>();
            Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 languageType = RelationTypeRegistry.Resolve("HAS_LANGUAGE").Id;

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                intentStages += change.IntentStages.Length;
                foreach (var e in change.Entities) entities.Add(e.Id);
                foreach (var a in change.Attestations)
                {
                    if (a.TypeId == translationType)
                    {
                        translationEdges++;
                        translationSubjects.Add(a.SubjectId);
                        if (a.ObjectId is { } to) translationObjects.Add(to);
                    }
                    else if (a.TypeId == languageType) { languageEdges++; if (a.ObjectId is { } o) langObjects.Add(o); }
                    Assert.True(a.TypeId == translationType || a.TypeId == languageType,
                        "only registry-routed IS_TRANSLATION_OF / HAS_LANGUAGE types are emitted");
                }
            }

            Assert.Equal(2, translationEdges);
            Assert.Equal(4, languageEdges);
            Assert.True(intentStages > 0, "content witness batches should populate IntentStages");

            Hash128 enId = LanguageReference.Resolve("en");
            Hash128 esId = LanguageReference.Resolve("es");
            Assert.Contains(enId, langObjects);
            Assert.Contains(esId, langObjects);

            Assert.Contains(enId, entities);
            Assert.Contains(esId, entities);

            Hash128? helloId = ContentEmitter.RootId("Hello there.");
            Hash128? holaId = ContentEmitter.RootId("Hola allí.");
            Assert.NotNull(helloId);
            Assert.NotNull(holaId);
            Assert.Contains(helloId!.Value, translationSubjects);
            Assert.Contains(holaId!.Value, translationObjects);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_Bootstraps_Source_And_Translation_RelationType()
    {
        var dec = new OpenSubtitlesDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext(TestIngestPaths.OpenSubtitles, writer));

        Assert.NotEmpty(writer.Captured);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == OpenSubtitlesDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        Hash128 translationType = RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id;
        Assert.Contains(boot.Entities, e => e.Id == translationType);
    }

    [Fact]
    public async Task Estimate_Reports_Published_Pair_Total()
    {
        var dec = new OpenSubtitlesDecomposer();
        Assert.Equal(600_995_230L, await dec.EstimateUnitCountAsync(new FakeContext(TestIngestPaths.OpenSubtitles, new NullWriter())));
    }

}
