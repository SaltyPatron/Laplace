using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

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
        Hash128 sourceId, string batchLabelPrefix, int batchSize, ISubstrateReader? reader,
        long maxInputUnits = 0)
    {
        var profile = IngestSourceProfile.Wiktionary;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile, defaultBatch: batchSize);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = Math.Clamp(ws.ProbeChunk, 64, 1024),
            ContainmentReader = reader,
            MaxInputUnits = maxInputUnits,
            EnableDeferredContentOnBuilder = true,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
        return maxInputUnits > 0 ? config.WithMaxInputUnits(maxInputUnits) : config;
    }
}

internal sealed class SemLinkJsonDocumentPhase : DecomposerPhase<GrammarIngestRecord>
{
    private readonly string _path;
    private readonly SemLinkDocumentKind _kind;
    private readonly string _label;

    public SemLinkJsonDocumentPhase(string path, SemLinkDocumentKind kind, string label)
    {
        _path = path;
        _kind = kind;
        _label = label;
    }

    protected override string PhaseLabel => _label;

    public override Hash128 SourceId => SemLinkDecomposer.Source;
    public override string SourceName => "SemLinkDecomposer";
    public override int LayerOrder => 3;
    public override Hash128 TrustClassId => SemLinkDecomposer.TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        Task.CompletedTask;

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SemLinkJsonPairStream.CountPairsAsync(_path, ct).ContinueWith(t => (long?)t.Result, ct);

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandler()
    {
        var witness = new SemLinkGrammarWitness(_kind);
        return new GrammarIngestHandler(SemLinkDecomposer.Source, witness.ModalityId, witness);
    }

    protected override IAsyncEnumerable<GrammarIngestRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        new SemLinkJsonPairStream(_path).RecordsAsync(ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        int batchSize = options.BatchSize > 0 ? options.BatchSize : 1;
        var config = SemLinkIngestSupport.PipelineConfig(
            SourceId, BatchLabelPrefix, batchSize, context.Reader, options.MaxInputUnits);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
