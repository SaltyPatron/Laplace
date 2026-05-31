using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

namespace Laplace.Decomposers.ConceptNet;

/// <summary>
/// Emits the ConceptNet 5.7 multilingual commonsense graph as content + attestations.
///
/// assertions.csv columns: URI ⇥ /r/Relation ⇥ /c/lang/start ⇥ /c/lang/end ⇥ JSON{weight,
/// surfaceText, …}. Concept terms (/c/&lt;lang&gt;/&lt;term&gt;) are decomposed as content so
/// they converge with the lexical/model/prompt layers; the relation becomes a typed kind; the
/// JSON <c>weight</c> seeds the Glicko μ (CreateWeighted); <c>surfaceText</c> (the natural-language
/// form, ~50-60% of rows) is emitted as content and HAS_EXAMPLE-linked to the start concept.
///
/// Two-pass (the graph is cyclic — concepts reference each other): pass 1 emits concept/surface
/// content + language entities; pass 2 emits the relation/HAS_LANGUAGE/HAS_EXAMPLE attestations.
/// </summary>
public sealed class ConceptNetDecomposer : RelationTripleDecomposerBase
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/ConceptNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserCuratedResource/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

    private static Hash128 Kind(string n) => Hash128.OfCanonical($"substrate/kind/{n}/v1");
    private static readonly Hash128 KindHasLanguage = Kind("HAS_LANGUAGE");
    private static readonly Hash128 KindHasExample  = Kind("HAS_EXAMPLE");

    // ConceptNet /r/Relation → (kind name, value tier).
    private static readonly (string Cn, string Kind, KindValueTier Tier)[] RelDefs =
    {
        ("RelatedTo", "RELATED_TO", KindValueTier.T8), ("FormOf", "FORM_OF", KindValueTier.T6),
        ("IsA", "IS_A", KindValueTier.T3), ("PartOf", "IS_PART_OF", KindValueTier.T4),
        ("HasA", "HAS_A", KindValueTier.T4), ("UsedFor", "USED_FOR", KindValueTier.T8),
        ("CapableOf", "CAPABLE_OF", KindValueTier.T8), ("AtLocation", "AT_LOCATION", KindValueTier.T8),
        ("Causes", "CAUSES", KindValueTier.T5), ("HasSubevent", "HAS_SUBEVENT", KindValueTier.T5),
        ("HasFirstSubevent", "HAS_FIRST_SUBEVENT", KindValueTier.T5),
        ("HasLastSubevent", "HAS_LAST_SUBEVENT", KindValueTier.T5),
        ("HasPrerequisite", "HAS_PREREQUISITE", KindValueTier.T5),
        ("HasProperty", "HAS_PROPERTY", KindValueTier.T8),
        ("MotivatedByGoal", "MOTIVATED_BY_GOAL", KindValueTier.T5),
        ("ObstructedBy", "OBSTRUCTED_BY", KindValueTier.T5), ("Desires", "DESIRES", KindValueTier.T5),
        ("CreatedBy", "CREATED_BY", KindValueTier.T5), ("Synonym", "IS_SYNONYM_OF", KindValueTier.T6),
        ("Antonym", "IS_ANTONYM_OF", KindValueTier.T7), ("DistinctFrom", "DISTINCT_FROM", KindValueTier.T7),
        ("DerivedFrom", "DERIVED_FROM", KindValueTier.T6), ("SymbolOf", "SYMBOL_OF", KindValueTier.T8),
        ("DefinedAs", "DEFINED_AS", KindValueTier.T6), ("MannerOf", "MANNER_OF", KindValueTier.T4),
        ("LocatedNear", "LOCATED_NEAR", KindValueTier.T8), ("HasContext", "HAS_CONTEXT", KindValueTier.T8),
        ("SimilarTo", "SIMILAR_TO", KindValueTier.T6),
        ("EtymologicallyRelatedTo", "ETYMOLOGICALLY_RELATED_TO", KindValueTier.T6),
        ("EtymologicallyDerivedFrom", "ETYMOLOGICALLY_DERIVED_FROM", KindValueTier.T6),
        ("CausesDesire", "CAUSES_DESIRE", KindValueTier.T5), ("MadeOf", "MADE_OF", KindValueTier.T4),
        ("ReceivesAction", "RECEIVES_ACTION", KindValueTier.T8), ("InstanceOf", "IS_INSTANCE_OF", KindValueTier.T3),
        ("NotDesires", "NOT_DESIRES", KindValueTier.T7), ("NotUsedFor", "NOT_USED_FOR", KindValueTier.T7),
        ("NotCapableOf", "NOT_CAPABLE_OF", KindValueTier.T7), ("NotHasProperty", "NOT_HAS_PROPERTY", KindValueTier.T7),
        ("Entails", "ENTAILS", KindValueTier.T5),
    };

    private static readonly Dictionary<string, (Hash128 Kind, KindValueTier Tier)> RelMap =
        RelDefs.ToDictionary(r => r.Cn, r => (Kind(r.Kind), r.Tier));

    public override Hash128 SourceId     => Source;
    public override string  SourceName   => "ConceptNetDecomposer";
    public override int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public override Hash128 TrustClassId => TrustClass;

    protected override bool RequiresTwoPass => false;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddKind("HAS_EXAMPLE", KindValueTier.T4, TC.UserCuratedResourceTier6);
        foreach (var (_, kindName, tier) in RelDefs)
            boot.AddKind(kindName, tier, TC.UserCuratedResourceTier6);
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(34_074_917L);

    protected override async IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string file = Path.Combine(ecosystemPath, "assertions.csv");
        if (!File.Exists(file)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        var b = NewBuilder("conceptnet/batch-0", batch);
        int n = 0, bn = 0;

        await foreach (var line in File.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            var c = line.Split('\t');
            if (c.Length < 5) continue;

            string rel = c[1].StartsWith("/r/", StringComparison.Ordinal) ? c[1][3..] : c[1];
            if (!RelMap.TryGetValue(rel, out var rk)) continue;
            if (!ParseConcept(c[2], out string startTerm, out string startLang)) continue;
            if (!ParseConcept(c[3], out string endTerm, out string endLang)) continue;

            // Single pass: emit both concepts' content + the attestation referencing them in
            // one intent (writer orders entities before attestations) — concepts decoded once,
            // even though the graph is cyclic (a repeat reference just re-emits, ON CONFLICT).
            var startId = ContentEmitter.Emit(b, startTerm, Source);
            var endId   = ContentEmitter.Emit(b, endTerm, Source);
            if (startId is null || endId is null) continue;

            Hash128 startLangId = LanguageEntityId.FromIso639_3(startLang);
            Hash128 endLangId   = LanguageEntityId.FromIso639_3(endLang);
            b.AddEntity(new EntityRow(startLangId, 2, LanguageTypeId, Source));
            b.AddEntity(new EntityRow(endLangId, 2, LanguageTypeId, Source));

            (double weight, string? surface) = ParseMeta(c[4]);

            b.AddAttestation(AttestationFactory.CreateWeighted(
                startId.Value, rk.Kind, endId.Value, Source, null,
                rk.Tier, TC.UserCuratedResourceTier6, magnitude: weight, floor: 1.0));
            b.AddAttestation(AttestationFactory.Create(
                startId.Value, KindHasLanguage, startLangId, Source, null,
                KindValueTier.T4, TC.UserCuratedResourceTier6));
            b.AddAttestation(AttestationFactory.Create(
                endId.Value, KindHasLanguage, endLangId, Source, null,
                KindValueTier.T4, TC.UserCuratedResourceTier6));
            if (surface is not null)
            {
                var sId = ContentEmitter.Emit(b, surface, Source);
                if (sId is not null)
                    b.AddAttestation(AttestationFactory.Create(
                        startId.Value, KindHasExample, sId.Value, Source, endId.Value,
                        KindValueTier.T4, TC.UserCuratedResourceTier6));
            }

            if (++n >= batch)
            {
                yield return b.Build();
                b = NewBuilder($"conceptnet/batch-{++bn}", batch);
                n = 0; await Task.Yield();
            }
        }
        if (n > 0) yield return b.Build();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 12,
            physicalityCapacity: batch * 12,
            attestationCapacity: batch * 4);

    /// <summary>/c/&lt;lang&gt;/&lt;term&gt;[/pos[/sense]] → (term with spaces, lang).</summary>
    private static bool ParseConcept(string uri, out string term, out string lang)
    {
        term = ""; lang = "";
        var p = uri.Split('/'); // ["", "c", lang, term, ...]
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
        catch (JsonException) { /* tolerate malformed metadata; keep defaults */ }
        return (weight, string.IsNullOrEmpty(surface) ? null : surface);
    }
}
