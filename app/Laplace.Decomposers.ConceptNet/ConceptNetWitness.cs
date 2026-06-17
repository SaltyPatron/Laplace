using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;

internal sealed class ConceptNetWitness
{
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private readonly ArenaRmsTracker _arena;
    private readonly LanguageFilter? _langs;
    private readonly HashSet<Hash128> _seenEntBatch = new();
    private readonly ConcurrentIdSet _seenAttRun = new();

    public ConceptNetWitness(ArenaRmsTracker arena, LanguageFilter? langs = null)
    {
        _arena = arena;
        _langs = langs;
    }

    internal void WalkAssertion(in ConceptNetTsvRow row, SubstrateChangeBuilder b)
    {
        string rel = row.RelationText();
        if (rel.StartsWith("/r/", StringComparison.Ordinal)) rel = rel[3..];

        RelationTypeRegistry.RelationTypeResolution? dbp = null;
        string typeName;
        if (ConceptNetDecomposer.RelMap.TryGetValue(rel, out var mapped))
            typeName = mapped;
        else if (rel.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase))
        {
            var r = RelationTypeRegistry.ResolveDbpedia(rel);
            dbp = r;
            typeName = r.Canonical;
        }
        else return;

        if (!ConceptNetUri.TryParseLangAndTerm(row.StartUri, out var startLang, out var startTerm)) return;
        if (!ConceptNetUri.TryParseLangAndTerm(row.EndUri, out var endLang, out var endTerm)) return;
        if (_langs?.MatchesAll(startLang, endLang) == false) return;

        if (!ConceptNetUri.TryAppendTerm(b, startTerm, ConceptNetDecomposer.Source, out var startId)) return;
        if (!ConceptNetUri.TryAppendTerm(b, endTerm, ConceptNetDecomposer.Source, out var endId)) return;

        Hash128 startLangId = LanguageReference.Resolve(startLang);
        Hash128 endLangId   = LanguageReference.Resolve(endLang);
        b.AddEntity(new EntityRow(startLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));
        b.AddEntity(new EntityRow(endLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));

        (double weight, string? surface) = ParseMeta(row.MetaJson);
        _arena.Record(typeName, weight);

        if (dbp is { } dyn)
            RelationTypeRegistry.SeedDynamic(b, dyn, ConceptNetDecomposer.Source, _seenEntBatch, _seenAttRun);

        b.AddAttestation(NativeAttestation.Categorical(
            startId, typeName, endId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource,
            magnitude: weight, arenaScale: _arena.Scale(typeName)));
        b.AddAttestation(NativeAttestation.Categorical(
            startId, "HAS_LANGUAGE", startLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));
        b.AddAttestation(NativeAttestation.Categorical(
            endId, "HAS_LANGUAGE", endLangId, ConceptNetDecomposer.Source, SourceTrust.UserCuratedResource));

        if (surface is { Length: > 0 }
            && ContentWitnessBatch.Emit(b, surface, ConceptNetDecomposer.Source) is { } sId)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                startId, "HAS_EXAMPLE", sId, ConceptNetDecomposer.Source,
                SourceTrust.UserCuratedResource, contextId: endId));
        }
    }

    private static (double Weight, string? Surface) ParseMeta(ReadOnlySpan<byte> json)
    {
        double weight = 1.0;
        string? surface = null;
        if (json.IsEmpty) return (weight, surface);
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
                    {
                        var txt = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(txt))
                            surface = txt!.Replace("[[", "").Replace("]]", "").TrimStart('*', ' ').Trim();
                    }
                }
            }
        }
        catch (JsonException) { }
        return (weight, string.IsNullOrEmpty(surface) ? null : surface);
    }
}
