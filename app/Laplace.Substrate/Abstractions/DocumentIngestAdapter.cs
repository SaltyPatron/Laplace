using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Thin multi-file wrapper over <see cref="DocumentFileExtract"/> — same unit
/// <see cref="DocumentDecomposer.ExtractFileAsync"/> uses. Kept for tests that
/// construct the stream directly.
/// </summary>
public sealed class DocumentMultiFileStream : IMultiFileRecordStream<ContentIngestRecord>
{
    private readonly string _root;

    public DocumentMultiFileStream(string root) => _root = root;

    public async IAsyncEnumerable<IFileRecordSource<ContentIngestRecord>> FilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool rootIsFile = File.Exists(_root);
        foreach (string file in DocumentDecomposer.EnumerateInputFiles(_root))
        {
            ct.ThrowIfCancellationRequested();
            string f = file;
            string rel = rootIsFile
                ? Path.GetFileName(f)
                : Path.GetRelativePath(_root, f).Replace('\\', '/');
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                $"document/{rel}", token => DocumentFileExtract.OpenAsync(f, rel, token));
        }
        await Task.CompletedTask;
    }
}

public sealed class DocumentIngestHandler : IIngestRecordHandler<ContentIngestRecord>
{
    private readonly Hash128 _decomposerSourceId;
    private readonly ContentIngestHandler _inner;

    public DocumentIngestHandler(int layerOrder, Hash128 decomposerSourceId)
    {
        LayerOrder = layerOrder;
        _decomposerSourceId = decomposerSourceId;
        _inner = new ContentIngestHandler(decomposerSourceId);
    }

    /// <summary>Layer the per-file completion marker is minted/checked at.</summary>
    public int LayerOrder { get; }

    /// <summary>
    /// Bypass the legacy content-root-only completion check inside the existence gate.
    /// The multi-file scheduler already performs the authoritative vendor-scoped check.
    /// </summary>
    public bool IgnoreCompletedFiles { get; init; }

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        _inner.CreateDeferredUnit(record);

    public void WalkWitness(
        ContentIngestRecord record,
        Hash128 root,
        SubstrateChangeBuilder builder,
        IIngestDeferredUnit unit)
    {
        if (unit is PresentRootDeferredUnit) return;

        Hash128 contentRoot = record.SourceId != default
            ? record.SourceId
            : root != default
                ? root
                : ContentTierSpine.ResolveRoot(record.CanonicalUtf8) ?? default;
        if (contentRoot == default) return;

        // The checkpoint is a fact about (this file content, this decomposer, this layer),
        // not a global fact that any consumer of the same bytes may inherit.
        Laplace.Ingestion.LayerCompletion.EmitFileMarker(
            builder, contentRoot, _decomposerSourceId, LayerOrder);

        if (record.Metadata is { } metadata)
            FileEntity.EmitMetadata(builder, contentRoot, metadata);
    }
}

public static class DocumentIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        string batchLabelPrefix, ISubstrateReader? reader, DecomposerOptions? options = null)
    {
        var profile = IngestSourceProfile.Document;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, options);
        return new()
        {
            SourceId = DocumentSource.SourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            WitnessWeight = DocumentSource.WitnessWeight,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
