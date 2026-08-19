using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Thin multi-file image lane: packaging → RGBA recovery → <see cref="ImageIngestRecord"/>;
/// <see cref="ImageTierSpine"/> owns codepoint-floor compose/emit. Not named
/// ImageDecomposer (stub path banned). Not corpus-specific.
/// </summary>
public sealed class RgbaImageDecomposer
    : DecomposerMultiFile<ImageIngestRecord, RgbaImageSource, FullScope>, IIngestInventoryProvider
{
    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    public override bool PerFileCompletion => true;

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        bool rootIsFile = File.Exists(ecosystemPath);
        return EnumerateInputFiles(ecosystemPath).Select(f =>
        {
            string rel = rootIsFile
                ? Path.GetFileName(f)
                : Path.GetRelativePath(ecosystemPath, f).Replace('\\', '/');
            return (f, $"rgba-image/{rel}");
        }).ToList();
    }

    protected override async IAsyncEnumerable<ImageIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string rel = fileLabel.StartsWith("rgba-image/", StringComparison.Ordinal)
            ? fileLabel["rgba-image/".Length..]
            : Path.GetFileName(filePath);
        var opened = await ImageFileOpen.OpenAsync(filePath, ct).ConfigureAwait(false);
        if (opened is null) yield break;
        var (w, h, rgba) = opened.Value;
        Hash128? root = ImageTierSpine.ResolveRoot(rgba, w, h);
        if (root is null)
        {
            Trace.TraceWarning("RgbaImageDecomposer: skipping '{0}' — unresolvable root", rel);
            yield break;
        }
        yield return new ImageIngestRecord(
            rgba, w, h, root.Value, FileMetadata.FromPath(filePath, rel));
    }

    protected override IIngestRecordHandler<ImageIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new ImageIngestHandler(SourceId, LayerOrder) { IgnoreCompletedFiles = options.ReObservePresent };

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        return MediaIngestSupport.PipelineConfig(
            SourceId, TC.StructuredCorpus, fileLabel, reader,
            IngestSourceProfile.MediaImage, options);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        var specs = paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("rgba-images", paths.Count, specs, TracksFileCompletion: true));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EnumerateInputFiles(context.EcosystemPath).LongCount();
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    internal static IEnumerable<string> EnumerateInputFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (File.Exists(path))
        {
            if (ImageFileOpen.IsSupportedPath(path))
                yield return Path.GetFullPath(path);
            yield break;
        }
        if (!Directory.Exists(path)) yield break;
        foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                         .Where(f => ImageFileOpen.IsSupportedPath(f)
                                                     && !VendoredPathFilter.IsVendoredOrBuildPath(f))
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
