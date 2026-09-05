using System.IO;
using System.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

// Pins GH #608: DocumentDecomposer must share the ONE VendoredPathFilter
// (Laplace.Core) that Code/RepoDecomposer use, so vendored/junk .txt inside an
// ecosystem tree never gets enumerated for document ingest under the corpus's
// identity. Before the fix, EnumerateInputFiles was a bare EnumerateFiles with
// no filter.
[Collection("GrammarPerfcache")]
public sealed class DocumentEnumerationTests
{
    [Fact]
    public async Task MixedDirectory_OneArtifactSnapshotAccountsForEveryFileAndSchedulesOnlySupportedText()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace1403_" + Path.GetRandomFileName());
        try
        {
            Write(root, "doc.txt", "authored prose");
            Write(root, Path.Combine("sub", "chapter.txt"), "more authored prose");
            Write(root, "image.bin", "not a supported document format");
            Write(root, Path.Combine("external", "vendor.txt"), "vendored prose");

            var observer = new RecordingObservability();
            var runner = new IngestRunner(
                new InsertAllWriter(), new EmptyReader(), NullLoggerFactory.Instance, observer);
            IngestRunResult result = await runner.RunAsync(
                new DocumentDecomposer(),
                IngestRunOptions.Default with
                {
                    EcosystemPath = root,
                    SkipLayerOrderingCheck = true,
                    SkipSourceCompletion = true,
                });

            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(observer.ArtifactGraph);
            Assert.Equal(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .OrderBy(static path => path, StringComparer.Ordinal),
                graph.Artifacts.Select(static artifact => artifact.Path)
                    .OrderBy(static path => path, StringComparer.Ordinal));
            Assert.Equal(2, graph.Artifacts.Count(static artifact => artifact.IsSelected));
            Assert.Single(graph.Artifacts, static artifact =>
                artifact.Disposition == IngestArtifactDisposition.Unsupported);
            Assert.Single(graph.Artifacts, static artifact =>
                artifact.Disposition == IngestArtifactDisposition.ExcludedWithReason);
            Assert.All(
                graph.Artifacts.Where(static artifact => !artifact.IsSelected),
                static artifact => Assert.False(string.IsNullOrWhiteSpace(artifact.Notes)));
            Assert.Equal(
                ["document/doc.txt", "document/sub/chapter.txt"],
                observer.Started.OrderBy(static label => label, StringComparer.Ordinal));
            Assert.Equal(2, result.FilesDone);
            Assert.Equal(2, result.InputUnitsDone);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedOnlyInput_RemainsInventoriedWithoutFallingBackIntoExecution()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace1403u_" + Path.GetRandomFileName());
        try
        {
            Write(root, "payload.bin", "unsupported payload");
            var observer = new RecordingObservability();
            var runner = new IngestRunner(
                new InsertAllWriter(), new EmptyReader(), NullLoggerFactory.Instance, observer);

            IngestRunResult result = await runner.RunAsync(
                new DocumentDecomposer(),
                IngestRunOptions.Default with
                {
                    EcosystemPath = root,
                    SkipLayerOrderingCheck = true,
                    SkipSourceCompletion = true,
                });

            IngestArtifactGraph graph = Assert.IsType<IngestArtifactGraph>(observer.ArtifactGraph);
            IngestArtifact artifact = Assert.Single(graph.Artifacts);
            Assert.Equal(IngestArtifactDisposition.Unsupported, artifact.Disposition);
            Assert.Empty(graph.Selected);
            Assert.Empty(observer.Started);
            Assert.Equal(0, result.UnitsApplied);
            Assert.Equal(0, result.FilesDone);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateInputFiles_ExcludesVendoredAndBuildTrees_KeepsAuthoredText()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace608_" + Path.GetRandomFileName());
        try
        {
            // authored content — kept
            Write(root, "doc.txt", "authored prose");
            Write(root, Path.Combine("sub", "chapter.txt"), "more authored prose");
            // vendored / build trees — dropped (segment match)
            Write(root, Path.Combine("external", "core-isl.txt"), "OMW vendored wordlist");
            Write(root, Path.Combine("node_modules", "pkg", "readme.txt"), "third party");
            Write(root, Path.Combine(".venv", "lib", "notes.txt"), "python venv");
            Write(root, Path.Combine("obj", "generated.txt"), "build artifact");

            var got = DocumentDecomposer.EnumerateInputFiles(root)
                .Select(p => Path.GetFileName(p))
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(new[] { "chapter.txt", "doc.txt" }, got);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateInputFiles_SingleFile_PassesThrough()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace608f_" + Path.GetRandomFileName());
        try
        {
            string file = Write(dir, "only.txt", "one file");
            var got = DocumentDecomposer.EnumerateInputFiles(file).ToArray();
            Assert.Single(got);
            Assert.Equal(Path.GetFullPath(file), got[0]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static string Write(string root, string rel, string content)
    {
        string full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private sealed class RecordingObservability : IIngestObservability
    {
        private readonly object _gate = new();
        public IngestArtifactGraph? ArtifactGraph { get; private set; }
        public List<string> Started { get; } = [];

        public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory) { }

        public void OnRunStart(
            string sourceName,
            int layerOrder,
            IngestInventory? inventory,
            IngestArtifactGraph? artifactGraph) => ArtifactGraph = artifactGraph;

        public void OnIntentApplied(string sourceName, ApplyResult result) { }
        public void OnIntentFailed(string sourceName, IngestFailure failure) { }
        public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error) { }

        public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0)
        {
            lock (_gate) Started.Add(fileLabel);
        }
    }

    private sealed class InsertAllWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            int entities = change.Entities.Length;
            int physicalities = change.Physicalities.Length;
            int attestations = change.Attestations.Length;
            return Task.FromResult(new ApplyResult(
                entities, entities, physicalities, physicalities, attestations, attestations,
                RoundTrips: 1, WallClock: TimeSpan.Zero, TrunkShortcircuitHit: false));
        }
    }

    private sealed class EmptyReader : ISubstrateReader
    {
        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(
            Hash128 sourceId, int layerOrder, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default) =>
            Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }
}
