using System.IO.Compression;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Decomposers.OpenSubtitles.Tests;

/// <summary>
/// Verifies the OpenSubtitles Moses-format decomposer on a small inline fixture: two
/// parallel line-aligned entries in one ".txt.zip" → content + IS_TRANSLATION_OF +
/// HAS_LANGUAGE, all routed through the registry. Needs liblaplace_core.so + a loaded
/// T0 perf-cache (the documented host precondition for any content-bearing decomposer).
/// </summary>
public sealed class OpenSubtitlesDecomposerTests
{
    static OpenSubtitlesDecomposerTests()
    {
        // Content emission routes text through ContentEmitter → the perf-cache must be
        // loaded (same static-ctor pattern as UnicodeDecomposerTests).
        CodepointPerfcache.Load(ResolvePerfcacheBlob());
        // HAS_LANGUAGE / language-entity resolution reads the ISO 639 reference index.
        LanguageReference.EnsureLoaded();
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

    // Inline aligned fixture (en ⇥ es), line N ↔ line N.
    private static readonly string[] En = { "Hello there.", "What is your name?", "" /* blank skipped */ };
    private static readonly string[] Es = { "Hola allí.",   "¿Cómo te llamas?",  "" };

    private static string WriteFixtureZip(string dir)
    {
        // Mirror the real OPUS naming: OpenSubtitles.<pair>.<langSuffix>, two parallel entries.
        string zipPath = Path.Combine(dir, "en-es.txt.zip");
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        WriteEntry(zip, "OpenSubtitles.en-es.en", En);
        WriteEntry(zip, "OpenSubtitles.en-es.es", Es);
        WriteEntry(zip, "README",  new[] { "a corpus" });   // non-text entry, must be ignored
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
            int translationEdges = 0, languageEdges = 0;
            var langObjects = new HashSet<Hash128>();
            Hash128 translationKind = KindRegistry.Resolve("IS_TRANSLATION_OF").Id;
            Hash128 languageKind     = KindRegistry.Resolve("HAS_LANGUAGE").Id;

            await foreach (var change in dec.DecomposeAsync(ctx, DecomposerOptions.Default))
            {
                foreach (var e in change.Entities) entities.Add(e.Id);
                foreach (var a in change.Attestations)
                {
                    if (a.KindId == translationKind) translationEdges++;
                    else if (a.KindId == languageKind) { languageEdges++; if (a.ObjectId is { } o) langObjects.Add(o); }
                    // Every attestation is routed through the registry (a known canonical kind).
                    Assert.True(a.KindId == translationKind || a.KindId == languageKind,
                        "only registry-routed IS_TRANSLATION_OF / HAS_LANGUAGE kinds are emitted");
                }
            }

            // Two non-blank aligned lines → 2 translation edges + 4 HAS_LANGUAGE edges
            // (one per sentence per line). The trailing blank line is skipped.
            Assert.Equal(2, translationEdges);
            Assert.Equal(4, languageEdges);

            // The language objects are the omni-glottal canonical entities, resolved
            // identically to every other source (en → eng, es → spa).
            Hash128 enId = LanguageReference.Resolve("en");
            Hash128 esId = LanguageReference.Resolve("es");
            Assert.Contains(enId, langObjects);
            Assert.Contains(esId, langObjects);

            // Both language entities were emitted (so the HAS_LANGUAGE FK is satisfiable).
            Assert.Contains(enId, entities);
            Assert.Contains(esId, entities);

            // Both sentences landed as content: the IS_TRANSLATION_OF endpoints are
            // exactly the content-addressed root ids of the surfaces.
            Hash128? helloId = ContentEmitter.RootId("Hello there.");
            Hash128? holaId  = ContentEmitter.RootId("Hola allí.");
            Assert.NotNull(helloId);
            Assert.NotNull(holaId);
            Assert.Contains(helloId!.Value, entities);
            Assert.Contains(holaId!.Value, entities);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_Bootstraps_Source_And_Translation_Kind()
    {
        var dec = new OpenSubtitlesDecomposer();
        var writer = new CapturingWriter();
        await dec.InitializeAsync(new FakeContext("/vault/Data/OpenSubtitles", writer));

        Assert.NotEmpty(writer.Captured);
        var boot = writer.Captured[0];

        Assert.Contains(boot.Entities, e =>
            e.Id == OpenSubtitlesDecomposer.Source && e.TypeId == BootstrapIntentBuilder.SourceTypeId);
        // IS_TRANSLATION_OF kind entity is seeded.
        Hash128 translationKind = KindRegistry.Resolve("IS_TRANSLATION_OF").Id;
        Assert.Contains(boot.Entities, e => e.Id == translationKind);
    }

    [Fact]
    public async Task Estimate_Reports_Published_Pair_Total()
    {
        var dec = new OpenSubtitlesDecomposer();
        // Sum of PROVENANCE.md per-pair aligned-pair counts.
        Assert.Equal(600_995_230L, await dec.EstimateUnitCountAsync(new FakeContext("/vault/Data/OpenSubtitles", new NullWriter())));
    }

    // === fakes ===

    private sealed class FakeContext(string ecosystemPath, ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath => ecosystemPath;
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
