using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.CILI;

public sealed class CILIDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/CILIDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;
    private static readonly Hash128 EngLang = LanguageEntityId.FromIso639_3("eng");

    private const int DefaultBatchSize = 2048;

    public Hash128 SourceId => Source;
    public string SourceName => "CILIDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly object _namesLock = new();
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _names;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["WordNet_Synset"],
            relationNodeNames: ["IS_TYPED_AS", "HAS_DEFINITION", "HAS_NAME_ALIAS", "HAS_SYNSET_KEY"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;
        if (options.DryRun) yield break;

        var reader = context.Reader;
        int batchSize = options.BatchSize > 1 ? options.BatchSize : DefaultBatchSize;
        int workers = IngestParallelism.ResolveFileWorkers(coreHeadroom: 1);


        string ttl = Path.Combine(root, "ili.ttl");
        if (File.Exists(ttl))
        {
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliTtlAsync(ttl, ct),
                workers, batchSize,
                () => NewBuilder("cili/concepts", 0, batchSize, reader),
                (b, rec) =>
                {
                    var (ili, def) = rec;
                    if (ContentEmitter.Emit(b, ili, Source) is not { } id) return;
                    b.AddAttestation(NativeAttestation.Categorical(
                        id, "IS_TYPED_AS", SynsetTypeId, Source, TC.AcademicCurated));
                    if (def is { Length: > 0 } && ContentEmitter.Emit(b, def, Source) is { } dId)
                    {
                        b.AddAttestation(NativeAttestation.Categorical(
                            id, "HAS_NAME_ALIAS", dId, Source, TC.AcademicCurated, EngLang));
                        b.AddAttestation(NativeAttestation.Categorical(
                            id, "HAS_DEFINITION", dId, Source, TC.AcademicCurated, EngLang));
                    }
                },
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }


        var tabs = Directory.EnumerateFiles(root, "ili-map-*.tab", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            ct.ThrowIfCancellationRequested();
            string version = VersionLabel(tab);
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliMapAsync(tab, version, ct),
                workers, batchSize,
                () => NewBuilder($"cili/map/{version}", 0, batchSize, reader),
                EmitMapRow,
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }


        var ttlMaps = Directory.EnumerateFiles(root, "ili-map-*.ttl", SearchOption.AllDirectories)
                               .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var ttlMap in ttlMaps)
        {
            ct.ThrowIfCancellationRequested();
            string version = VersionLabel(ttlMap);
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliMapTtlAsync(ttlMap, version, ct),
                workers, batchSize,
                () => NewBuilder($"cili/map/{version}", 0, batchSize, reader),
                EmitMapRow,
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }
    }

    private static void EmitMapRow(SubstrateChangeBuilder mb, (byte[] Ili, byte[] OffsetPos, string Version) rec)
    {
        var (ili, offsetPos, version) = rec;
        if (ContentEmitter.Emit(mb, ili, Source) is not { } id) return;
        if (ContentEmitter.Emit(mb, offsetPos, Source) is not { } keyId) return;
        var verCtx = ContentEmitter.Emit(mb, version, Source) ?? id;
        mb.AddAttestation(NativeAttestation.Categorical(
            id, "HAS_SYNSET_KEY", keyId, Source, TC.AcademicCurated, verCtx));
    }



    private static async IAsyncEnumerable<(byte[] Ili, byte[] OffsetPos, string Version)> ParseIliMapAsync(
        string tab, string version, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ReadOnlyMemory<byte> lineMem in StreamingUtf8LineReader.ReadLinesAsync(tab, ct))
        {
            if (TryParseIliMapLine(lineMem.Span, out var ili, out var offsetPos))
                yield return (ili, offsetPos, version);
        }
    }

    private static bool TryParseIliMapLine(ReadOnlySpan<byte> span, out byte[] ili, out byte[] offsetPos)
    {
        ili = [];
        offsetPos = [];
        int sep = span.IndexOf((byte)'\t');
        if (sep <= 0) return false;
        ReadOnlySpan<byte> iliSpan = TrimAscii(span[..sep]);
        ReadOnlySpan<byte> rest = span[(sep + 1)..];
        int sep2 = rest.IndexOf((byte)'\t');



        ReadOnlySpan<byte> offsetPosSpan = TrimAscii(sep2 >= 0 ? rest[..sep2] : rest);
        if (iliSpan.IsEmpty || offsetPosSpan.IsEmpty || iliSpan[0] != (byte)'i') return false;
        ili = iliSpan.ToArray();
        offsetPos = offsetPosSpan.ToArray();
        return true;
    }

    private static async IAsyncEnumerable<(byte[] Ili, byte[] OffsetPos, string Version)> ParseIliMapTtlAsync(
        string path, string version, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (ReadOnlyMemory<byte> lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (TryParseIliMapTtlLine(lineMem.Span, out var ili, out var offsetPos))
                yield return (ili, offsetPos, version);
        }
    }

    private static bool TryParseIliMapTtlLine(ReadOnlySpan<byte> span, out byte[] ili, out byte[] offsetPos)
    {
        ili = [];
        offsetPos = [];
        ReadOnlySpan<byte> line = TrimAscii(span);
        if (line.IsEmpty || line[0] == (byte)'@' || line[0] == (byte)'#') return false;

        ReadOnlySpan<byte> sameAsTag = "owl:sameAs"u8;
        int sameAs = line.IndexOf(sameAsTag);
        if (sameAs < 0) return false;

        ReadOnlySpan<byte> iliSpan = NormalizeIliBytes(TrimAscii(line[..sameAs]));
        if (iliSpan.IsEmpty) return false;

        ReadOnlySpan<byte> rest = TrimAscii(line[(sameAs + sameAsTag.Length)..]);
        int hash = rest.IndexOf((byte)'#');
        if (hash >= 0) rest = rest[..hash];
        int dot = rest.LastIndexOf((byte)'.');
        if (dot >= 0) rest = rest[..dot];
        ReadOnlySpan<byte> objTerm = TrimAscii(rest);
        if (objTerm.IsEmpty) return false;

        int prefixColon = objTerm.IndexOf((byte)':');
        ReadOnlySpan<byte> offsetPosSpan = prefixColon >= 0 ? TrimAscii(objTerm[(prefixColon + 1)..]) : objTerm;
        if (offsetPosSpan.IsEmpty) return false;

        ili = iliSpan.ToArray();
        offsetPos = offsetPosSpan.ToArray();
        return true;
    }

    private static async IAsyncEnumerable<(byte[] Ili, byte[]? Def)> ParseIliTtlAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[]? curIli = null;
        byte[]? curDef = null;
        var pending = new List<(byte[], byte[]?)>(2);
        await foreach (ReadOnlyMemory<byte> lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            pending.Clear();
            ProcessIliTtlLine(lineMem, ref curIli, ref curDef, pending);
            foreach (var item in pending)
                yield return item;
        }
        if (curIli is not null) yield return (curIli, curDef);
    }

    private static void ProcessIliTtlLine(
        ReadOnlyMemory<byte> lineMem, ref byte[]? curIli, ref byte[]? curDef,
        List<(byte[], byte[]?)> results)
    {
        ReadOnlySpan<byte> t = TrimAscii(lineMem.Span);
        if (t.IsEmpty) return;

        bool isSubject = t.Length > 2 && t[0] == (byte)'<' && t[1] == (byte)'i'
                         && t[2] >= (byte)'0' && t[2] <= (byte)'9';
        if (isSubject)
        {
            if (curIli is not null) results.Add((curIli, curDef));
            int gt = t.IndexOf((byte)'>');
            curIli = gt > 1 ? t[1..gt].ToArray() : null;
            curDef = null;
            if (t[^1] == (byte)'.')
            {
                if (curIli is not null) results.Add((curIli, curDef));
                curIli = null; curDef = null;
            }
            return;
        }

        if (curIli is null) return;
        if (t.IndexOf("skos:definition"u8) >= 0)
            curDef = ExtractTurtleStringBytes(t) ?? curDef;
        if (t[^1] == (byte)'.')
        {
            results.Add((curIli, curDef));
            curIli = null; curDef = null;
        }
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> span) =>
        Utf8TextHelpers.TrimAscii(span);

    private static ReadOnlySpan<byte> NormalizeIliBytes(ReadOnlySpan<byte> term)
    {
        ReadOnlySpan<byte> s = TrimAscii(term);
        if (s.StartsWith("ili:"u8)) s = s["ili:".Length..];
        if (s.Length >= 2 && s[0] == (byte)'<' && s[^1] == (byte)'>') s = s[1..^1];
        s = TrimAscii(s);
        return s.Length > 1 && s[0] == (byte)'i' && s[1] >= (byte)'0' && s[1] <= (byte)'9' ? s : default;
    }

    private static byte[]? ExtractTurtleStringBytes(ReadOnlySpan<byte> span) =>
        Utf8TextHelpers.ExtractTurtleStringBytes(span);

    private static SubstrateChangeBuilder NewBuilder(string label, int bn, int batchSize, ISubstrateReader? _) =>
        new SubstrateChangeBuilder(Source, $"{label}-{bn}", null,
            entityCapacity: batchSize * 4, physicalityCapacity: batchSize * 4,
            attestationCapacity: batchSize * 4);

    private static string VersionLabel(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "ili-map-";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(120_000L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
