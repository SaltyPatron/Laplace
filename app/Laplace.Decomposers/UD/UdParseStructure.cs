using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.UD;

public static class UdParseStructure
{
    private const byte ParseTier = EntityTier.Document;
    private static readonly Hash128 SchemaV1 = NamedMarker("ud/parse/schema/v1");
    private static readonly Hash128 None = NamedMarker("ud/parse/none/v1");
    private static readonly Hash128 Root = NamedMarker("ud/parse/root/v1");
    private static readonly Hash128 Present = NamedMarker("ud/parse/present/v1");
    private static readonly Hash128 FeaturesEnd = NamedMarker("ud/parse/features-end/v1");
    private static readonly Hash128 EnhancedEnd = NamedMarker("ud/parse/enhanced-end/v1");
    private static readonly Hash128 MiscEnd = NamedMarker("ud/parse/misc-end/v1");
    private static readonly Hash128 TokensEnd = NamedMarker("ud/parse/tokens-end/v1");
    private static readonly Hash128 MwtEnd = NamedMarker("ud/parse/mwt-end/v1");
    private static readonly string[] MarkerNames =
    [
        "ud/parse/schema/v1", "ud/parse/none/v1", "ud/parse/root/v1",
        "ud/parse/present/v1",
        "ud/parse/features-end/v1", "ud/parse/enhanced-end/v1",
        "ud/parse/misc-end/v1", "ud/parse/tokens-end/v1", "ud/parse/mwt-end/v1",
    ];

