using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class DocumentMultiFileStream : IMultiFileRecordStream<ContentIngestRecord>
{
    private readonly string _root;

    public DocumentMultiFileStream(string root) => _root = root;

    public async IAsyncEnumerable<(string FileLabel, ContentIngestRecord Record)> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (string file in DocumentDecomposer.EnumerateInputFiles(_root))
        {
            ct.ThrowIfCancellationRequested();

            byte[]? bytes = await ReadFileBytesAsync(file, ct);
            if (bytes is null || bytes.Length == 0) continue;

            string rel = File.Exists(_root)
                ? Path.GetFileName(file)
                : Path.GetRelativePath(_root, file).Replace('\\', '/');
            string label = $"document/{rel}";

            // Pillar 0: source_id IS the file-entity — this file's content DAG composed with its
            // metadata DAG (FileEntity.SourceId) — not the decomposer's static "UserPrompt" label.
            // Re-ingesting the same file collides on this hash and no-ops; the same content in a
            // different file is a distinct witness (corroboration).
            var meta = FileMetadata.FromPath(file, rel);
            Hash128 fileSource = FileEntity.SourceId(bytes, in meta);

            yield return (label, new ContentIngestRecord(bytes, SourceId: fileSource));
            await Task.Yield();
        }
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
