using System.Diagnostics;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Media;

/// <summary>
/// Thin multi-file audio lane: packaging → PCM recovery → <see cref="AudioIngestRecord"/>;
/// <see cref="AudioTierSpine"/> owns codepoint-floor compose/emit. Not named
/// AudioDecomposer (stub path banned). Not corpus-specific / not one container.
/// </summary>
public sealed class TrackAudioDecomposer
    : DecomposerMultiFile<AudioIngestRecord, TrackAudioSource, FullScope>, IIngestInventoryProvider
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
            return (f, $"track-audio/{rel}");
        }).ToList();
    }

    protected override async IAsyncEnumerable<AudioIngestRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string rel = fileLabel.StartsWith("track-audio/", StringComparison.Ordinal)
            ? fileLabel["track-audio/".Length..]
            : Path.GetFileName(filePath);
        var opened = await AudioFileOpen.OpenAsync(filePath, ct).ConfigureAwait(false);
        if (opened is null) yield break;
        var (rate, samples) = opened.Value;
        Hash128? root = AudioTierSpine.ResolveRoot(samples);
        if (root is null)
        {
            Trace.TraceWarning("TrackAudioDecomposer: skipping '{0}' — unresolvable root", rel);
            yield break;
        }
        yield return new AudioIngestRecord(
            samples, rate, root.Value, FileMetadata.FromPath(filePath, rel));
    }

    protected override IIngestRecordHandler<AudioIngestRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new AudioIngestHandler(SourceId, LayerOrder) { IgnoreCompletedFiles = options.ReObservePresent };

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        int batchSize = BatchConfigDefaults.Resolve(options, 8);
        return MediaIngestSupport.PipelineConfig(
            SourceId, TC.StructuredCorpus, fileLabel, reader,
            IngestSourceProfile.MediaAudio, batchSize);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        var specs = paths.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("track-audio", paths.Count, specs, TracksFileCompletion: true));
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
            if (AudioFileOpen.IsSupportedPath(path))
                yield return Path.GetFullPath(path);
            yield break;
        }
        if (!Directory.Exists(path)) yield break;
        foreach (string file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                         .Where(f => AudioFileOpen.IsSupportedPath(f)
                                                     && !VendoredPathFilter.IsVendoredOrBuildPath(f))
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
