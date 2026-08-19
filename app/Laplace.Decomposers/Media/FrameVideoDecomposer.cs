using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Thin video lane: ordered image-frame packaging under a directory. Spatial =
/// <see cref="ImageTierSpine"/> (codepoint floor). The video is one ordered
/// trajectory over frame roots; frame membership and adjacency are recovered from
/// that trajectory rather than deposited as testimony. Video root = blake3 over
/// ordered frame ladder roots (path-independent), not blake3 of frame RGBA buffers.
/// </summary>
public sealed class FrameVideoDecomposer
    : Decomposer<VideoIngestRecord, FrameVideoSource, FullScope>, IIngestInventoryProvider
{
    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;
    protected override bool CanSegmentMonolith => false;

    protected override IIngestRecordHandler<VideoIngestRecord> CreateHandler() =>
        new VideoFrameIngestHandler(SourceId, LayerOrder);

    protected override async IAsyncEnumerable<VideoIngestRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int frameIndex = 0;
        foreach (string filePath in EnumerateInputFiles(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(ecosystemPath, filePath).Replace('\\', '/');
            var opened = await ImageFileOpen.OpenAsync(filePath, ct).ConfigureAwait(false);
            if (opened is null) continue;
            var (w, h, rgba) = opened.Value;
            Hash128? root = ImageTierSpine.ResolveRoot(rgba, w, h);
            if (root is null)
            {
                Trace.TraceWarning("FrameVideoDecomposer: skipping '{0}' — unresolvable root", rel);
                continue;
            }
            yield return new VideoIngestRecord.Frame(new VideoFrameIngestRecord(
                rgba, w, h, frameIndex++, root.Value, FileMetadata.FromPath(filePath, rel)));
        }
        if (frameIndex > 0)
            yield return new VideoIngestRecord.SequenceEnd();
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        return MediaIngestSupport.PipelineConfig(
            SourceId, TC.StructuredCorpus, "frame-video", context.Reader,
            IngestSourceProfile.MediaVideo, options);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        var specs = paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("frame-video", paths.Count, specs, TracksFileCompletion: false));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EnumerateInputFiles(context.EcosystemPath).LongCount();
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    /// <summary>
    /// blake3( domain || frameRoot0 || frameRoot1 || … ) — same frames same video id
    /// regardless of directory path.
    /// </summary>
    internal static async Task<Hash128> ComputeContentAddressedVideoRootAsync(
        string ecosystemPath, CancellationToken ct)
    {
        var roots = new List<Hash128>();
        foreach (string filePath in EnumerateInputFiles(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            var opened = await ImageFileOpen.OpenAsync(filePath, ct).ConfigureAwait(false);
            if (opened is null) continue;
            var (w, h, rgba) = opened.Value;
            if (ImageTierSpine.ResolveRoot(rgba, w, h) is { } root)
                roots.Add(root);
        }
        return HashVideoRoot(roots);
    }

    internal static Hash128 HashVideoRoot(IReadOnlyList<Hash128> orderedFrameRoots)
        => VideoFrameIngestHandler.HashVideoRoot(orderedFrameRoots);

    internal static IEnumerable<string> EnumerateInputFiles(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) yield break;
        foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                         .Where(f => ImageFileOpen.IsSupportedPath(f)
                                                     && !VendoredPathFilter.IsVendoredOrBuildPath(f))
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
