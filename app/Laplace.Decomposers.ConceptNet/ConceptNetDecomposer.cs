using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

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
    private static readonly (string Cn, string Kind, double Tier)[] RelDefs =
    {
        ("RelatedTo", "RELATED_TO", KindRank.Associative), ("FormOf", "FORM_OF", KindRank.Equivalence),
        ("IsA", "IS_A", KindRank.Taxonomic), ("PartOf", "IS_PART_OF", KindRank.Partitive),
        ("HasA", "HAS_A", KindRank.Partitive), ("UsedFor", "USED_FOR", KindRank.Associative),
        ("CapableOf", "CAPABLE_OF", KindRank.Associative), ("AtLocation", "AT_LOCATION", KindRank.Associative),
        ("Causes", "CAUSES", KindRank.Causal), ("HasSubevent", "HAS_SUBEVENT", KindRank.Causal),
        ("HasFirstSubevent", "HAS_FIRST_SUBEVENT", KindRank.Causal),
        ("HasLastSubevent", "HAS_LAST_SUBEVENT", KindRank.Causal),
        ("HasPrerequisite", "HAS_PREREQUISITE", KindRank.Causal),
        ("HasProperty", "HAS_PROPERTY", KindRank.Associative),
        ("MotivatedByGoal", "MOTIVATED_BY_GOAL", KindRank.Causal),
        ("ObstructedBy", "OBSTRUCTED_BY", KindRank.Causal), ("Desires", "DESIRES", KindRank.Causal),
        ("CreatedBy", "CREATED_BY", KindRank.Causal), ("Synonym", "IS_SYNONYM_OF", KindRank.Equivalence),
        ("Antonym", "IS_ANTONYM_OF", KindRank.Oppositional), ("DistinctFrom", "DISTINCT_FROM", KindRank.Oppositional),
        ("DerivedFrom", "DERIVED_FROM", KindRank.Equivalence), ("SymbolOf", "SYMBOL_OF", KindRank.Associative),
        ("DefinedAs", "DEFINED_AS", KindRank.Equivalence), ("MannerOf", "MANNER_OF", KindRank.Partitive),
        ("LocatedNear", "LOCATED_NEAR", KindRank.Associative), ("HasContext", "HAS_CONTEXT", KindRank.Associative),
        ("SimilarTo", "SIMILAR_TO", KindRank.Equivalence),
        ("EtymologicallyRelatedTo", "ETYMOLOGICALLY_RELATED_TO", KindRank.Equivalence),
        ("EtymologicallyDerivedFrom", "ETYMOLOGICALLY_DERIVED_FROM", KindRank.Equivalence),
        ("CausesDesire", "CAUSES_DESIRE", KindRank.Causal), ("MadeOf", "MADE_OF", KindRank.Partitive),
        ("ReceivesAction", "RECEIVES_ACTION", KindRank.Associative), ("InstanceOf", "IS_INSTANCE_OF", KindRank.Taxonomic),
        ("NotDesires", "NOT_DESIRES", KindRank.Oppositional), ("NotUsedFor", "NOT_USED_FOR", KindRank.Oppositional),
        ("NotCapableOf", "NOT_CAPABLE_OF", KindRank.Oppositional), ("NotHasProperty", "NOT_HAS_PROPERTY", KindRank.Oppositional),
        ("Entails", "ENTAILS", KindRank.Causal),
    };

    private static readonly Dictionary<string, (Hash128 Kind, double Tier)> RelMap =
        RelDefs.ToDictionary(r => r.Cn, r => (Kind(r.Kind), r.Tier));

    public override Hash128 SourceId     => Source;
    public override string  SourceName   => "ConceptNetDecomposer";
    public override int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public override Hash128 TrustClassId => TrustClass;

    protected override bool RequiresTwoPass => false;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddKind("HAS_EXAMPLE", KindRank.Partitive, SourceTrust.UserCuratedResource);
        foreach (var (_, kindName, tier) in RelDefs)
            boot.AddKind(kindName, tier, SourceTrust.UserCuratedResource);
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

            Hash128 startLangId = LanguageReference.Resolve(startLang);
            Hash128 endLangId   = LanguageReference.Resolve(endLang);
            b.AddEntity(new EntityRow(startLangId, 2, LanguageTypeId, Source));
            b.AddEntity(new EntityRow(endLangId, 2, LanguageTypeId, Source));

            (double weight, string? surface) = ParseMeta(c[4]);

            b.AddAttestation(AttestationFactory.CreateWeighted(
                startId.Value, rk.Kind, endId.Value, Source, null,
                rk.Tier, SourceTrust.UserCuratedResource, magnitude: weight, floor: 1.0));
            b.AddAttestation(AttestationFactory.Create(
                startId.Value, KindHasLanguage, startLangId, Source, null,
                KindRank.Partitive, SourceTrust.UserCuratedResource));
            b.AddAttestation(AttestationFactory.Create(
                endId.Value, KindHasLanguage, endLangId, Source, null,
                KindRank.Partitive, SourceTrust.UserCuratedResource));
            if (surface is not null)
            {
                var sId = ContentEmitter.Emit(b, surface, Source);
                if (sId is not null)
                    b.AddAttestation(AttestationFactory.Create(
                        startId.Value, KindHasExample, sId.Value, Source, endId.Value,
                        KindRank.Partitive, SourceTrust.UserCuratedResource));
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
