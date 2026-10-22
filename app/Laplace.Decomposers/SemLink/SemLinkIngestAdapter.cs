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
        ct.ThrowIfCancellationRequested();
        byte[] utf8 = await ReadFileBytesAsync(_path, ct);

        // Span discovery is one sequential Utf8JsonReader pass, not a full grammar AST of
        // the document plus a reparse of every pair.
        var pairSpans = ReadTopLevelPairSpansUtf8(utf8);
        if (pairSpans.Count == 0) yield break;
        IntPtr recipe = GrammarDecomposer.LookupById("json");
        if (recipe == IntPtr.Zero)
            throw new InvalidOperationException($"SemLink source '{_path}' requires the native JSON grammar.");

        int rowIndex = 0;
        long rowsTotal = pairSpans.Count;
        foreach (var (start, end) in pairSpans)
        {
            ct.ThrowIfCancellationRequested();
            byte[] subDoc = WrapSinglePair(utf8, start, end);
            var ast = GrammarDecomposer.Parse(subDoc, recipe);
            try
            {
                if (!ast.Diagnostics.SyntaxComplete)
                    throw new InvalidDataException($"SemLink native JSON parse recovered incomplete syntax at source '{_path}', record {rowIndex}.");
            }
            catch { ast.Dispose(); throw; }
            yield return new GrammarIngestRecord(subDoc, ast, rowIndex++, rowsTotal);
            await Task.Yield();
        }
    }

    internal static async Task<long?> CountRecordsAsync(string path, CancellationToken ct)
    {
        byte[] utf8 = await ReadFileBytesAsync(path, ct);
        return ReadTopLevelPairSpansUtf8(utf8).Count;
    }

    private static List<(uint Start, uint End)> ReadTopLevelPairSpansUtf8(byte[] utf8)
    {
        var spans = new List<(uint, uint)>();
        int preamble = utf8.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var reader = new Utf8JsonReader(utf8.AsSpan(preamble), isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("SemLink JSON source must contain one complete top-level object.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                // Reading through EOF also rejects trailing garbage/a second
                // document instead of admitting only a valid-looking prefix.
                if (reader.Read()) throw new JsonException("SemLink source contains more than one JSON value.");
                return spans;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            long start = preamble + reader.TokenStartIndex;
            reader.Skip();
            long end = preamble + reader.BytesConsumed;
            if (end > start && end <= utf8.Length)
                spans.Add(((uint)start, (uint)end));
        }
        throw new JsonException("SemLink JSON source ended before the top-level object closed.");
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

    private static async Task<byte[]> ReadFileBytesAsync(string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var fs = IngestIo.OpenSequentialRead(file, useAsync: true);
        long length = fs.Length;
        long limit = Math.Min(Array.MaxLength, IngestSizing.ResolveContiguousPayloadBytes());
        if (length == 0) throw new InvalidDataException($"SemLink JSON source '{file}' is empty.");
        if (length > limit)
            throw new InvalidDataException($"SemLink JSON source '{file}' has {length} bytes, exceeding the contiguous payload limit of {limit} bytes.");
        var bytes = new byte[checked((int)length)];
        await fs.ReadExactlyAsync(bytes, ct);
        if (fs.Length != length) throw new IOException($"SemLink JSON source '{file}' changed length while being read.");
        return bytes;
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
