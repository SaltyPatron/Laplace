using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;

internal sealed class ConceptNetWitness : IGrammarWitness
{
    public string ModalityId => "tsv";

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

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx, SubstrateChangeBuilder b)
    {
        if (composed.Composer is null) return;
        var fields = composed.Composer.FieldSpans();
        if (fields.Count < 5) return;

        var row = new ConceptNetTsvRow
        {
            Relation = composed.Utf8.AsSpan((int)fields[1].Start, (int)(fields[1].End - fields[1].Start)),
            StartUri = composed.Utf8.AsSpan((int)fields[2].Start, (int)(fields[2].End - fields[2].Start)),
            EndUri   = composed.Utf8.AsSpan((int)fields[3].Start, (int)(fields[3].End - fields[3].Start)),
            MetaJson = composed.Utf8.AsSpan((int)fields[4].Start, (int)(fields[4].End - fields[4].Start)),
        };
        WalkAssertion(row, b);
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

        if (!ParseConcept(row.StartUriText(), out var startTerm, out var startLang)) return;
        if (!ParseConcept(row.EndUriText(), out var endTerm, out var endLang)) return;
        if (_langs?.MatchesAll(startLang, endLang) == false) return;

        if (ContentEmitter.Emit(b, startTerm, ConceptNetDecomposer.Source) is not { } startId) return;
        if (ContentEmitter.Emit(b, endTerm, ConceptNetDecomposer.Source) is not { } endId) return;

        Hash128 startLangId = LanguageReference.Resolve(startLang);
        Hash128 endLangId   = LanguageReference.Resolve(endLang);
        b.AddEntity(new EntityRow(startLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));
        b.AddEntity(new EntityRow(endLangId, EntityTier.Vocabulary, LanguageTypeId, ConceptNetDecomposer.Source));

        (double weight, string? surface) = ParseMeta(row.MetaText());
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

        if (surface is not null && ContentEmitter.Emit(b, surface, ConceptNetDecomposer.Source) is { } sId)
        {
            b.AddAttestation(NativeAttestation.Categorical(
                startId, "HAS_EXAMPLE", sId, ConceptNetDecomposer.Source,
                SourceTrust.UserCuratedResource, contextId: endId));
        }
    }

    private static string FieldText(in GrammarComposeContext ctx, (uint Start, uint End) span) =>
        Encoding.UTF8.GetString(ctx.Utf8.AsSpan((int)span.Start, (int)(span.End - span.Start)));

    private static bool ParseConcept(string uri, out string term, out string lang)
    {
        term = ""; lang = "";
        var p = uri.Split('/');
        if (p.Length < 4 || p[1] != "c") return false;
        lang = p[2];
        term = p[3].Replace('_', ' ').Trim();
        return term.Length > 0;
    }

    private static (double Weight, string? Surface) ParseMeta(string json)
    {
        double weight = 1.0; string? surface = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number)
                weight = w.GetDouble();
            if (root.TryGetProperty("surfaceText", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var txt = s.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                    surface = txt!.Replace("[[", "").Replace("]]", "").TrimStart('*', ' ').Trim();
            }
        }
        catch (JsonException) { }
        return (weight, string.IsNullOrEmpty(surface) ? null : surface);
    }
}
