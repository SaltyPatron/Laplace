using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Structured;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class SourceGenerationRegressionTests
{
    [Fact]
    public async Task Unicode_admits_complete_grouped_xml_without_requiring_reduced_or_duplicate_packages()
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            var recipe = SourceGenerationRecipe.Load(RepositoryFile("recipes/unicode/UCD/17.0.0/unicode.source.json"));
            foreach (var rule in recipe.Rules.Where(r => r.Artifact.Required)) Write(root, rule.Artifact.Selector, "source artifact");
            Write(root, "ucd/.laplace-archive-sha256", "acquisition receipt");
            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Empty(resolved.ExecutionErrors);
            Assert.Contains(resolved.Bindings, b => b.Artifact.RelativePath == "ucdxml/ucd.all.grouped.zip");
            Assert.DoesNotContain(resolved.Bindings, b => b.Artifact.RelativePath.Contains("UnicodeData")
                || b.Artifact.RelativePath.Contains("nounihan") || b.Artifact.RelativePath.Contains("flat"));
            Assert.Equal(5, resolved.Bindings.Count(b => b.Artifact.RelativePath.StartsWith("linkification/")));

            File.Delete(Path.Combine(root, "ucdxml/ucd.all.grouped.zip"));
            Write(root, "ucd/UnicodeData.txt", "a reduced source cannot replace complete XML");
            var writer = new CapturingWriter();
            await using var decomposer = new Laplace.Decomposers.Structured.Decomposer<SourceGenerationRecipe>(recipe, root);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => decomposer.InitializeAsync(new FakeContext(root, writer)));
            Assert.Contains("ucdxml/ucd.all.grouped.zip", error.Message);
            Assert.Empty(writer.Captured);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static string RepositoryFile(string relative, [CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../..", relative));

    [Fact]
    public async Task Unicode_chart_presentations_are_retained_without_hiding_unrecognized_chart_data()
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            var recipe = SourceGenerationRecipe.Load(RepositoryFile("recipes/unicode/UCD/17.0.0/unicode.source.json"));
            foreach (var rule in recipe.Rules.Where(r => r.Artifact.Required))
                Write(root, rule.Artifact.Selector, "source artifact");
            string[] presentations = ["charts/About.html", "charts/PDF/U0000.pdf", "charts/PDF/U1FA70.pdf"];
            foreach (string relative in presentations) Write(root, relative, "published chart presentation");

            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Empty(resolved.ExecutionErrors);
            foreach (string relative in presentations)
            {
                var artifact = Assert.Single(resolved.Graph.Artifacts, a => a.RelativePath == relative);
                Assert.Equal(IngestArtifactDisposition.ExcludedWithReason, artifact.Disposition);
                Assert.False(string.IsNullOrWhiteSpace(artifact.Notes));
                Assert.Equal(64, artifact.Sha256.Length);
                Assert.DoesNotContain(resolved.Bindings, b => b.Artifact.RelativePath == relative);
            }
            Assert.Contains(resolved.Bindings, b => b.Artifact.RelativePath == "charts/RSIndex.txt");

            Write(root, "charts/new-properties.txt", "unrecognized semantic data");
            resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Contains(resolved.ExecutionErrors, e => e.Contains("charts/new-properties.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("extracted")]
    public async Task Cili_selected_generation_admits_checkout_and_archive_layouts(string prefix)
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            CreateCiliFiles(root, prefix);
            foreach (string sidecar in new[] { ".landed", ".git/HEAD", ".archive/cili.tar.gz",
                         ".superseded-20261010/ili.ttl" })
                Write(root, sidecar, "acquisition metadata");
            var recipe = SourceGenerationRecipe.Load(RepositoryFile("recipes/cili/a895d7e/cili.source.json"));
            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Empty(resolved.ExecutionErrors);
            // The sense-key tables are pointer-to-pointer maps and are excluded: five bindings.
            Assert.Equal(5, resolved.Bindings.Count);
            Assert.DoesNotContain(resolved.Bindings, b => b.Artifact.RelativePath.Contains("sense-mappings", StringComparison.Ordinal));
            Assert.DoesNotContain(resolved.Bindings, b => b.Artifact.RelativePath.StartsWith('.'));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("recipes/verbnet/3.4/verbnet.source.json", "verbnet3.4/give-13.1.xml")]
    [InlineData("recipes/propbank/3.4/propbank.source.json", "frames/give.xml")]
    [InlineData("recipes/oewn/2025-plus/oewn.source.json", "english-wordnet-2025-plus.xml.gz")]
    [InlineData("recipes/omw/2.0/omw.source.json", "omw-2.0/omw-it/omw-it.xml")]
    [InlineData("recipes/omw/2.0/omw.source.json", "extracted/omw-2.0/omw-it/omw-it.xml")]
    [InlineData("recipes/ud/2.18/ud.source.json", "ud-treebanks-v2.18/UD_English-EWT/en_ewt-ud-train.conllu")]
    [InlineData("recipes/ud/2.18/ud.source.json", "extracted/ud-treebanks-v2.18/UD_English-EWT/en_ewt-ud-train.conllu")]
    public async Task Selected_generations_tolerate_git_and_archive_control_files(string manifest, string admittedFile)
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            Write(root, admittedFile, "source record");
            foreach (string sidecar in new[] { ".landed", ".archive/release.tar.xz", ".superseded-20261010/old.xml" })
                Write(root, sidecar, "acquisition metadata");
            if (manifest.Contains("verbnet.source.json", StringComparison.Ordinal)
                || manifest.Contains("propbank.source.json", StringComparison.Ordinal))
                Write(root, ".git/HEAD", "ref: refs/heads/main");

            var recipe = SourceGenerationRecipe.Load(RepositoryFile(manifest));
            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Empty(resolved.ExecutionErrors);
            Assert.Single(resolved.Bindings);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cili_rejects_two_release_copies_and_reports_unrecognized_content()
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            CreateCiliFiles(root, "");
            var recipe = SourceGenerationRecipe.Load(RepositoryFile("recipes/cili/a895d7e/cili.source.json"));
            Write(root, "new-claims.ttl", "unrecognized source content");
            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Contains(resolved.ExecutionErrors, e => e.Contains("new-claims.ttl"));
            CreateCiliFiles(root, "extracted");
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => SourceGenerationResolver.ResolveAsync(recipe, root));
            Assert.Contains("multiple declared artifact layouts", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Iso_acquisition_marker_does_not_invalidate_admitted_tables()
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            var recipe = SourceGenerationRecipe.Load(RepositoryFile("recipes/iso639/20260415/iso639.source.json"));
            foreach (var rule in recipe.Rules.Where(r => r.Artifact.Required))
                Write(root, rule.Artifact.Selector, "source table");
            Write(root, ".landed", "acquisition complete");
            var resolved = await SourceGenerationResolver.ResolveAsync(recipe, root);
            Assert.Empty(resolved.ExecutionErrors);
            Assert.Equal(4, resolved.Bindings.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(".landed")]
    [InlineData("ucd/.laplace-archive-sha256")]
    public async Task Acquisition_receipts_are_control_artifacts_without_hiding_unknown_files(string receipt)
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            Write(root, receipt, "acquisition metadata");
            Write(root, "unrecognized-source.txt", "source content");
            var graph = await new Unicode.UnicodeDecomposer().DescribeArtifactsAsync(root, DecomposerOptions.Default);
            Assert.NotNull(graph);
            Assert.Equal(IngestArtifactDisposition.ExcludedWithReason,
                Assert.Single(graph.Artifacts, a => a.RelativePath == receipt).Disposition);
            Assert.Equal(IngestArtifactDisposition.Unsupported,
                Assert.Single(graph.Artifacts, a => a.RelativePath == "unrecognized-source.txt").Disposition);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HostCompositionListsCanonicalNativeSourcesWithoutLoadingJsonGenerations()
    {
        var resolver = new Composition.SeedDecomposerResolver(new NullServiceProvider(), []);
        Assert.Equal(EtlManifest.SourceOrder, resolver.SelectedGenerationKeys);
        Assert.Contains("open-multilingual-wordnet", resolver.SelectedGenerationKeys);
        Assert.Contains("universal-dependencies", resolver.SelectedGenerationKeys);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static void CreateCiliFiles(string root, string prefix)
    {
        foreach (string relative in new[] { "ili.ttl", "ili-map-pwn30.tab", "ili-map-pwn31.tab",
                     "ili-map-odwn13.ttl", "older-wn-mappings/ili-map-pwn15.tab", "sense-mappings/pwn30.tab" })
            Write(root, Path.Combine(prefix, relative), "source record");
    }

    private static string TemporaryDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace-source-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
