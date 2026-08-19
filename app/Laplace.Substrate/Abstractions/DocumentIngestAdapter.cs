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
    private readonly ContentIngestHandler _inner = new(UserPromptContent.Source);

    public DocumentIngestHandler(int layerOrder) => LayerOrder = layerOrder;

    /// <summary>Layer the per-file completion marker is minted/checked at — the
    /// decomposer's layer, threaded in so the gate and the marker can never disagree.</summary>
    public int LayerOrder { get; }

    /// <summary>--force (ReObservePresent): bypass the per-file completion-marker skip in
    /// the existence gate and re-observe already-completed files.</summary>
    public bool IgnoreCompletedFiles { get; init; }

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        _inner.CreateDeferredUnit(record);

    public void WalkWitness(ContentIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        // Pillar 3a: a document emits its content DAG (entities + physicalities/trajectory) via
        // the deferred unit ONLY. No per-node distributional attestations: sequence is the
        // trajectory geometry, containment is containers_of + the point-match. The file's
        // WITNESS is trunk-grain — the per-file completion marker plus the metadata DAG,
        // deposited once per file, novel path only (the present path already skipped a
        // marker-complete file in IngestExistenceGate; recomposes that reach here without a
        // marker still deposit it, which is the "content known from another source" case).
        if (unit is PresentRootDeferredUnit) return;

        // DrainInto legitimately returns default when the existence bitmap covered every
        // node (content fully present, only the marker/metadata are novel) — the file root
        // is the record's own per-file source id, not the drain result.
        Hash128 fileRoot = record.SourceId != default
            ? record.SourceId
            : root != default
                ? root
                : ContentTierSpine.ResolveRoot(record.CanonicalUtf8) ?? default;
        if (fileRoot == default) return;

        Laplace.Ingestion.LayerCompletion.EmitFileMarker(builder, fileRoot, LayerOrder);
        if (record.Metadata is { } metadata)
            FileEntity.EmitMetadata(builder, fileRoot, metadata);
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
            SourceId = UserPromptContent.Source,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            WitnessWeight = UserPromptContent.WitnessWeight,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
