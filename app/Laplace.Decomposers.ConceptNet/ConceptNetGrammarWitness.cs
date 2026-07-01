using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;

internal sealed class ConceptNetGrammarWitness : IGrammarWitness
{
    private readonly LanguageFilter? _langs;

    public ConceptNetGrammarWitness(LanguageFilter? langs) => _langs = langs;

    public string ModalityId => "tsv";

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        if (!TryAssertionFields(composed.Composer, composed.Utf8,
                out var rel, out var startUri, out var endUri, out var meta))
            return;
        if (ConceptNetUri.IsExternalUrlRelation(rel)) return;
        if (!ConceptNetRelations.TryResolveType(rel, out var typeName)) return;
        if (!ConceptNetUri.TryParseConceptUri(startUri, out var startLang, out var startTerm, out _, out _)) return;
        if (!ConceptNetUri.TryParseConceptUri(endUri, out var endLang, out var endTerm, out _, out _)) return;
        if (_langs?.MatchesAllUtf8(startLang, endLang) == false) return;
        if (!ConceptNetUri.TryAppendTerm(b, startTerm, ConceptNetDecomposer.Source, out var startId)) return;
        if (!ConceptNetUri.TryAppendTerm(b, endTerm, ConceptNetDecomposer.Source, out var endId)) return;

        b.AddAttestation(NativeAttestation.Categorical(
            startId, typeName, endId, ConceptNetDecomposer.Source,
            SourceTrust.UserCuratedResource, magnitude: ParseWeight(meta), arenaScale: 1.0));
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
        relation = Slice(utf8, fields[1]);
        startUri = Slice(utf8, fields[2]);
        endUri = Slice(utf8, fields[3]);
        metaJson = Slice(utf8, fields[4]);
        return !relation.IsEmpty && !startUri.IsEmpty && !endUri.IsEmpty;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, (uint Start, uint End) sp) =>
        utf8[(int)sp.Start..(int)sp.End];

    private static double ParseWeight(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return 1.0;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("weight"u8))
                    return reader.Read() && reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : 1.0;
        }
        catch (JsonException) { }
        return 1.0;
    }
}
