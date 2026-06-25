using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ConceptNet;

internal sealed class ConceptNetGrammarWitness : IGrammarWitness
{
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private readonly LanguageFilter? _langs;
    private readonly ArenaRmsTracker _arena = new();

    public ConceptNetGrammarWitness(LanguageFilter? langs) => _langs = langs;

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        if (!TryAssertionFields(composed.Composer, composed.Utf8,
                out var rel, out var startUri, out var endUri, out var meta))
            return;

        if (ConceptNetUri.IsExternalUrlRelation(rel))
        {
            WalkExternalUrl(b, startUri, endUri);
            return;
        }

        if (!ConceptNetRelations.TryResolveType(rel, out var typeName)) return;
        if (!ConceptNetUri.TryParseConceptUri(startUri, out var startLang, out var startTerm, out var startPos, out var startWn)) return;
        if (!ConceptNetUri.TryParseConceptUri(endUri, out var endLang, out var endTerm, out var endPos, out var endWn)) return;
        if (_langs?.MatchesAllUtf8(startLang, endLang) == false) return;

        if (!ConceptNetUri.TryAppendTerm(b, startTerm, ConceptNetDecomposer.Source, out var startId)) return;
        if (!ConceptNetUri.TryAppendTerm(b, endTerm, ConceptNetDecomposer.Source, out var endId)) return;

        Hash128 startLangId = ConceptNetLangCache.Resolve(startLang);
        Hash128 endLangId   = ConceptNetLangCache.Resolve(endLang);
        TrackLangUtf8(startLang);
        TrackLangUtf8(endLang);
        b.AddEntity(new EntityRow(startLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));
        b.AddEntity(new EntityRow(endLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));

        double weight = 1.0;
        ParseMeta(meta, ref weight, out var surfaceUtf8, out var datasetUtf8);
        _arena.Record(typeName, weight);

        // Provenance: tag the edge with the ConceptNet dataset it came from (the contributor source),
        // so the fold treats /d/wiktionary/en vs /d/conceptnet/4 as distinct evidence, not one source.
        Hash128? datasetCtx = datasetUtf8.IsEmpty
            ? null
            : ContentWitnessBatch.Emit(b, Encoding.UTF8.GetString(datasetUtf8), ConceptNetDecomposer.Source);

        b.AddAttestation(NativeAttestation.Categorical(
            startId, typeName, endId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource,
            magnitude: weight, arenaScale: _arena.Scale(typeName), contextId: datasetCtx));
        b.AddAttestation(NativeAttestation.Categorical(
            startId, "HAS_LANGUAGE", startLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));
        b.AddAttestation(NativeAttestation.Categorical(
            endId, "HAS_LANGUAGE", endLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));

        AttestPosIfPresent(b, startId, startPos);
        AttestPosIfPresent(b, endId, endPos);
        AttestSynsetBridgeIfPresent(b, startId, startPos, startWn);
        AttestSynsetBridgeIfPresent(b, endId, endPos, endWn);

        if (!surfaceUtf8.IsEmpty)
        {
            string surface = Encoding.UTF8.GetString(surfaceUtf8)
                .Replace("[[", "").Replace("]]", "").TrimStart('*', ' ').Trim();
            if (surface.Length > 0
                && ContentWitnessBatch.Emit(b, surface, ConceptNetDecomposer.Source) is { } sId)
            {
                b.AddAttestation(NativeAttestation.Categorical(
                    startId, "HAS_EXAMPLE", sId, ConceptNetDecomposer.Source,
                    SourceTrust.UserCuratedResource, contextId: endId));
            }
        }
    }

    internal static bool TryAssertionFields(
        GrammarRowComposer composer,
        ReadOnlySpan<byte> utf8,
        out ReadOnlySpan<byte> relation,
        out ReadOnlySpan<byte> startUri,
        out ReadOnlySpan<byte> endUri,
        out ReadOnlySpan<byte> metaJson)
    {
        relation = startUri = endUri = metaJson = default;
        var fields = composer.FieldSpans();
        if (fields.Count < 5) return false;
        relation  = Slice(utf8, fields[1]);
        startUri  = Slice(utf8, fields[2]);
        endUri    = Slice(utf8, fields[3]);
        metaJson  = Slice(utf8, fields[4]);
        return !relation.IsEmpty && !startUri.IsEmpty && !endUri.IsEmpty;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, (uint Start, uint End) sp) =>
        utf8[(int)sp.Start..(int)sp.End];

    private static void TrackLangUtf8(ReadOnlySpan<byte> langUtf8)
    {
        if (langUtf8.Length <= 8)
        {
            Span<char> chars = stackalloc char[8];
            int n = Encoding.UTF8.GetChars(langUtf8, chars);
            VocabularyNames.TrackLanguage(ConceptNetDecomposer.LanguageNames, new string(chars[..n]));
        }
        else
            VocabularyNames.TrackLanguage(ConceptNetDecomposer.LanguageNames, Encoding.UTF8.GetString(langUtf8));
    }

    private static void ParseMeta(ReadOnlySpan<byte> json, ref double weight,
        out ReadOnlySpan<byte> surfaceUtf8, out ReadOnlySpan<byte> datasetUtf8)
    {
        surfaceUtf8 = default;
        datasetUtf8 = default;
        if (json.IsEmpty) return;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("weight"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                        weight = reader.GetDouble();
                }
                else if (reader.ValueTextEquals("surfaceText"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                        surfaceUtf8 = reader.ValueSpan;
                }
                // dataset (e.g. "/d/wiktionary/en") is the assertion's provenance — previously dropped
                // (only weight + surfaceText were read), so every ConceptNet edge collapsed onto one
                // generic source. Capture it as the edge's context so the Glicko fold sees distinct
                // sources. ("dataset" is top-level only; nested sources[].contributor is not matched here.)
                else if (reader.ValueTextEquals("dataset"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                        datasetUtf8 = reader.ValueSpan;
                }
            }
        }
        catch (JsonException) { }
    }

    private static void WalkExternalUrl(SubstrateChangeBuilder b, ReadOnlySpan<byte> startUri, ReadOnlySpan<byte> endUri)
    {
        if (!ConceptNetUri.TryParseConceptUri(startUri, out _, out var term, out var pos, out var startWn)) return;
        if (!ConceptNetUri.TryAppendTerm(b, term, ConceptNetDecomposer.Source, out var termId)) return;
        AttestPosIfPresent(b, termId, pos);
        AttestSynsetBridgeIfPresent(b, termId, pos, startWn);

        Hash128? synId = ConceptNetUri.ResolveSynsetFromExternalUrl(endUri);
        if (synId is null) return;
        b.AddAttestation(NativeAttestation.Categorical(
            termId, "CORRESPONDS_TO", synId.Value, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));
    }

    private static void AttestSynsetBridgeIfPresent(
        SubstrateChangeBuilder b, Hash128 termId, char? pos, ReadOnlySpan<byte> wnSuffix)
    {
        Hash128? synId = ConceptNetUri.ResolveSynsetFromWnSuffix(wnSuffix, pos);
        if (synId is null) return;
        b.AddAttestation(NativeAttestation.Categorical(
            termId, "CORRESPONDS_TO", synId.Value, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));
    }

    private static void AttestPosIfPresent(SubstrateChangeBuilder b, Hash128 termId, char? pos)
    {
        if (pos is not { } p) return;
        PosReference.Attest(b, termId, p.ToString(), PosReference.PosTagset.WordNet,
            ConceptNetDecomposer.Source, null, SourceTrust.UserCuratedResource);
    }
}
