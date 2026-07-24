using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentMultiFileStream : IMultiFileRecordStream<ContentIngestRecord>
{
    private readonly string _root;

    public DocumentMultiFileStream(string root) => _root = root;

    public async IAsyncEnumerable<IFileRecordSource<ContentIngestRecord>> FilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cheap enumeration: yield a source per file with a label derived from the PATH only —
        // no read here. Each worker opens (reads bytes) its own file, so the read is parallel and
        // never serialized ahead of the compose. source_id IS the file's content-DAG root
        // (FileEntity.SourceId), stamped onto the record below; name/size/mtime ride as a
        // metadata DAG fetched off the trunk, never hashed into identity.
        bool rootIsFile = File.Exists(_root);
        foreach (string file in DocumentDecomposer.EnumerateInputFiles(_root))
        {
            ct.ThrowIfCancellationRequested();
            string f = file;
            string rel = rootIsFile
                ? Path.GetFileName(f)
                : Path.GetRelativePath(_root, f).Replace('\\', '/');
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                $"document/{rel}", token => OpenAsync(f, rel, token));
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ContentIngestRecord> OpenAsync(
        string file, string relativePath, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] bytes = await ReadFileBytesAsync(file, ct);
        if (bytes.Length == 0) yield break;
        Hash128 fileRoot = ContentTierSpine.ResolveRoot(bytes)
            ?? throw new InvalidOperationException(
                $"document '{relativePath}': content has no resolvable root");
        yield return new ContentIngestRecord(
            bytes, SourceId: fileRoot, Metadata: FileMetadata.FromPath(file, relativePath));
    }

    // Read failures are FAILURES: they surface as per-file ingest failures (the
    // multi-file driver's isolation lane), never a silent skip. Only a genuinely
    // empty file is a non-event.
    private static async Task<byte[]> ReadFileBytesAsync(string file, CancellationToken ct)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists)
            throw new FileNotFoundException($"document vanished between enumeration and open: {file}");
        if (fi.Length == 0) return Array.Empty<byte>();
        if (fi.Length > int.MaxValue)
            throw new InvalidOperationException(
                $"document '{file}' is {fi.Length:N0} bytes — exceeds the 2 GiB single-document "
                + "compose limit; split the file into documents below the limit");
        var bytes = new byte[(int)fi.Length];
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        int off = 0;
        while (off < bytes.Length)
        {
            int n = await fs.ReadAsync(bytes.AsMemory(off), ct);
            if (n == 0)
                throw new IOException(
                    $"document '{file}' truncated mid-read at {off:N0}/{bytes.Length:N0} bytes");
            off += n;
        }
        return bytes;
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

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        ContentIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        _inner.TryTrunkShortcircuitAsync(record, builder, reader, witnessWeight, ct);

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
        string batchLabelPrefix, ISubstrateReader? reader, int batchSize = 32)
    {
        var profile = IngestSourceProfile.Document;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSize);
        return new()
        {
            SourceId = UserPromptContent.Source,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = Math.Clamp(ws.Batch, 1, 256),
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 16, 256),
            WitnessWeight = UserPromptContent.WitnessWeight,
            ContainmentReader = reader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }
}
