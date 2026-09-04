using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Laplace.Ingestion.Tests;

public sealed class AmbientManifestIsolationTests
{
    [Fact]
    public async Task DirectContent_IgnoresAmbientManifestAndUsesSelectedPathInventory()
    {
        string root = CreateEstate();
        try
        {
            var result = await RunAsync(new DirectInputDecomposer(), root, requireManifest: false);

            Assert.Equal(2, result.UnitsApplied);
            Assert.Equal(2, result.InputUnitsDone);
            Assert.Equal(2, result.InputUnitsTotal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorpusSource_StillUsesAmbientManifestSelection()
    {
        string root = CreateEstate();
        try
        {
            var result = await RunAsync(new ManifestAwareDecomposer(), root, requireManifest: false);

            Assert.Equal(1, result.UnitsApplied);
            Assert.Equal(1, result.InputUnitsDone);
            Assert.Equal(1, result.InputUnitsTotal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectContent_ExplicitManifestRequirementStillWins()
    {
        string root = CreateEstate();
        try
        {
            var result = await RunAsync(new DirectInputDecomposer(), root, requireManifest: true);

            Assert.Equal(1, result.UnitsApplied);
            Assert.Equal(1, result.InputUnitsDone);
            Assert.Equal(1, result.InputUnitsTotal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<IngestRunResult> RunAsync(
        IDecomposer decomposer, string root, bool requireManifest)
    {
        var runner = new IngestRunner(
            new InsertAllWriter(), new EmptyReader(), NullLoggerFactory.Instance);
        return runner.RunAsync(
            decomposer,
            IngestRunOptions.Default with
            {
                EcosystemPath = root,
                SkipLayerOrderingCheck = true,
                SkipSourceCompletion = true,
                RequireArtifactManifest = requireManifest,
            });
    }

    private static string CreateEstate()
    {
        string root = Path.Combine(Path.GetTempPath(), $"laplace-ambient-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "selected document");
        File.WriteAllText(Path.Combine(root, "b.txt"), "direct document outside selected dataset graph");

        string header = string.Join('\t',
        [
            "source", "release", "artifact", "relative_path", "disposition", "upstream_url",
            "fetched_at_utc", "bytes", "sha256", "upstream_checksum", "media_type", "license",
            "citation", "language", "split", "annotation_origin", "notes",
        ]);
        string admitted = string.Join('\t',
        [
            "fixture", "v1", "a", "a.txt", "admitted", "", "", "", "", "",
            "text/plain", "test", "", "", "", "", "selected fixture",
        ]);
        string excluded = string.Join('\t',
        [
            "fixture", "v1", "b", "b.txt", "excluded-with-reason", "", "", "", "", "",
            "text/plain", "test", "", "", "", "", "not part of the curated selection",
        ]);
        File.WriteAllText(Path.Combine(root, "MANIFEST.tsv"), $"{header}\n{admitted}\n{excluded}\n");
        return root;
    }

    private class ManifestAwareDecomposer : IDecomposer, IIngestInventoryProvider
    {
        private static readonly Hash128 Source = Hash128.OfCanonical("test/ambient-manifest/source");

        public Hash128 SourceId => Source;
        public string SourceName => "AmbientManifestTest";
        public int LayerOrder => 0;
        public Hash128 TrustClassId => SubstrateCanonicalIds.TrustClass("StructuredCorpus");

        public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (string path in ScheduledPaths(context))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                Hash128 id = Hash128.OfCanonical($"test/ambient-manifest/{name}");
                yield return new SubstrateChangeBuilder(Source, $"ambient/{name}")
                    .AddEntity(id, EntityTier.Word, EntityTypeRegistry.SourceReference, Source)
                    .SetInputUnitsConsumed(1)
                    .Build();
            }
            await Task.CompletedTask;
        }

        public Task<IngestInventory?> DescribeInputAsync(
            IDecomposerContext context,
            DecomposerOptions options,
            CancellationToken ct = default)
        {
            var paths = ScheduledPaths(context);
            var files = paths
                .Select(static path => new IngestFileSpec(Path.GetFileName(path), path, 1))
                .ToArray();
            return Task.FromResult<IngestInventory?>(
                new IngestInventory("files", paths.Count, files, TracksFileCompletion: false));
        }

        public Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default) =>
            Task.FromResult<long?>(ScheduledPaths(context).Count);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static IReadOnlyList<string> ScheduledPaths(IDecomposerContext context) =>
            context.SelectedArtifacts.Count > 0
                ? context.SelectedArtifacts.Select(static artifact => artifact.Path).ToArray()
                : Directory.EnumerateFiles(context.EcosystemPath, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToArray();
    }

    private sealed class DirectInputDecomposer : ManifestAwareDecomposer, IIgnoresAmbientArtifactManifest
    {
    }

    private sealed class InsertAllWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            int entities = change.Entities.Length;
            int physicalities = change.Physicalities.Length;
            int attestations = change.Attestations.Length;
            return Task.FromResult(new ApplyResult(
                entities, entities,
                physicalities, physicalities,
                attestations, attestations,
                RoundTrips: 1,
                WallClock: TimeSpan.Zero,
                TrunkShortcircuitHit: false));
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
