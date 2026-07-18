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
        // never serialized ahead of the compose. source_id IS the file's content-DAG root, which
        // the compose already produces when it builds the tree; the name/metadata is a metadata DAG
        // fetched off it, not hashed in.
        bool rootIsFile = File.Exists(_root);
        foreach (string file in DocumentDecomposer.EnumerateInputFiles(_root))
        {
            ct.ThrowIfCancellationRequested();
            string f = file;
            string rel = rootIsFile
                ? Path.GetFileName(f)
                : Path.GetRelativePath(_root, f).Replace('\\', '/');
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                $"document/{rel}", token => OpenAsync(f, token));
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ContentIngestRecord> OpenAsync(
        string file, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[]? bytes = await ReadFileBytesAsync(file, ct);
        if (bytes is null || bytes.Length == 0) yield break;
        yield return new ContentIngestRecord(bytes);
    }

    private static async Task<byte[]?> ReadFileBytesAsync(string file, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0 || fi.Length > int.MaxValue) return null;
            var bytes = new byte[(int)fi.Length];
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);
            int off = 0;
            while (off < bytes.Length)
            {
                int n = await fs.ReadAsync(bytes.AsMemory(off), ct);
                if (n == 0) return null;
                off += n;
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class DocumentIngestHandler : IIngestRecordHandler<ContentIngestRecord>
{
    private readonly ContentIngestHandler _inner = new(UserPromptContent.Source);

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        ContentIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct) =>
        _inner.TryTrunkShortcircuitAsync(record, builder, reader, witnessWeight, ct);

    public IIngestDeferredUnit CreateDeferredUnit(ContentIngestRecord record) =>
        _inner.CreateDeferredUnit(record);

    public void WalkWitness(ContentIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        // Pillar 3a: a document emits its content DAG (entities + physicalities/trajectory) via
        // the deferred unit ONLY. No distributional attestations: sequence is the trajectory
        // geometry, containment is containers_of + the point-match, and PRECEDES is a MODEL
        // relation (token couplings from Q/K/V/O/gate/up/down), not text word-adjacency. This
        // call emitted the ~3.7M redundant document attestations behind the re-witness grind.
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
