using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Streams top-level JSON object pairs as grammar rows — one structural parse for span discovery,
/// then per-pair compose/probe via <see cref="IngestBatchPipeline"/>.
/// </summary>
public sealed class SemLinkJsonPairStream : IRecordStream<GrammarIngestRecord>
{
    private readonly string _path;

    public SemLinkJsonPairStream(string path) => _path = path;

    public async IAsyncEnumerable<GrammarIngestRecord> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById("json");
        if (recipe == IntPtr.Zero) yield break;

        byte[]? utf8 = await ReadFileBytesAsync(_path, ct);
        if (utf8 is null || utf8.Length == 0) yield break;

        var pairSpans = ReadTopLevelPairSpans(utf8, recipe);
        if (pairSpans.Count == 0) yield break;

        int rowIndex = 0;
        long rowsTotal = pairSpans.Count;
        foreach (var (start, end) in pairSpans)
        {
            ct.ThrowIfCancellationRequested();
            byte[] subDoc = WrapSinglePair(utf8, start, end);
            var ast = GrammarDecomposer.Parse(subDoc, recipe);
            yield return new GrammarIngestRecord(subDoc, ast, rowIndex++, rowsTotal);
            await Task.Yield();
        }
    }

    public static async Task<int> CountPairsAsync(string path, CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById("json");
        if (recipe == IntPtr.Zero) return 0;
        byte[]? utf8 = await ReadFileBytesAsync(path, ct);
        if (utf8 is null || utf8.Length == 0) return 0;
        return ReadTopLevelPairSpans(utf8, recipe).Count;
    }

    internal static List<(uint Start, uint End)> ReadTopLevelPairSpans(byte[] utf8, IntPtr recipe)
    {
        var spans = new List<(uint, uint)>();
        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ast);
        if (rootObj < 0) return spans;

        for (int i = 0; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.Parent != (uint)rootObj) continue;
            if (ast.NodeTypeName(node.NodeTypeId) != "pair") continue;
            if (node.EndByte <= node.StartByte || node.EndByte > utf8.Length) continue;
            spans.Add((node.StartByte, node.EndByte));
        }
        return spans;
    }

    internal static byte[] WrapSinglePair(byte[] utf8, uint start, uint end)
    {
        int len = (int)(end - start);
        var buf = new byte[len + 2];
        buf[0] = (byte)'{';
        Array.Copy(utf8, (int)start, buf, 1, len);
        buf[len + 1] = (byte)'}';
        return buf;
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

public static class SemLinkIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, string batchLabelPrefix, int batchSize, ISubstrateReader? reader) =>
        new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = Math.Max(1, batchSize),
            ProbeChunkSize = Math.Clamp(batchSize, 64, 1024),
            ContainmentReader = reader,
            EnableDeferredContentOnBuilder = false,
        };

    public static async IAsyncEnumerable<SubstrateChange> IngestJsonDocumentAsync(
        string path,
        SemLinkDocumentKind kind,
        string batchLabelPrefix,
        int batchSize,
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var witness = new SemLinkGrammarWitness(kind);
        var stream = new SemLinkJsonPairStream(path);
        var handler = new GrammarIngestHandler(SemLinkDecomposer.Source, witness.ModalityId, witness);
        var config = PipelineConfig(SemLinkDecomposer.Source, batchLabelPrefix, batchSize, containmentReader);
        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }
}
