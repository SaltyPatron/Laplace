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

            string label = File.Exists(_root)
                ? $"document/{Path.GetFileName(file)}"
                : $"document/{Path.GetRelativePath(_root, file).Replace('\\', '/')}";

            yield return (label, new ContentIngestRecord(bytes));
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
        using var tree = ContentTierSpine.BuildTree(record.CanonicalUtf8);
        if (tree is null) return;
        foreach (var att in TextEntityBuilder.BuildDistributionalAttestations(
                     tree, UserPromptContent.Source, UserPromptContent.WitnessWeight))
            builder.AddAttestation(att);
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
