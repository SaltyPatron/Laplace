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

    // Combining-class value id memo (the perf-cache discipline, sibling to the
    // pre-built CategoryEntityIds / ScriptEntityIds dictionaries this loop also
    // reads). Combining class is the closed set 1..254, but the per-codepoint emit
    // loop runs over the whole codepoint space — without the memo every combining
    // codepoint re-formats + UTF8-encodes + BLAKE3s the same string. Computed once
    // per distinct class; index 0 is unused (zero is the default, never attested).
    private static readonly Hash128[] CombiningClassIds = BuildCombiningClassIds();

    private static Hash128[] BuildCombiningClassIds()
    {
        var ids = new Hash128[255];
        for (int cc = 1; cc <= 254; cc++)
            ids[cc] = Hash128.OfCanonical($"unicode/combining_class/{cc}/v1");
        return ids;
    }

    private const string UnicodeVersion = "17.0.0";
    private const int DefaultBatch = 4096;   // smaller: batches now carry attestations

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
        // ── bootstrap: types + attestation kinds ──
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Codepoint");
        boot.AddType("UcdClassifier");
        boot.AddType("OrdinalContext");
        // Rank lives ONLY in KindRegistry (all UCD kinds are StandardsStructural).
        boot.AddKind("HAS_GENERAL_CATEGORY");
        boot.AddKind("HAS_COMBINING_CLASS");
        boot.AddKind("HAS_SCRIPT");
        boot.AddKind("HAS_BLOCK");
        boot.AddKind("HAS_UPPERCASE_MAPPING");
        boot.AddKind("HAS_LOWERCASE_MAPPING");
        boot.AddKind("CANONICAL_DECOMPOSES_TO");
        // 2026-06-05 completeness sweep — the UCD properties the seed left out.
        boot.AddKind("HAS_TITLECASE_MAPPING");
        boot.AddKind("COMPATIBILITY_DECOMPOSES_TO");
        boot.AddKind("HAS_NUMERIC_VALUE");
        boot.AddKind("HAS_BIDI_CLASS");
        boot.AddKind("HAS_MIRROR");
        boot.AddKind("HAS_AGE");
        boot.AddKind("HAS_NAME_ALIAS");
        boot.AddKind("CONFUSABLE_WITH");
        boot.AddKind("HAS_EMOJI_PROPERTY");
        // Byte tier (2026-06-05): encoding relations of the 128 high-byte atoms.
        boot.AddKind("DECODES_TO");
        boot.AddKind("HAS_UTF8_ROLE");
        boot.AddType("Byte");
        boot.AddType("Utf8Role");
        boot.AddType("CharacterEncoding");
        await context.Writer.ApplyAsync(boot.Build(), ct);

        // ── seed classifier entities (category/script/block + ordinal contexts + combining class values) ──
        EnsureUcdProperties(context);
        var ucdClassifierTypeId = Hash128.OfCanonical("substrate/type/UcdClassifier/v1");
        var ordinalContextTypeId = Hash128.OfCanonical("substrate/type/OrdinalContext/v1");
        // categories/scripts/blocks/bidi/ages/emoji/numeric classifiers
        // + 2 ordinal + 254 combining-class values
        var classifiers = new SubstrateChangeBuilder(
            Source, "bootstrap/ucd-classifiers", null,
            entityCapacity: 2048, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (var row in _ucd!.ClassificationEntities(Source))
            classifiers.AddEntity(row);
        // Ordinal context entities used as context_id on CANONICAL_DECOMPOSES_TO attestations
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx0, (byte)MetaTier.Meta, ordinalContextTypeId, Source));
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx1, (byte)MetaTier.Meta, ordinalContextTypeId, Source));
        // Combining class value entities used as object_id on HAS_COMBINING_CLASS attestations (1-254)
        for (int cc = 1; cc <= 254; cc++)
            classifiers.AddEntity(new EntityRow(CombiningClassIds[cc], (byte)MetaTier.Meta, ucdClassifierTypeId, Source));
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

        // Pass 1: entities + physicalities only.
        // Attestations reference codepoint entities as object_id (case mappings,
        // decomposition targets). Some targets are in later batches, so we
        // commit all entities before emitting any attestations.
        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end, entitiesOnly: true);
            await Task.Yield();
        }

        // Pass 2: attestations only (all 1.1M codepoint entities now exist).
        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end, entitiesOnly: false);
            await Task.Yield();
        }

        // Pass 3 (2026-06-05 completeness): sparse text-valued properties —
        // formal name aliases (NameAliases.txt) and confusable mappings
        // (security/confusables.txt). Self-contained batches: the alias /
        // sequence CONTENT rides the same intent as its attestation.
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
                        b.AddAttestation(KindRegistry.Attest(
                            recs[cp].Hash, "HAS_NAME_ALIAS", aid, Source, SourceTrust.StandardsDerived));
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
                // single-codepoint target → the codepoint entity itself;
                // sequence target → its content entity (tier tree).
                int first = char.ConvertToUtf32(target, 0);
                int firstLen = char.IsSurrogatePair(target, 0) ? 2 : 1;
                Hash128? targetId = target.Length == firstLen
                    ? recs[(uint)first].Hash
                    : ContentEmitter.Emit(b, target, Source);
                if (targetId is { } tid)
                    b.AddAttestation(KindRegistry.Attest(
                        recs[src].Hash, "CONFUSABLE_WITH", tid, Source, SourceTrust.StandardsDerived));
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

        // Pass 4 (2026-06-05): the BYTE TIER — the substrate's modality-blind
        // floor. Bytes ≤ 0x7F ARE their ASCII codepoint entities (same content
        // bytes, same id — nothing to add). The 128 high bytes are atoms BELOW
        // the codepoint tier: canonical structural placements (super-Fibonacci
        // over the byte band, ByteAtoms — the ONE implementation any anchorer
        // shares), plus the encoding relations the standards define:
        //   byte —HAS_UTF8_ROLE→ continuation / lead2..4 / invalid (RFC 3629);
        //   byte —DECODES_TO(ctx: ISO-8859-1)→ U+0080+b   (Latin-1 alignment);
        //   byte —DECODES_TO(ctx: windows-1252)→ the remapped 0x80–0x9F chars.
        // Which character a byte means is a property of the ENCODING — these
        // are witnessed relations with the encoding as context, NEVER identity.
        {
            var recs = _records!;
            var bb = new SubstrateChangeBuilder(Source, "bytes/atoms-and-encodings", null,
                entityCapacity: 160, physicalityCapacity: 128, attestationCapacity: 512);

            var latin1 = Hash128.OfCanonical("substrate/encoding/ISO-8859-1/v1");
            var cp1252 = Hash128.OfCanonical("substrate/encoding/windows-1252/v1");
            var encType = Hash128.OfCanonical("substrate/type/CharacterEncoding/v1");
            var roleType = Hash128.OfCanonical("substrate/type/Utf8Role/v1");
            bb.AddEntity(new EntityRow(latin1, (byte)MetaTier.Meta, encType, Source));
            bb.AddEntity(new EntityRow(cp1252, (byte)MetaTier.Meta, encType, Source));
            var roleIds = new Dictionary<string, Hash128>(StringComparer.Ordinal);
            foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
            {
                var rid = Hash128.OfCanonical($"substrate/utf8/{role}/v1");
                roleIds[role] = rid;
                bb.AddEntity(new EntityRow(rid, (byte)MetaTier.Meta, roleType, Source));
            }

            for (int v = ByteAtoms.First; v <= 0xFF; v++)
            {
                byte bv = (byte)v;
                Hash128 byteId = ByteAtoms.Id(bv);
                bb.AddEntity(byteId, tier: 0, ByteAtoms.TypeId, firstObservedBy: Source);

                var coord = ByteAtoms.Coord(bv);
                Hash128 physId = PhysicalityId.Compute(
                    byteId, Source, PhysicalityKind.Content,
                    coord[0], coord[1], coord[2], coord[3], ReadOnlySpan<double>.Empty);
                bb.AddPhysicality(new PhysicalityRow(
                    Id: physId, EntityId: byteId, SourceId: Source,
                    Type: PhysicalityKind.Content,
                    CoordX: coord[0], CoordY: coord[1], CoordZ: coord[2], CoordM: coord[3],
                    HilbertIndex: ByteAtoms.Hilbert(bv),
                    TrajectoryXyzm: null, NConstituents: 0,
                    AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));

                bb.AddAttestation(KindRegistry.Attest(
                    byteId, "HAS_UTF8_ROLE", roleIds[ByteAtoms.Utf8Role(bv)],
                    Source, SourceTrust.StandardsDerived));

                // Latin-1: byte value IS the codepoint index (the alignment
                // Unicode chose for its first 256 codepoints).
                bb.AddAttestation(KindRegistry.Attest(
                    byteId, "DECODES_TO", recs[v].Hash, Source,
                    SourceTrust.StandardsDerived, contextId: latin1));

                // Windows-1252: 0x80–0x9F remapped (5 undefined slots skipped);
                // 0xA0–0xFF agrees with Latin-1.
                uint cp1252Target = bv <= 0x9F
                    ? ByteAtoms.Cp1252High[bv - 0x80]
                    : (uint)bv;
                if (cp1252Target != 0)
                    bb.AddAttestation(KindRegistry.Attest(
                        byteId, "DECODES_TO", recs[cp1252Target].Hash, Source,
                        SourceTrust.StandardsDerived, contextId: cp1252));
            }
            yield return bb.Build();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(UnicodeSeed.CodepointCount);

    /// <summary>The data-derived UCD classifier / value canonical names this
    /// decomposer mints (category/script/block/bidi/age/emoji/numeric +
    /// combining-class values). Built from the SAME format templates the
    /// UcdProperties entity-id dictionaries use, so render() resolves the same
    /// ids. Read post-DecomposeAsync (the decomposer caches <c>_ucd</c>) —
    /// returns empty if the UCD properties aren't loaded yet.</summary>
    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var ucd = _ucd;
            if (ucd is null) return Array.Empty<string>();
            // Keys are the names; rebuild the canonical strings from the SAME
            // templates UcdProperties.BuildEntityIds used per dictionary.
            var names = new List<string>(2048);
            foreach (var n in ucd.CategoryEntityIds.Keys)   names.Add($"unicode/category/{n}/v1");
            foreach (var n in ucd.ScriptEntityIds.Keys)     names.Add($"unicode/script/{n}/v1");
            foreach (var n in ucd.BlockEntityIds.Keys)      names.Add($"unicode/block/{n}/v1");
            foreach (var n in ucd.BidiClassEntityIds.Keys)  names.Add($"unicode/bidi_class/{n}/v1");
            foreach (var n in ucd.AgeEntityIds.Keys)        names.Add($"unicode/age/{n}/v1");
            foreach (var n in ucd.EmojiPropEntityIds.Keys)  names.Add($"unicode/emoji/{n}/v1");
            foreach (var v in ucd.NumericEntityIds.Keys)    names.Add($"unicode/numeric/{v}/v1");
            // Combining-class value entities (1..254), seeded in InitializeAsync.
            for (int cc = 1; cc <= 254; cc++)               names.Add($"unicode/combining_class/{cc}/v1");
            // Byte-tier classifiers (pass 4).
            names.Add("substrate/type/Byte/v1");
            names.Add("substrate/encoding/ISO-8859-1/v1");
            names.Add("substrate/encoding/windows-1252/v1");
            foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
                names.Add($"substrate/utf8/{role}/v1");
            return names;
        }
    }

    public ValueTask DisposeAsync() { _records = null; _ucd = null; return ValueTask.CompletedTask; }

    private SubstrateChange BuildBatch(int start, int end, bool entitiesOnly)
    {
        int n = end - start;
        string suffix = entitiesOnly ? "/entities" : "/attestations";
        var b = new SubstrateChangeBuilder(
            Source, $"codepoints/U+{start:X4}..U+{(end - 1):X4}{suffix}", null,
            entityCapacity:      entitiesOnly ? n : 0,
            physicalityCapacity: entitiesOnly ? n : 0,
            attestationCapacity: entitiesOnly ? 0 : n * 12);

        CodepointRecord[] recs = _records!;
        UcdProperties ucd = _ucd!;

        for (int cp = start; cp < end; cp++)
        {
            ref readonly CodepointRecord r = ref recs[cp];
            Hash128 entityId = r.Hash;

            if (entitiesOnly)
            {
                b.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);

                Hash128 physId = PhysicalityId.Compute(
                    entityId, Source, PhysicalityKind.Content,
                    r.CoordX, r.CoordY, r.CoordZ, r.CoordM,
                    ReadOnlySpan<double>.Empty);

                b.AddPhysicality(new PhysicalityRow(
                    Id: physId, EntityId: entityId, SourceId: Source,
                    Type: PhysicalityKind.Content,
                    CoordX: r.CoordX, CoordY: r.CoordY, CoordZ: r.CoordZ, CoordM: r.CoordM,
                    HilbertIndex: r.Hilbert,
                    TrajectoryXyzm: null, NConstituents: 0,
                    AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
            }
            else
            {
                uint ucp = (uint)cp;

                // HAS_GENERAL_CATEGORY
                string? cat = ucd.GeneralCategory[cp];
                if (cat != null && ucd.CategoryEntityIds.TryGetValue(cat, out var catId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasGeneralCategory,
                        catId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_COMBINING_CLASS (only for non-zero — zero is the default)
                if (ucd.CombiningClass[cp] > 0)
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasCombiningClass,
                        CombiningClassIds[ucd.CombiningClass[cp]], Source, null,
                        KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_SCRIPT
                string? script = ucd.ScriptForCodepoint(ucp);
                if (script != null && ucd.ScriptEntityIds.TryGetValue(script, out var scriptId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasScript,
                        scriptId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_BLOCK
                string? block = ucd.BlockForCodepoint(ucp);
                if (block != null && ucd.BlockEntityIds.TryGetValue(block, out var blockId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasBlock,
                        blockId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_UPPERCASE_MAPPING
                if (ucd.UppercaseMapping[cp] != 0)
                {
                    uint targetCp = ucd.UppercaseMapping[cp];
                    if (targetCp < (uint)recs.Length)
                        b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasUppercaseMapping,
                            recs[targetCp].Hash, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                }

                // HAS_LOWERCASE_MAPPING
                if (ucd.LowercaseMapping[cp] != 0)
                {
                    uint targetCp = ucd.LowercaseMapping[cp];
                    if (targetCp < (uint)recs.Length)
                        b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasLowercaseMapping,
                            recs[targetCp].Hash, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                }

                // CANONICAL_DECOMPOSES_TO
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
                                recs[targetCp].Hash, Source, ctx,
                                KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                        }
                    }
                }

                // ── 2026-06-05 completeness sweep ──

                // HAS_TITLECASE_MAPPING
                if (ucd.TitlecaseMapping[cp] != 0 && ucd.TitlecaseMapping[cp] < (uint)recs.Length)
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasTitlecaseMapping,
                        recs[ucd.TitlecaseMapping[cp]].Hash, Source, null,
                        KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // COMPATIBILITY_DECOMPOSES_TO — the <tag> forms, DISTINCT arena.
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
                                recs[targetCp].Hash, Source, ctx,
                                KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                        }
                    }
                }

                // HAS_NUMERIC_VALUE (ScalarValued rank via the registry)
                string? num = ucd.NumericValue[cp];
                if (num != null && ucd.NumericEntityIds.TryGetValue(num, out var numId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasNumericValue,
                        numId, Source, null, KindRank.ScalarValued, SourceTrust.StandardsDerived));

                // HAS_BIDI_CLASS
                string? bidi = ucd.BidiClass[cp];
                if (bidi != null && ucd.BidiClassEntityIds.TryGetValue(bidi, out var bidiId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasBidiClass,
                        bidiId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_MIRROR (symmetric — emit once from the lower codepoint;
                // the registry would orient anyway, this avoids the duplicate)
                uint mir = ucd.BidiMirror[cp];
                if (mir != 0 && mir < (uint)recs.Length && cp <= mir)
                    b.AddAttestation(KindRegistry.Attest(
                        entityId, "HAS_MIRROR", recs[mir].Hash, Source, SourceTrust.StandardsDerived));

                // HAS_AGE (the Unicode version that introduced the codepoint)
                string? age = ucd.AgeForCodepoint(ucp);
                if (age != null && ucd.AgeEntityIds.TryGetValue(age, out var ageId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasAge,
                        ageId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_EMOJI_PROPERTY (overlapping booleans → one arena, classifier values)
                byte eprops = ucd.EmojiProps[cp];
                if (eprops != 0)
                    for (int bit = 0; bit < UcdProperties.EmojiPropNames.Length; bit++)
                        if ((eprops & (1 << bit)) != 0
                            && ucd.EmojiPropEntityIds.TryGetValue(UcdProperties.EmojiPropNames[bit], out var epId))
                            b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasEmojiProperty,
                                epId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
            }
        }
        return b.Build();
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
