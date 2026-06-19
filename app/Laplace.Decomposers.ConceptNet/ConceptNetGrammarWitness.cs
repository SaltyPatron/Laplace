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

        if (!ConceptNetRelations.TryResolveType(rel, out var typeName)) return;
        if (!ConceptNetUri.TryParseLangAndTerm(startUri, out var startLang, out var startTerm)) return;
        if (!ConceptNetUri.TryParseLangAndTerm(endUri, out var endLang, out var endTerm)) return;
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
        ParseMeta(meta, ref weight, out var surfaceUtf8);
        _arena.Record(typeName, weight);

        b.AddAttestation(NativeAttestation.Categorical(
            startId, typeName, endId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource,
            magnitude: weight, arenaScale: _arena.Scale(typeName)));
        b.AddAttestation(NativeAttestation.Categorical(
            startId, "HAS_LANGUAGE", startLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));
        b.AddAttestation(NativeAttestation.Categorical(
            endId, "HAS_LANGUAGE", endLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));

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

    private static void ParseMeta(ReadOnlySpan<byte> json, ref double weight, out ReadOnlySpan<byte> surfaceUtf8)
    {
        surfaceUtf8 = default;
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
            }
        }
        catch (JsonException) { }
    }
}
