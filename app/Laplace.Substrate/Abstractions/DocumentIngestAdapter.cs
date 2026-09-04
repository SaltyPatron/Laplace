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
    private readonly ContentIngestHandler _inner = new(DocumentSource.SourceId);

    public DocumentIngestHandler(int layerOrder) => LayerOrder = layerOrder;

    public int LayerOrder { get; }

    public bool IgnoreCompletedFiles { get; init; }

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        _inner.CreateDeferredUnit(record);

    public void WalkWitness(
        ContentIngestRecord record,
        Hash128 root,
        SubstrateChangeBuilder builder,
        IIngestDeferredUnit unit)
    {
        Hash128 contentRoot = record.ContentRootId != default
            ? record.ContentRootId
            : root != default
                ? root
                : ContentTierSpine.ResolveRoot(record.CanonicalUtf8) ?? default;
        if (contentRoot == default) return;

        if (record.Metadata is not { } metadata
            || record.FileId == default
            || record.DocumentId == default)
        {
            // Compatibility for synthetic records that predate explicit file/document ids.
            Hash128 legacyFileRoot = record.SourceId != default ? record.SourceId : contentRoot;
            Laplace.Ingestion.LayerCompletion.EmitFileMarker(builder, legacyFileRoot, LayerOrder);
            if (record.Metadata is { } legacyMetadata)
                FileEntity.EmitMetadata(builder, legacyFileRoot, legacyMetadata);
            return;
        }

        FileIdentity file = FileEntity.Emit(
            builder,
            DocumentSource.SourceId,
            record.CanonicalUtf8,
            metadata);
        if (file.ContentRootId != contentRoot || file.FileId != record.FileId)
            throw new InvalidOperationException(
                "DocumentIngestHandler: extracted file identity changed between open and compose");

        Hash128 documentId = DocumentEntity.Emit(
            builder,
            file.FileId,
            contentRoot,
            record.CanonicalUtf8);
        if (documentId != record.DocumentId)
            throw new InvalidOperationException(
                "DocumentIngestHandler: extracted document identity changed between open and compose");

        Laplace.Ingestion.LayerCompletion.EmitFileMarker(
            builder,
            file.FileId,
            DocumentSource.SourceId,
            LayerOrder);
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
            WitnessWeight = RelationTypeRank.Associative * SourceTrust.StructuredCorpus,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
