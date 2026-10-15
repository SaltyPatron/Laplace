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
            Assert.Equal(6, resolved.Bindings.Count);
            Assert.DoesNotContain(resolved.Bindings, b => b.Artifact.RelativePath.StartsWith('.'));
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
    [InlineData(false, ".landed")]
    [InlineData(true, ".landed")]
    [InlineData(true, "ucd/.laplace-archive-sha256")]
    public async Task Acquisition_receipts_are_control_artifacts_without_hiding_unknown_files(bool unicode, string receipt)
    {
        CodepointPerfcache.LoadDefault();
        string root = TemporaryDirectory();
        try
        {
            Write(root, receipt, "acquisition metadata");
            Write(root, "unrecognized-source.txt", "source content");
            var graph = unicode
                ? await new Unicode.UnicodeDecomposer().DescribeArtifactsAsync(root, DecomposerOptions.Default)
                : await new ISO.ISODecomposer().DescribeArtifactsAsync(root, DecomposerOptions.Default);
            Assert.NotNull(graph);
            Assert.Equal(IngestArtifactDisposition.ExcludedWithReason,
                Assert.Single(graph.Artifacts, a => a.RelativePath == receipt).Disposition);
            Assert.Equal(IngestArtifactDisposition.Unsupported,
                Assert.Single(graph.Artifacts, a => a.RelativePath == "unrecognized-source.txt").Disposition);
        }
        finally { Directory.Delete(root, true); }
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
