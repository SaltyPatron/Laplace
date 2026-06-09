using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

public sealed class UnicodeDecomposer : IDecomposer
{
    public static readonly Hash128 Source     = Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");
    public static readonly Hash128 TrustClass = Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");
    public static readonly Hash128 CodepointType = Hash128.OfCanonical("substrate/type/Codepoint/v1");

    private static readonly Hash128[] CombiningClassIds = BuildCombiningClassIds();

    private static Hash128[] BuildCombiningClassIds()
    {
        var ids = new Hash128[255];
        for (int cc = 1; cc <= 254; cc++)
            ids[cc] = Hash128.OfCanonical($"unicode/combining_class/{cc}/v1");
        return ids;
    }

    private const string UnicodeVersion = "17.0.0";
    private const int DefaultBatch = 4096;

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private CodepointRecord[]? _records;
    private UcdProperties? _ucd;

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet     = ducet;
    }

    public Hash128 SourceId    => Source;
    public string  SourceName  => "UnicodeDecomposer";
    public int     LayerOrder  => 0;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Codepoint");
        boot.AddType("UcdClassifier");
        boot.AddType("OrdinalContext");
        boot.AddRelationType("HAS_GENERAL_CATEGORY");
        boot.AddRelationType("HAS_COMBINING_CLASS");
        boot.AddRelationType("HAS_SCRIPT");
        boot.AddRelationType("HAS_BLOCK");
        boot.AddRelationType("HAS_UPPERCASE_MAPPING");
        boot.AddRelationType("HAS_LOWERCASE_MAPPING");
        boot.AddRelationType("CANONICAL_DECOMPOSES_TO");
        boot.AddRelationType("HAS_TITLECASE_MAPPING");
        boot.AddRelationType("COMPATIBILITY_DECOMPOSES_TO");
        boot.AddRelationType("HAS_NUMERIC_VALUE");
        boot.AddRelationType("HAS_BIDI_CLASS");
        boot.AddRelationType("HAS_MIRROR");
        boot.AddRelationType("HAS_AGE");
        boot.AddRelationType("HAS_NAME_ALIAS");
        boot.AddRelationType("CONFUSABLE_WITH");
        boot.AddRelationType("HAS_EMOJI_PROPERTY");
        boot.AddRelationType("DECODES_TO");
        boot.AddRelationType("HAS_UTF8_ROLE");
        boot.AddType("Byte");
        boot.AddType("Utf8Role");
        boot.AddType("CharacterEncoding");
        await context.Writer.ApplyAsync(boot.Build(), ct);

        EnsureUcdProperties(context);
        var ucdClassifierTypeId = Hash128.OfCanonical("substrate/type/UcdClassifier/v1");
        var ordinalContextTypeId = Hash128.OfCanonical("substrate/type/OrdinalContext/v1");
        var classifiers = new SubstrateChangeBuilder(
            Source, "bootstrap/ucd-classifiers", null,
            entityCapacity: 2048, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (var row in _ucd!.ClassificationEntities(Source))
            classifiers.AddEntity(row);
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx0, EntityTier.Vocabulary, ordinalContextTypeId, Source));
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx1, EntityTier.Vocabulary, ordinalContextTypeId, Source));
        for (int cc = 1; cc <= 254; cc++)
            classifiers.AddEntity(new EntityRow(CombiningClassIds[cc], EntityTier.Vocabulary, ucdClassifierTypeId, Source));
        await context.Writer.ApplyAsync(classifiers.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureComputed(context);
        EnsureUcdProperties(context);
        int total = _records!.Length;
        int batch = options.BatchSize > 1 ? options.BatchSize : DefaultBatch;

        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end);
            await Task.Yield();
        }

        {
            var recs = _records!;
            var ucd = _ucd!;
            var b = new SubstrateChangeBuilder(Source, "ucd/aliases-confusables/0", null,
                entityCapacity: batch, physicalityCapacity: batch, attestationCapacity: batch);
            int count = 0, bn = 0;

            foreach (var (cp, aliases) in ucd.NameAliases)
            {
                ct.ThrowIfCancellationRequested();
                if (cp >= (uint)recs.Length) continue;
                foreach (var alias in aliases)
                {
                    var aliasId = ContentEmitter.Emit(b, alias, Source);
                    if (aliasId is { } aid)
                        b.AddAttestation(RelationTypeRegistry.Attest(
                            StageCodepointTarget(b, recs, cp), "HAS_NAME_ALIAS", aid, Source, SourceTrust.StandardsDerived));
                    count++;
                }
                if (count >= batch)
                {
                    yield return b.Build();
                    b = new SubstrateChangeBuilder(Source, $"ucd/aliases-confusables/{++bn}", null,
                        entityCapacity: batch, physicalityCapacity: batch, attestationCapacity: batch);
                    count = 0;
                    await Task.Yield();
                }
            }

            foreach (var (src, target) in ucd.Confusables)
            {
                ct.ThrowIfCancellationRequested();
                if (src >= (uint)recs.Length || target.Length == 0) continue;
                int first = char.ConvertToUtf32(target, 0);
                int firstLen = char.IsSurrogatePair(target, 0) ? 2 : 1;
                Hash128? targetId = target.Length == firstLen
                    ? StageCodepointTarget(b, recs, (uint)first)
                    : ContentEmitter.Emit(b, target, Source);
                if (targetId is { } tid)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        StageCodepointTarget(b, recs, src), "CONFUSABLE_WITH", tid, Source, SourceTrust.StandardsDerived));
                if (++count >= batch)
                {
                    yield return b.Build();
                    b = new SubstrateChangeBuilder(Source, $"ucd/aliases-confusables/{++bn}", null,
                        entityCapacity: batch, physicalityCapacity: batch, attestationCapacity: batch);
                    count = 0;
                    await Task.Yield();
                }
            }
            if (count > 0) yield return b.Build();
        }

        {
            var recs = _records!;
            var bb = new SubstrateChangeBuilder(Source, "bytes/atoms-and-encodings", null,
                entityCapacity: 512, physicalityCapacity: 128, attestationCapacity: 512);

            var latin1 = Hash128.OfCanonical("substrate/encoding/ISO-8859-1/v1");
            var cp1252 = Hash128.OfCanonical("substrate/encoding/windows-1252/v1");
            var encType = Hash128.OfCanonical("substrate/type/CharacterEncoding/v1");
            var roleType = Hash128.OfCanonical("substrate/type/Utf8Role/v1");
            bb.AddEntity(new EntityRow(latin1, EntityTier.Vocabulary, encType, Source));
            bb.AddEntity(new EntityRow(cp1252, EntityTier.Vocabulary, encType, Source));
            var roleIds = new Dictionary<string, Hash128>(StringComparer.Ordinal);
            foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
            {
                var rid = Hash128.OfCanonical($"substrate/utf8/{role}/v1");
                roleIds[role] = rid;
                bb.AddEntity(new EntityRow(rid, EntityTier.Vocabulary, roleType, Source));
            }

            for (int v = ByteAtoms.First; v <= 0xFF; v++)
            {
                byte bv = (byte)v;
                Hash128 byteId = ByteAtoms.Id(bv);
                bb.AddEntity(byteId, tier: 0, ByteAtoms.TypeId, firstObservedBy: Source);

                var coord = ByteAtoms.Coord(bv);
                Hash128 physId = PhysicalityId.Compute(
                    byteId, Source, PhysicalityType.Content,
                    coord[0], coord[1], coord[2], coord[3], ReadOnlySpan<double>.Empty);
                bb.AddPhysicality(new PhysicalityRow(
                    Id: physId, EntityId: byteId, SourceId: Source,
                    Type: PhysicalityType.Content,
                    CoordX: coord[0], CoordY: coord[1], CoordZ: coord[2], CoordM: coord[3],
                    HilbertIndex: ByteAtoms.Hilbert(bv),
                    TrajectoryXyzm: null, NConstituents: 0,
                    AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));

                bb.AddAttestation(RelationTypeRegistry.Attest(
                    byteId, "HAS_UTF8_ROLE", roleIds[ByteAtoms.Utf8Role(bv)],
                    Source, SourceTrust.StandardsDerived));

                bb.AddAttestation(RelationTypeRegistry.Attest(
                    byteId, "DECODES_TO", StageCodepointTarget(bb, recs, (uint)v), Source,
                    SourceTrust.StandardsDerived, contextId: latin1));

                uint cp1252Target = bv <= 0x9F
                    ? ByteAtoms.Cp1252High[bv - 0x80]
                    : (uint)bv;
                if (cp1252Target != 0)
                    bb.AddAttestation(RelationTypeRegistry.Attest(
                        byteId, "DECODES_TO", StageCodepointTarget(bb, recs, cp1252Target), Source,
                        SourceTrust.StandardsDerived, contextId: cp1252));
            }
            yield return bb.Build();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(UnicodeSeed.CodepointCount);

    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var ucd = _ucd;
            if (ucd is null) return Array.Empty<string>();
            var names = new List<string>(2048);
            foreach (var n in ucd.CategoryEntityIds.Keys)   names.Add($"unicode/category/{n}/v1");
            foreach (var n in ucd.ScriptEntityIds.Keys)     names.Add($"unicode/script/{n}/v1");
            foreach (var n in ucd.BlockEntityIds.Keys)      names.Add($"unicode/block/{n}/v1");
            foreach (var n in ucd.BidiClassEntityIds.Keys)  names.Add($"unicode/bidi_class/{n}/v1");
            foreach (var n in ucd.AgeEntityIds.Keys)        names.Add($"unicode/age/{n}/v1");
            foreach (var n in ucd.EmojiPropEntityIds.Keys)  names.Add($"unicode/emoji/{n}/v1");
            foreach (var v in ucd.NumericEntityIds.Keys)    names.Add($"unicode/numeric/{v}/v1");
            for (int cc = 1; cc <= 254; cc++)               names.Add($"unicode/combining_class/{cc}/v1");
            names.Add("substrate/type/Byte/v1");
            names.Add("substrate/encoding/ISO-8859-1/v1");
            names.Add("substrate/encoding/windows-1252/v1");
            foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
                names.Add($"substrate/utf8/{role}/v1");
            return names;
        }
    }

    public ValueTask DisposeAsync() { _records = null; _ucd = null; return ValueTask.CompletedTask; }

    private SubstrateChange BuildBatch(int start, int end)
    {
        int n = end - start;
        var b = new SubstrateChangeBuilder(
            Source, $"codepoints/U+{start:X4}..U+{(end - 1):X4}", null,
            entityCapacity:      n * 4,
            physicalityCapacity: n,
            attestationCapacity: n * 12);

        CodepointRecord[] recs = _records!;
        UcdProperties ucd = _ucd!;

        for (int cp = start; cp < end; cp++)
        {
            ref readonly CodepointRecord r = ref recs[cp];
            Hash128 entityId = r.Hash;

            b.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);

            Hash128 physId = PhysicalityId.Compute(
                entityId, Source, PhysicalityType.Content,
                r.CoordX, r.CoordY, r.CoordZ, r.CoordM,
                ReadOnlySpan<double>.Empty);

            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: entityId, SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: r.CoordX, CoordY: r.CoordY, CoordZ: r.CoordZ, CoordM: r.CoordM,
                HilbertIndex: r.Hilbert,
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));

            uint ucp = (uint)cp;

            string? cat = ucd.GeneralCategory[cp];
            if (cat != null && ucd.CategoryEntityIds.TryGetValue(cat, out var catId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasGeneralCategory,
                    catId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            if (ucd.CombiningClass[cp] > 0)
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasCombiningClass,
                    CombiningClassIds[ucd.CombiningClass[cp]], Source, null,
                    RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            string? script = ucd.ScriptForCodepoint(ucp);
            if (script != null && ucd.ScriptEntityIds.TryGetValue(script, out var scriptId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasScript,
                    scriptId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            string? block = ucd.BlockForCodepoint(ucp);
            if (block != null && ucd.BlockEntityIds.TryGetValue(block, out var blockId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasBlock,
                    blockId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            if (ucd.UppercaseMapping[cp] != 0)
            {
                uint targetCp = ucd.UppercaseMapping[cp];
                if (targetCp < (uint)recs.Length)
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasUppercaseMapping,
                        StageCodepointTarget(b, recs, targetCp), Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
            }

            if (ucd.LowercaseMapping[cp] != 0)
            {
                uint targetCp = ucd.LowercaseMapping[cp];
                if (targetCp < (uint)recs.Length)
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasLowercaseMapping,
                        StageCodepointTarget(b, recs, targetCp), Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
            }

            uint[]? decomp = ucd.CanonDecomp[cp];
            if (decomp != null)
            {
                for (int di = 0; di < decomp.Length; di++)
                {
                    uint targetCp = decomp[di];
                    if (targetCp < (uint)recs.Length)
                    {
                        Hash128 ctx = di == 0 ? UcdProperties.OrdinalCtx0 : UcdProperties.OrdinalCtx1;
                        b.AddAttestation(AttestationFactory.Create(entityId,
                            UcdProperties.KindCanonDecomposesTo,
                            StageCodepointTarget(b, recs, targetCp), Source, ctx,
                            RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
                    }
                }
            }

            if (ucd.TitlecaseMapping[cp] != 0 && ucd.TitlecaseMapping[cp] < (uint)recs.Length)
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasTitlecaseMapping,
                    StageCodepointTarget(b, recs, ucd.TitlecaseMapping[cp]), Source, null,
                    RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            uint[]? compat = ucd.CompatDecomp[cp];
            if (compat != null)
            {
                for (int di = 0; di < compat.Length; di++)
                {
                    uint targetCp = compat[di];
                    if (targetCp < (uint)recs.Length)
                    {
                        Hash128 ctx = di == 0 ? UcdProperties.OrdinalCtx0 : UcdProperties.OrdinalCtx1;
                        b.AddAttestation(AttestationFactory.Create(entityId,
                            UcdProperties.KindCompatDecomposesTo,
                            StageCodepointTarget(b, recs, targetCp), Source, ctx,
                            RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
                    }
                }
            }

            string? num = ucd.NumericValue[cp];
            if (num != null && ucd.NumericEntityIds.TryGetValue(num, out var numId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasNumericValue,
                    numId, Source, null, RelationTypeRank.ScalarValued, SourceTrust.StandardsDerived));

            string? bidi = ucd.BidiClass[cp];
            if (bidi != null && ucd.BidiClassEntityIds.TryGetValue(bidi, out var bidiId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasBidiClass,
                    bidiId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            uint mir = ucd.BidiMirror[cp];
            if (mir != 0 && mir < (uint)recs.Length && cp <= mir)
                b.AddAttestation(RelationTypeRegistry.Attest(
                    entityId, "HAS_MIRROR", StageCodepointTarget(b, recs, mir), Source, SourceTrust.StandardsDerived));

            string? age = ucd.AgeForCodepoint(ucp);
            if (age != null && ucd.AgeEntityIds.TryGetValue(age, out var ageId))
                b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasAge,
                    ageId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));

            byte eprops = ucd.EmojiProps[cp];
            if (eprops != 0)
                for (int bit = 0; bit < UcdProperties.EmojiPropNames.Length; bit++)
                    if ((eprops & (1 << bit)) != 0
                        && ucd.EmojiPropEntityIds.TryGetValue(UcdProperties.EmojiPropNames[bit], out var epId))
                        b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasEmojiProperty,
                            epId, Source, null, RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
        }
        return b.Build();
    }

    private Hash128 StageCodepointTarget(SubstrateChangeBuilder b, CodepointRecord[] recs, uint targetCp)
    {
        Hash128 targetId = recs[targetCp].Hash;
        b.AddEntity(targetId, tier: 0, CodepointType, firstObservedBy: Source);
        return targetId;
    }

    private void EnsureComputed(IDecomposerContext context)
    {
        if (_records is not null) return;
        var (xml, duc) = ResolveSource(context);
        _records = UnicodeSeed.Compute(xml, duc);
    }

    private void EnsureUcdProperties(IDecomposerContext context)
    {
        if (_ucd is not null) return;
        string ucdDir = Path.Combine(context.EcosystemPath, "Public", UnicodeVersion, "ucd");
        _ucd = UcdProperties.Load(ucdDir);
    }

    private (string xml, string duc) ResolveSource(IDecomposerContext context)
    {
        string baseDir = context.EcosystemPath;
        string xml = _ucdxmlZip ?? Path.Combine(baseDir, "Public", UnicodeVersion, "ucdxml", "ucd.nounihan.flat.zip");
        string duc = _ducet    ?? Path.Combine(baseDir, "Public", UnicodeVersion, "uca", "allkeys.txt");
        return (xml, duc);
    }
}