    private static readonly ConcurrentDictionary<string, NamedAnchor> TokenRefs =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Language, string Tag), NamedAnchor> Xpos = new();
    private static readonly ConcurrentDictionary<string, NamedAnchor> MiscKeys =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Key, string Value), NamedAnchor> MiscValues = new();

    public sealed record DecodedToken(
        Hash128 RefId,
        Hash128 FormId,
        Hash128 LemmaId,
        Hash128 UposId,
        Hash128 XposId,
        IReadOnlyList<(Hash128 RelationId, Hash128 ValueId)> Features,
        Hash128 HeadRefId,
        Hash128 DeprelId,
        IReadOnlyList<(Hash128 HeadRefId, Hash128 RelationId)> Enhanced,
        IReadOnlyList<(Hash128 KeyId, Hash128 ValueId)> Misc);

    public sealed record DecodedMwt(
        Hash128 StartRefId,
        Hash128 EndRefId,
        Hash128 FormId,
        IReadOnlyList<(Hash128 KeyId, Hash128 ValueId)> Misc);

    public sealed record DecodedParse(
        Hash128 SentenceId,
        Hash128 LanguageId,
        IReadOnlyList<DecodedToken> Tokens,
        IReadOnlyList<DecodedMwt> Mwts);

    internal static Hash128 Emit(
        SubstrateChangeBuilder builder,
        UdSentence sentence,
        Hash128 languageId,
        string languageCode,
        string fileLabel,
        HashSet<Hash128> seenEntitiesThisBatch,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames,
        UdSentenceEmitContext content,
        Hash128 sourceId)
    {
        Hash128 sentenceId = sentence.TextUtf8 is { Length: > 0 }
            ? content.RootFor(sentence.TextUtf8) ?? None
            : None;
        var flat = new List<Hash128>(3 + sentence.Tokens.Count * 16 + sentence.Mwts.Count * 4)
        {
            SchemaV1,
            sentenceId,
            languageId,
        };

        DeclareMarkers(builder, sourceId, canonicalNames);
        var refIds = new Dictionary<string, Hash128>(sentence.Tokens.Count, StringComparer.Ordinal);
        foreach (UdToken token in sentence.Tokens)
            refIds[token.Ref] = DeclareTokenRef(builder, token.Ref, sourceId, canonicalNames);

        var fallbackCoords = new List<double>(sentence.Tokens.Count * 4);
        Span<double> coord = stackalloc double[4];
        foreach (UdToken token in sentence.Tokens)
        {
            if (!refIds.TryGetValue(token.Ref, out Hash128 refId)
                || content.RootFor(token.FormUtf8) is not { } formId)
                continue;

            Hash128 lemmaId = content.RootFor(token.LemmaUtf8) ?? formId;
            Hash128 uposId = ResolveUpos(builder, token.Upos, sourceId, canonicalNames);
            Hash128 xposId = ResolveXpos(
                builder, token.Xpos, languageCode, uposId, sourceId,
                seenSourceDeclarations, canonicalNames);

            flat.Add(refId);
            flat.Add(formId);
            flat.Add(lemmaId);
            flat.Add(uposId);
            flat.Add(xposId);

            var features = ResolveFeatures(
                builder, token.Feats, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames);
            foreach ((Hash128 relationId, Hash128 valueId) in features)
            {
                flat.Add(relationId);
                flat.Add(valueId);
            }
            flat.Add(FeaturesEnd);

            Hash128 headRefId = !token.HeadSpecified
                ? None
                : token.Head switch
            {
                0 => Root,
                > 0 => DeclareTokenRef(builder, token.Head.ToString(), sourceId, canonicalNames),
                _ => None,
            };
            Hash128 deprelId = ResolveDeprel(
                builder, token.Deprel, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames, enhanced: false);
            flat.Add(headRefId);
            flat.Add(deprelId);

            var enhanced = ResolveEnhanced(
                builder, token.Deps, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames);
            foreach ((Hash128 enhancedHead, Hash128 enhancedRelation) in enhanced)
            {
                flat.Add(enhancedHead);
                flat.Add(enhancedRelation);
            }
            flat.Add(EnhancedEnd);

            var misc = ResolveMisc(builder, token.Misc, content, sourceId, canonicalNames);
            foreach ((Hash128 keyId, Hash128 valueId) in misc)
            {
                flat.Add(keyId);
                flat.Add(valueId);
            }
            flat.Add(MiscEnd);

            if (content.TryRootCoord(token.FormUtf8, coord))
                for (int i = 0; i < 4; i++) fallbackCoords.Add(coord[i]);
        }
        flat.Add(TokensEnd);

        foreach (UdMwt mwt in sentence.Mwts)
        {
            if (content.RootFor(mwt.FormUtf8) is not { } formId) continue;
            flat.Add(DeclareTokenRef(builder, mwt.Start.ToString(), sourceId, canonicalNames));
            flat.Add(DeclareTokenRef(builder, mwt.End.ToString(), sourceId, canonicalNames));
            flat.Add(formId);
            foreach ((Hash128 keyId, Hash128 valueId) in
                     ResolveMisc(builder, mwt.Misc, content, sourceId, canonicalNames))
            {
                flat.Add(keyId);
                flat.Add(valueId);
            }
            flat.Add(MwtEnd);
        }

        Hash128 parseId = Hash128.Merkle(ParseTier, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(flat));
        builder.AddEntity(parseId, ParseTier, EntityTypeRegistry.UdParse, sourceId);

        Span<double> parseCoord = stackalloc double[4];
        bool hasCoord = sentence.TextUtf8 is { Length: > 0 }
            && content.TryRootCoord(sentence.TextUtf8, parseCoord);
        if (!hasCoord && fallbackCoords.Count >= 4)
        {
            double[] mean = Math4d.KarcherMean(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fallbackCoords));
            mean.AsSpan(0, 4).CopyTo(parseCoord);
            hasCoord = true;
        }
        if (!hasCoord)
            throw new InvalidOperationException("UD parse has no sentence or token placement");

        Hash128 physicalityId = PhysicalityId.Compute(parseId, PhysicalityType.Content);
        if (builder.TrySeePhysicality(physicalityId))
            builder.AddPhysicalityPreSeen(new PhysicalityRow(
                physicalityId,
                parseId,
                sourceId,
                PhysicalityType.Content,
                parseCoord[0], parseCoord[1], parseCoord[2], parseCoord[3],
                Hilbert128.Encode(parseCoord),
                Trajectory.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(flat)),
                flat.Count,
                null,
                null,
                0));

        Hash128 occurrenceId = OccurrenceId(
            sourceId, parseId, fileLabel, sentence.SourceOrdinal, sentence.SourceSentenceId);
        builder.AddEntity(
            occurrenceId, EntityTier.Document, EntityTypeRegistry.UdParseOccurrence, sourceId);
        Hash128 subjectId = sentenceId == None ? occurrenceId : sentenceId;
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            subjectId,
            UDSource.HasParseTypeId,
            parseId,
            sourceId,
            occurrenceId,
            SourceTrust.AcademicCurated));
        return parseId;
    }

    public static bool TryDecode(ReadOnlySpan<Hash128> flat, out DecodedParse? parse)
    {
        parse = null;
        if (flat.Length < 4 || flat[0] != SchemaV1) return false;
        Hash128 sentenceId = flat[1];
        Hash128 languageId = flat[2];
        int i = 3;
        var tokens = new List<DecodedToken>();
        while (i < flat.Length && flat[i] != TokensEnd)
        {
            if (i + 5 > flat.Length) return false;
            Hash128 refId = flat[i++];
            Hash128 formId = flat[i++];
            Hash128 lemmaId = flat[i++];
            Hash128 uposId = flat[i++];
            Hash128 xposId = flat[i++];

            if (!TryReadPairs(flat, ref i, FeaturesEnd, out var features)) return false;
            if (i + 2 > flat.Length) return false;
            Hash128 headRefId = flat[i++];
            Hash128 deprelId = flat[i++];
            if (!TryReadPairs(flat, ref i, EnhancedEnd, out var enhanced)) return false;
            if (!TryReadPairs(flat, ref i, MiscEnd, out var misc)) return false;

            tokens.Add(new DecodedToken(
                refId, formId, lemmaId, uposId, xposId,
                features, headRefId, deprelId, enhanced, misc));
        }
        if (i >= flat.Length || flat[i++] != TokensEnd) return false;

        var mwts = new List<DecodedMwt>();
        while (i < flat.Length)
        {
            if (i + 3 > flat.Length) return false;
            Hash128 startRefId = flat[i++];
            Hash128 endRefId = flat[i++];
            Hash128 formId = flat[i++];
            if (!TryReadPairs(flat, ref i, MwtEnd, out var misc)) return false;
            mwts.Add(new DecodedMwt(startRefId, endRefId, formId, misc));
        }
        parse = new DecodedParse(sentenceId, languageId, tokens, mwts);
        return true;
    }

    internal static Hash128 TokenRefId(string tokenRef) => TokenRef(tokenRef).Id;

    internal static Hash128 XposId(string languageCode, string xpos) =>
        XposAnchor(languageCode, xpos).Id;

    internal static Hash128 MiscKeyId(string key) => MiscKey(key).Id;

    internal static Hash128 MiscOpaqueValueId(string key, string value) =>
        MiscValue(key, value).Id;

    internal static Hash128 NoneId => None;
    internal static Hash128 RootId => Root;
    internal static Hash128 PresentId => Present;

    private static bool TryReadPairs(
        ReadOnlySpan<Hash128> flat,
        ref int i,
        Hash128 terminator,
        out List<(Hash128, Hash128)> pairs)
    {
        pairs = [];
        while (i < flat.Length && flat[i] != terminator)
        {
            if (i + 2 > flat.Length) return false;
            pairs.Add((flat[i], flat[i + 1]));
            i += 2;
        }
        if (i >= flat.Length || flat[i++] != terminator) return false;
        return true;
    }

    private static void DeclareMarkers(
        SubstrateChangeBuilder builder,
        Hash128 sourceId,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        foreach (string name in MarkerNames)
        {
            Hash128 id = NamedMarker(name);
            builder.AddEntity(id, EntityTier.Word, EntityTypeRegistry.UdAnnotationMarker, sourceId);
            VocabularyNames.Track(canonicalNames, name);
        }
    }

    private static Hash128 DeclareTokenRef(
        SubstrateChangeBuilder builder,
        string tokenRef,
        Hash128 sourceId,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        NamedAnchor anchor = TokenRef(tokenRef);
        builder.AddEntity(anchor.Id, EntityTier.Word, EntityTypeRegistry.UdTokenRef, sourceId);
        VocabularyNames.Track(canonicalNames, anchor.Name);
        return anchor.Id;
    }

    private static Hash128 ResolveUpos(
        SubstrateChangeBuilder builder,
        string upos,
        Hash128 sourceId,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        if (string.IsNullOrWhiteSpace(upos) || upos == "_") return None;
        Hash128 id = PosReference.Resolve(upos, PosReference.PosTagset.Upos, out bool probationary);
        builder.AddEntity(id, EntityTier.Word, PosReference.PosTypeId, sourceId);
        VocabularyNames.TrackProbationaryPos(
            canonicalNames, upos, PosReference.PosTagset.Upos, probationary);
        return id;
    }

    private static Hash128 ResolveXpos(
        SubstrateChangeBuilder builder,
        string xpos,
        string languageCode,
        Hash128 uposId,
        Hash128 sourceId,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        if (string.IsNullOrWhiteSpace(xpos) || xpos == "_") return None;
        NamedAnchor anchor = XposAnchor(languageCode, xpos);
        builder.AddEntity(anchor.Id, EntityTier.Word, EntityTypeRegistry.UdXpos, sourceId);
        VocabularyNames.Track(canonicalNames, anchor.Name);
        if (uposId != None)
        {
            AttestationRow mapping = NativeAttestation.CategoricalResolved(
                anchor.Id,
                UDSource.IsATypeId,
                uposId,
                sourceId,
                null,
                SourceTrust.AcademicCurated);
            if (seenSourceDeclarations.Add(mapping.Id)) builder.AddAttestation(mapping);
        }
        return anchor.Id;
    }

    private static List<(Hash128 RelationId, Hash128 ValueId)> ResolveFeatures(
        SubstrateChangeBuilder builder,
        string[] features,
        Hash128 sourceId,
        HashSet<Hash128> seenEntitiesThisBatch,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        var resolved = new List<(Hash128, Hash128)>(features.Length);
        foreach (string feature in features)
        {
            if (!RelationTypeRegistry.ParseFeature(feature, out string name, out string value)) continue;
            VocabularyNames.TrackUdFeatureValue(canonicalNames, name, value);
            Hash128 valueId = HighwayNodeEmitter.Emit(
                builder,
                $"{name}={value}",
                EntityTypeRegistry.UdFeature,
                sourceId,
                SourceTrust.AcademicCurated,
                seenEntitiesThisBatch,
                readbackNames: canonicalNames);
            RelationTypeRegistry.RelationTypeResolution relation =
                RelationTypeRegistry.ResolveFeature(name);
            RelationTypeRegistry.SeedDynamic(
                builder, relation, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames);
            resolved.Add((relation.Id, valueId));
        }
        SortAndDeduplicate(resolved);
        return resolved;
    }

    private static Hash128 ResolveDeprel(
        SubstrateChangeBuilder builder,
        string relation,
        Hash128 sourceId,
        HashSet<Hash128> seenEntitiesThisBatch,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames,
        bool enhanced)
    {
        if (string.IsNullOrWhiteSpace(relation) || relation == "_") return None;
        if (enhanced)
        {
            RelationTypeRegistry.SeedEnhancedDeprel(
                builder, relation, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames);
            return RelationTypeRegistry.ResolveEnhancedDeprel(relation).Id;
        }
        RelationTypeRegistry.SeedDeprel(
            builder, relation, sourceId, seenEntitiesThisBatch,
            seenSourceDeclarations, canonicalNames);
        return RelationTypeRegistry.ResolveDeprel(relation).Id;
    }

    private static List<(Hash128 HeadRefId, Hash128 RelationId)> ResolveEnhanced(
        SubstrateChangeBuilder builder,
        string deps,
        Hash128 sourceId,
        HashSet<Hash128> seenEntitiesThisBatch,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        var resolved = new List<(Hash128, Hash128)>();
        if (string.IsNullOrWhiteSpace(deps) || deps == "_") return resolved;
        foreach (string edge in deps.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = edge.IndexOf(':');
            if (colon <= 0 || colon >= edge.Length - 1) continue;
            string head = edge[..colon].Trim();
            string relation = edge[(colon + 1)..].Trim();
            if (head.Length == 0 || relation.Length == 0) continue;
            Hash128 headId = head == "0"
                ? Root
                : DeclareTokenRef(builder, head, sourceId, canonicalNames);
            Hash128 relationId = ResolveDeprel(
                builder, relation, sourceId, seenEntitiesThisBatch,
                seenSourceDeclarations, canonicalNames, enhanced: true);
            resolved.Add((headId, relationId));
        }
        SortAndDeduplicate(resolved);
        return resolved;
    }

    private static List<(Hash128 KeyId, Hash128 ValueId)> ResolveMisc(
        SubstrateChangeBuilder builder,
        string misc,
        UdSentenceEmitContext content,
        Hash128 sourceId,
        ConcurrentDictionary<string, byte> canonicalNames)
    {
        var resolved = new List<(Hash128, Hash128)>();
        if (string.IsNullOrWhiteSpace(misc) || misc == "_") return resolved;
        foreach (string item in misc.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = item.IndexOf('=');
            string key = (equals < 0 ? item : item[..equals]).Trim();
            string value = equals < 0 ? string.Empty : item[(equals + 1)..].Trim();
            if (key.Length == 0) continue;

            NamedAnchor keyAnchor = MiscKey(key);
            builder.AddEntity(
                keyAnchor.Id, EntityTier.Word, EntityTypeRegistry.UdAnnotationMarker, sourceId);
            VocabularyNames.Track(canonicalNames, keyAnchor.Name);

            Hash128 valueId;
            if (equals < 0)
            {
                valueId = Present;
            }
            else if (key.Equals("Gloss", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
            {
                valueId = content.RootFor(Encoding.UTF8.GetBytes(value)) ?? None;
            }
            else if (key.Equals("Lang", StringComparison.OrdinalIgnoreCase))
            {
                valueId = LanguageReference.Resolve(value);
                builder.AddEntity(valueId, EntityTier.Word, EntityTypeRegistry.Language, sourceId);
            }
            else
            {
                NamedAnchor valueAnchor = MiscValue(key, value);
                valueId = valueAnchor.Id;
                builder.AddEntity(
                    valueId, EntityTier.Word, EntityTypeRegistry.UdAnnotationValue, sourceId);
                VocabularyNames.Track(canonicalNames, valueAnchor.Name);
            }
            resolved.Add((keyAnchor.Id, valueId));
        }
        SortAndDeduplicate(resolved);
        return resolved;
    }

    private static void SortAndDeduplicate(List<(Hash128 Left, Hash128 Right)> values)
    {
        values.Sort(static (a, b) =>
        {
            int c = a.Left.CompareToBytewise(b.Left);
            return c != 0 ? c : a.Right.CompareToBytewise(b.Right);
        });
        int write = 0;
        for (int read = 0; read < values.Count; read++)
        {
            if (write > 0 && values[read] == values[write - 1]) continue;
            values[write++] = values[read];
        }
        if (write < values.Count) values.RemoveRange(write, values.Count - write);
    }

    private static Hash128 OccurrenceId(
        Hash128 sourceId,
        Hash128 parseId,
        string fileLabel,
        long sourceOrdinal,
        string? sourceSentenceId)
    {
        ReadOnlySpan<byte> domain = "laplace/ud-parse-occurrence/v1\0"u8;
        int fileBytes = Encoding.UTF8.GetByteCount(fileLabel);
        int sentenceBytes = Encoding.UTF8.GetByteCount(sourceSentenceId ?? string.Empty);
        int length = domain.Length + 32 + sizeof(int) + fileBytes + sizeof(long)
            + sizeof(int) + sentenceBytes;
        byte[]? rented = null;
        Span<byte> preimage = length <= 512
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            domain.CopyTo(preimage);
            int cursor = domain.Length;
            sourceId.WriteBytes(preimage[cursor..]);
            cursor += 16;
            parseId.WriteBytes(preimage[cursor..]);
            cursor += 16;
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], fileBytes);
            cursor += sizeof(int);
            cursor += Encoding.UTF8.GetBytes(fileLabel, preimage[cursor..]);
            BinaryPrimitives.WriteInt64LittleEndian(preimage[cursor..], sourceOrdinal);
            cursor += sizeof(long);
            BinaryPrimitives.WriteInt32LittleEndian(preimage[cursor..], sentenceBytes);
            cursor += sizeof(int);
            if (sourceSentenceId is not null)
                Encoding.UTF8.GetBytes(sourceSentenceId, preimage[cursor..]);
            return Hash128.Blake3(preimage);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static NamedAnchor TokenRef(string tokenRef) =>
        TokenRefs.GetOrAdd(tokenRef, static value => Named(
            "ud/token-ref/", value));

    private static NamedAnchor XposAnchor(string languageCode, string xpos) =>
        Xpos.GetOrAdd((languageCode, xpos), static value => Named(
            $"ud/xpos/{value.Language}/", value.Tag));

    private static NamedAnchor MiscKey(string key) =>
        MiscKeys.GetOrAdd(key, static value => Named("ud/misc-key/", value));

    private static NamedAnchor MiscValue(string key, string value) =>
        MiscValues.GetOrAdd((key, value), static item => Named(
            $"ud/misc-value/{Convert.ToHexString(Encoding.UTF8.GetBytes(item.Key))}/",
            item.Value));

    private static NamedAnchor Named(string prefix, string value)
    {
        string encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(value));
        string name = $"{prefix}{encoded}/v1";
        return new NamedAnchor(Hash128.OfCanonical(name), name);
    }

    private static Hash128 NamedMarker(string name) => Hash128.OfCanonical(name);

    private readonly record struct NamedAnchor(Hash128 Id, string Name);
}
