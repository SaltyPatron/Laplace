using System.Runtime.CompilerServices;
using System.Text.Json;
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

        // Span discovery is Utf8JsonReader (one sequential pass) — not a full Grammar AST
        // of the document followed by a reparse of every pair.
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

    internal static async Task<long?> CountRecordsAsync(string path, CancellationToken ct)
    {
        byte[]? utf8 = await ReadFileBytesAsync(path, ct);
        if (utf8 is null || utf8.Length == 0) return null;
        try
        {
            return ReadTopLevelPairSpansUtf8(utf8).Count;
        }
        catch (JsonException)
        {
            IntPtr recipe = GrammarDecomposer.LookupById("json");
            return recipe == IntPtr.Zero ? null : ReadTopLevelPairSpans(utf8, recipe).Count;
        }
    }

    /// <summary>
    /// Top-level object property byte ranges. Prefer <see cref="Utf8JsonReader"/>;
    /// fall back to a single Grammar AST walk only when the JSON reader rejects the file.
    /// </summary>
    internal static List<(uint Start, uint End)> ReadTopLevelPairSpans(byte[] utf8, IntPtr recipe)
    {
        try
        {
            var viaReader = ReadTopLevelPairSpansUtf8(utf8);
            if (viaReader.Count > 0) return viaReader;
        }
        catch (JsonException)
        {
            // Fall through to Grammar AST.
        }

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

    private static List<(uint Start, uint End)> ReadTopLevelPairSpansUtf8(byte[] utf8)
    {
        var spans = new List<(uint, uint)>();
        var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return spans;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            long start = reader.TokenStartIndex;
            reader.Skip();
            long end = reader.BytesConsumed;
            if (end > start && end <= utf8.Length)
                spans.Add(((uint)start, (uint)end));
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
        Hash128 sourceId, string batchLabelPrefix, DecomposerOptions options,
        ISubstrateReader? reader)
    {
        var profile = IngestSourceProfile.Wiktionary;
        return IngestPipelineDefaults.Compose(
            sourceId,
            batchLabelPrefix,
            IngestPipelineDefaults.ResolveBatch(profile, options),
            options,
            reader,
            profile);
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
        SemLinkJsonPairStream.CountRecordsAsync(_path, ct);

    protected override IIngestRecordHandler<GrammarIngestRecord> CreateHandler()
    {
        var witness = new SemLinkGrammarWitness(_kind);
        return new GrammarWitnessIngestHandler(witness);
    }

    protected override IAsyncEnumerable<GrammarIngestRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        new SemLinkJsonPairStream(_path).RecordsAsync(ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
        => SemLinkIngestSupport.PipelineConfig(
            SourceId, BatchLabelPrefix, options, context.Reader);
}
