using Laplace.Decomposers.ConceptNet;
using Laplace.Decomposers.Wiktionary;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class WiktionaryInventoryTests
{
    [Fact]
    public async Task DescribeInputAsync_MaxInputUnits_ReturnsCap_NotFullFileCount()
    {
        string dir = CreateTempDir();
        try
        {
            string eng = Path.Combine(dir, "kaikki.org-dictionary-English.jsonl");
            await File.WriteAllTextAsync(eng, "{\"word\":\"a\"}\n{\"word\":\"b\"}\n");

            var dec = new WiktionaryDecomposer();
            var opts = DecomposerOptions.ForWitness("WiktionaryDecomposer", languageOverride: LanguageFilter.FromSpec("en"))
                with { MaxInputUnits = 50_000 };
            var inv = await dec.DescribeInputAsync(new TempContext(dir), opts);

            Assert.NotNull(inv);
            Assert.Equal(50_000, inv.TotalInputUnits);
            Assert.Single(inv.Files);
            Assert.EndsWith("kaikki.org-dictionary-English.jsonl", inv.Files[0].Path);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task DescribeInputAsync_LangsEn_DoesNotPreferMultilingualCorpus()
    {
        string dir = CreateTempDir();
        try
        {
            string multi = Path.Combine(dir, "raw-wiktextract-data.jsonl");
            string eng = Path.Combine(dir, "kaikki.org-dictionary-English.jsonl");
            await File.WriteAllTextAsync(multi, string.Join('\n', Enumerable.Repeat("{\"x\":1}", 100)));
            await File.WriteAllTextAsync(eng, "{\"word\":\"en\"}\n");

            var dec = new WiktionaryDecomposer();
            var opts = DecomposerOptions.ForWitness("WiktionaryDecomposer", languageOverride: LanguageFilter.FromSpec("en"))
                with { MaxInputUnits = 1 };
            var inv = await dec.DescribeInputAsync(new TempContext(dir), opts);

            Assert.NotNull(inv);
            Assert.EndsWith("kaikki.org-dictionary-English.jsonl", inv!.Files[0].Path);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task ConceptNet_DescribeInputAsync_MaxInputUnits_ReturnsCap()
    {
        string dir = CreateTempDir();
        try
        {
            string csv = Path.Combine(dir, "assertions.csv");
            await File.WriteAllTextAsync(csv, "1\tRelatedTo\t/a\t/b\t{}\n");

            var dec = new ConceptNetDecomposer();
            var opts = DecomposerOptions.ForWitness("ConceptNetDecomposer") with { MaxInputUnits = 50_000 };
            var inv = await dec.DescribeInputAsync(new TempContext(dir), opts);

            Assert.NotNull(inv);
            Assert.Equal(50_000, inv!.TotalInputUnits);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static string CreateTempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "laplace-wikt-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class TempContext(string ecosystemPath) : IDecomposerContext
    {
        public string EcosystemPath => ecosystemPath;
        public ISubstrateWriter Writer => throw new NotSupportedException();
        public ISubstrateReader Reader => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
