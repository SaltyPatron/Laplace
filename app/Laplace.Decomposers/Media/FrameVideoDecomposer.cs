using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Thin video lane: ordered image-frame packaging under a directory. Spatial =
/// <see cref="ImageTierSpine"/> (codepoint floor); temporal edges in WalkWitness
/// (HAS_FRAME / PRECEDES_IN_TIME). Video root = blake3 over ordered frame ladder
/// roots (path-independent) — not blake3 of frame RGBA buffers.
/// </summary>
public sealed class FrameVideoDecomposer
    : Decomposer<VideoFrameIngestRecord, FrameVideoSource, FullScope>, IIngestInventoryProvider
{
    private Hash128 _videoRootId;
    private VideoFrameIngestHandler? _handler;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        _videoRootId = await ComputeContentAddressedVideoRootAsync(context.EcosystemPath, ct)
            .ConfigureAwait(false);
        _handler = new VideoFrameIngestHandler(SourceId, LayerOrder, _videoRootId);
    }

    protected override IIngestRecordHandler<VideoFrameIngestRecord> CreateHandler() =>
        _handler ?? new VideoFrameIngestHandler(SourceId, LayerOrder, _videoRootId);

    protected override async IAsyncEnumerable<VideoFrameIngestRecord> ExtractRecordsAsync(
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
            yield return new VideoFrameIngestRecord(
                rgba, w, h, frameIndex++, root.Value, FileMetadata.FromPath(filePath, rel));
        }
    }

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        int batchSize = BatchConfigDefaults.Resolve(options, 8);
        return MediaIngestSupport.PipelineConfig(
            SourceId, TC.StructuredCorpus, "frame-video", context.Reader,
            IngestSourceProfile.MediaVideo, batchSize);
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
    {
        // Domain tag + packed frame roots.
        byte[] tag = "substrate/video/v1/frames"u8.ToArray();
        var buf = new byte[tag.Length + orderedFrameRoots.Count * 16];
        tag.CopyTo(buf, 0);
        for (int i = 0; i < orderedFrameRoots.Count; i++)
        {
            var r = orderedFrameRoots[i];
            // Hash128 layout: write via BitConverter-style lo/hi if exposed; else Blake3 of ToString is wrong.
            // Use the public byte encode if any — fall back to unsafe-free path via Hash128 fields.
            WriteHash128(buf.AsSpan(tag.Length + i * 16), r);
        }
        return Hash128.Blake3(buf);
    }

    private static void WriteHash128(Span<byte> dst, Hash128 h)
    {
        // Hash128 is a readonly struct with Hi/Lo ulong in Laplace.Engine.Core.
        BitConverter.TryWriteBytes(dst, h.Hi);
        BitConverter.TryWriteBytes(dst[8..], h.Lo);
    }

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
