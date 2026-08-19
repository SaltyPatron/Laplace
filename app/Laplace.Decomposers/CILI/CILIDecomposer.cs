using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.CILI;

public sealed class CILIDecomposer : DecomposerMultiPhase<CILISource, FullScope>
{
    private enum CiliEntryKind : byte { Unknown, Concept, Instance }
    private readonly record struct CiliConceptRecord(
        byte[] Ili, CiliEntryKind Kind, byte[]? Definition,
        string? SourceVersion, byte[]? SourceKey);
    private readonly record struct CiliMapInput(string Path, string Version, bool IsTab);

    public static readonly Hash128 Source = CILISource.SourceId;
    public static readonly Hash128 TrustClass = CILISource.TrustClass;

    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;
    private static readonly Hash128 EngLang = LanguageEntityId.FromIso639_3("eng");


    public override int LayerOrder => 2;

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;

        string ttl = Path.Combine(root, "ili.ttl");
        if (File.Exists(ttl))
        {
            await foreach (var change in RunPhaseAsync(new ConceptsPhase(), context, options, ct))
                yield return change;
        }

        foreach (CiliMapInput map in SelectMapInputs(root, conceptsContainPwn30: File.Exists(ttl)))
        {
            ct.ThrowIfCancellationRequested();
            IDecomposer phase = map.IsTab
                ? new MapTabPhase(map.Path, map.Version)
                : new MapTtlPhase(map.Path, map.Version);
            await foreach (var change in RunPhaseAsync(phase, context, options, ct))
                yield return change;
        }
    }

    private static int ResolveBatch(DecomposerOptions options) =>
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Cili, options);

    private abstract class CiliComposePhase<T> : ComposeDecomposerPhase<T>
    {
        public override Hash128 SourceId => Source;
        public override string SourceName => "CILIDecomposer";
        public override int LayerOrder => 2;
        public override Hash128 TrustClassId => TrustClass;
        protected override double SourceTrust => TC.AcademicCurated;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId, BatchLabelPrefix, ResolveBatch(options), options, context.Reader, PipelineProfile),
                options);
    }

    private sealed class ConceptsPhase : CiliComposePhase<CiliConceptRecord>
    {
        protected override string PhaseLabel => "concepts";
        protected override void Compose(CiliConceptRecord rec, SubstrateChangeBuilder b)
        {
            if (ReferenceAnchor.DeclareUtf8(
                    b, ReferenceIdentityKind.CiliIli, rec.Ili,
                    SynsetTypeId, Source) is not { } id)
                return;

            Hash128? nativeType = rec.Kind switch
            {
                CiliEntryKind.Concept => EntityTypeRegistry.CiliConcept,
                CiliEntryKind.Instance => EntityTypeRegistry.CiliInstance,
                _ => null,
            };
            if (nativeType is { } typeId)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    id, CILISource.IsTypedAsTypeId, typeId,
                    Source, null, TC.AcademicCurated));

            // CILI asserts a DEFINITION for the ILI concept — only that. The old
            // duplicate HAS_NAME_ALIAS emission of the same text made resolve_name's
            // authoritative-name arm serve the gloss as every synset's NAME,
            // outranking the synset-lemma path substrate-wide (record what the
            // source asserts, at the relation it asserts it).
            if (rec.Definition is { Length: > 0 } def
                && ContentEmitter.Emit(b, def, Source) is { } dId)
            {
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    id, CILISource.HasDefinitionTypeId, dId,
                    Source, EngLang, TC.AcademicCurated));
            }


            if (rec.SourceVersion is { Length: > 0 } version
                && rec.SourceKey is { Length: > 0 } sourceKey)
                EmitMapRow(b, (rec.Ili, sourceKey, version));
        }
        protected override async IAsyncEnumerable<CiliConceptRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in ParseIliTtlAsync(Path.Combine(ecosystemPath, "ili.ttl"), ct))
                yield return rec;
        }
    }

    private sealed class MapTabPhase : CiliComposePhase<(byte[] Ili, byte[] OffsetPos, string Version)>
    {
        private readonly string _path;
        private readonly string _version;

        public MapTabPhase(string path, string version)
        {
            _path = path;
            _version = version;
        }

        protected override string PhaseLabel => $"map/{_version}";
        protected override void Compose((byte[] Ili, byte[] OffsetPos, string Version) rec, SubstrateChangeBuilder b) =>
            EmitMapRow(b, rec);
        protected override async IAsyncEnumerable<(byte[] Ili, byte[] OffsetPos, string Version)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in ParseIliMapAsync(_path, _version, ct))
                yield return rec;
        }
    }

    private sealed class MapTtlPhase : CiliComposePhase<(byte[] Ili, byte[] OffsetPos, string Version)>
    {
        private readonly string _path;
        private readonly string _version;

        public MapTtlPhase(string path, string version)
        {
            _path = path;
            _version = version;
        }

        protected override string PhaseLabel => $"map/{_version}";
        protected override void Compose((byte[] Ili, byte[] OffsetPos, string Version) rec, SubstrateChangeBuilder b) =>
            EmitMapRow(b, rec);
        protected override async IAsyncEnumerable<(byte[] Ili, byte[] OffsetPos, string Version)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in ParseIliMapTtlAsync(_path, _version, ct))
            {
                byte[] normalized = NormalizeMapKey(_version, rec.OffsetPos);
                yield return (rec.Ili, normalized, rec.Version);
            }
        }
    }

    private static void EmitMapRow(
        SubstrateChangeBuilder mb, (byte[] Ili, byte[] OffsetPos, string Version) rec)
    {
        var (ili, offsetPos, version) = rec;
        if (ReferenceAnchor.DeclareUtf8(
                mb, ReferenceIdentityKind.CiliIli, ili, SynsetTypeId, Source) is not { } id)
            return;
        string offsetKey = System.Text.Encoding.UTF8.GetString(offsetPos);
        if (ReferenceAnchor.DeclareWordNetSynsetKey(
                mb, version, offsetKey, Source) is not { } keyId)
            return;
        var verCtx = ReferenceAnchor.Declare(
            mb, ReferenceIdentityKind.CiliMapVersion, version,
            EntityTypeRegistry.SourceVersion, Source) ?? id;
        mb.AddAttestation(NativeAttestation.CategoricalResolved(
            id, CILISource.HasSynsetKeyTypeId, keyId,
            Source, verCtx, TC.AcademicCurated));
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

    private static async IAsyncEnumerable<CiliConceptRecord> ParseIliTtlAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        byte[]? curIli = null;
        byte[]? curDef = null;
        CiliEntryKind curKind = CiliEntryKind.Unknown;
        string? curSourceVersion = null;
        byte[]? curSourceKey = null;
        var pending = new List<CiliConceptRecord>(2);
        await foreach (ReadOnlyMemory<byte> lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            pending.Clear();
            ProcessIliTtlLine(
                lineMem, ref curIli, ref curKind, ref curDef,
                ref curSourceVersion, ref curSourceKey, pending);
            foreach (var item in pending)
                yield return item;
        }
        if (curIli is not null)
            yield return new CiliConceptRecord(
                curIli, curKind, curDef, curSourceVersion, curSourceKey);
    }

    private static void ProcessIliTtlLine(
        ReadOnlyMemory<byte> lineMem,
        ref byte[]? curIli,
        ref CiliEntryKind curKind,
        ref byte[]? curDef,
        ref string? curSourceVersion,
        ref byte[]? curSourceKey,
        List<CiliConceptRecord> results)
    {
        ReadOnlySpan<byte> t = TrimAscii(lineMem.Span);
        if (t.IsEmpty) return;

        bool isSubject = t.Length > 2 && t[0] == (byte)'<' && t[1] == (byte)'i'
                         && t[2] >= (byte)'0' && t[2] <= (byte)'9';
        if (isSubject)
        {
            if (curIli is not null)
                results.Add(new CiliConceptRecord(
                    curIli, curKind, curDef, curSourceVersion, curSourceKey));
            int gt = t.IndexOf((byte)'>');
            curIli = gt > 1 ? t[1..gt].ToArray() : null;
            curKind = CiliEntryKind.Unknown;
            curDef = null;
            curSourceVersion = null;
            curSourceKey = null;
        }

        if (curIli is null) return;
        if (t.IndexOf("<Concept>"u8) >= 0 || t.IndexOf("ili:Concept"u8) >= 0)
            curKind = CiliEntryKind.Concept;
        else if (t.IndexOf("<Instance>"u8) >= 0 || t.IndexOf("ili:Instance"u8) >= 0)
            curKind = CiliEntryKind.Instance;
        if (t.IndexOf("skos:definition"u8) >= 0)
            curDef = ExtractTurtleStringBytes(t) ?? curDef;
        if (TryExtractSourceKey(t, out string? version, out byte[]? sourceKey))
        {
            curSourceVersion = version;
            curSourceKey = sourceKey;
        }
        if (t[^1] == (byte)'.')
        {
            results.Add(new CiliConceptRecord(
                curIli, curKind, curDef, curSourceVersion, curSourceKey));
            curIli = null;
            curKind = CiliEntryKind.Unknown;
            curDef = null;
            curSourceVersion = null;
            curSourceKey = null;
        }
    }

    private static bool TryExtractSourceKey(
        ReadOnlySpan<byte> line, out string? version, out byte[]? sourceKey)
    {
        version = null;
        sourceKey = null;
        ReadOnlySpan<byte> tag = "dc:source"u8;
        int at = line.IndexOf(tag);
        if (at < 0) return false;
        ReadOnlySpan<byte> term = TrimAscii(line[(at + tag.Length)..]);
        int end = term.IndexOfAny(" \t;."u8);
        if (end >= 0) term = term[..end];
        int colon = term.IndexOf((byte)':');
        if (colon <= 0 || colon == term.Length - 1) return false;
        version = CanonicalMapVersion(System.Text.Encoding.ASCII.GetString(term[..colon]));
        sourceKey = term[(colon + 1)..].ToArray();
        return sourceKey.Length > 0;
    }

    private static byte[] NormalizeMapKey(string version, byte[] key)
    {
        // The PWN 3.1 RDF namespace serializes the 8-digit offset with a
        // version marker prefix (`3xxxxxxxx-p`); the tab and native WordNet
        // forms use the underlying 8-digit key. Decode the serialization before
        // governed identity so both forms cannot mint parallel references.
        if (version == "pwn31" && key.Length == 11 && key[0] == (byte)'3'
            && key[9] == (byte)'-')
            return key[1..];
        return key;
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

    private static string VersionLabel(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "ili-map-";
        string raw = name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;
        return CanonicalMapVersion(raw);
    }

    private static string CanonicalMapVersion(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "wn30" or "pwn30" or "ili-map" => "pwn30",
        "wn31" or "pwn31" => "pwn31",
        var version => version,
    };

    private static IReadOnlyList<CiliMapInput> SelectMapInputs(
        string root, bool conceptsContainPwn30)
    {
        var candidates = Directory
            .EnumerateFiles(root, "ili-map-*.tab", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "ili-map-*.ttl", SearchOption.AllDirectories))
            .Select(path => new CiliMapInput(
                path, VersionLabel(path),
                Path.GetExtension(path).Equals(".tab", StringComparison.OrdinalIgnoreCase)))
            .Where(map => !(conceptsContainPwn30 && map.Version == "pwn30"));

        // Each version is one mapping, even when the repository publishes it
        // simultaneously as RDF and tab packaging. Prefer RDF because the PWN
        // 3.1 file contains 27 mappings absent from its tab export; older releases
        // with only tab data remain admitted. File formats are not witnesses.
        return candidates
            .GroupBy(map => map.Version, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(map => map.IsTab)
                .ThenBy(map => map.Path, StringComparer.Ordinal)
                .First())
            .OrderBy(map => map.Version, StringComparer.Ordinal)
            .ThenBy(map => map.Path, StringComparer.Ordinal)
            .ToList();
    }

    // The unit numerator counts parsed concept/map records. CILI's main Turtle
    // serialization is four physical lines per record; map serializations are
    // one record per data line with only a small fixed header. The live observed
    // floor remains authoritative when a future package changes either layout.
    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string root = context.EcosystemPath;
        long total = 0;
        string ttl = Path.Combine(root, "ili.ttl");
        if (File.Exists(ttl))
            total += Math.Max(1, EtlInventory.EstimateNewlineCount(ttl, ct) / 4);
        foreach (CiliMapInput map in SelectMapInputs(root, conceptsContainPwn30: File.Exists(ttl)))
            total += EtlInventory.EstimateNewlineCount(map.Path, ct);
        return Task.FromResult<long?>(total > 0 ? total : null);
    }
}
