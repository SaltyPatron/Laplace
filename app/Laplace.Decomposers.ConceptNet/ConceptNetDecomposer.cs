using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ConceptNet;

public sealed class ConceptNetDecomposer : RelationTripleDecomposerBase
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/ConceptNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserCuratedResource/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

    private static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["RelatedTo"] = "RELATED_TO",      ["FormOf"] = "FORM_OF",
        ["IsA"] = "IS_A",                  ["PartOf"] = "IS_PART_OF",
        ["HasA"] = "HAS_A",                ["UsedFor"] = "USED_FOR",
        ["CapableOf"] = "CAPABLE_OF",      ["AtLocation"] = "AT_LOCATION",
        ["Causes"] = "CAUSES",             ["HasSubevent"] = "HAS_SUBEVENT",
        ["HasFirstSubevent"] = "HAS_FIRST_SUBEVENT",
        ["HasLastSubevent"]  = "HAS_LAST_SUBEVENT",
        ["HasPrerequisite"]  = "HAS_PREREQUISITE",
        ["HasProperty"]      = "HAS_PROPERTY",
        ["MotivatedByGoal"]  = "MOTIVATED_BY_GOAL",
        ["ObstructedBy"] = "OBSTRUCTED_BY", ["Desires"] = "DESIRES",
        ["CreatedBy"] = "CREATED_BY",       ["Synonym"] = "IS_SYNONYM_OF",
        ["Antonym"] = "IS_ANTONYM_OF",      ["DistinctFrom"] = "DISTINCT_FROM",
        ["DerivedFrom"] = "DERIVED_FROM",   ["SymbolOf"] = "SYMBOL_OF",
        ["DefinedAs"] = "DEFINED_AS",       ["MannerOf"] = "MANNER_OF",
        ["LocatedNear"] = "LOCATED_NEAR",   ["HasContext"] = "HAS_CONTEXT",
        ["SimilarTo"] = "SIMILAR_TO",
        ["EtymologicallyRelatedTo"]   = "ETYMOLOGICALLY_RELATED_TO",
        ["EtymologicallyDerivedFrom"] = "ETYMOLOGICALLY_DERIVED_FROM",
        ["CausesDesire"] = "CAUSES_DESIRE", ["MadeOf"] = "MADE_OF",
        ["ReceivesAction"] = "RECEIVES_ACTION", ["InstanceOf"] = "IS_INSTANCE_OF",
        ["NotDesires"] = "NOT_DESIRES",     ["NotUsedFor"] = "NOT_USED_FOR",
        ["NotCapableOf"] = "NOT_CAPABLE_OF", ["NotHasProperty"] = "NOT_HAS_PROPERTY",
        ["Entails"] = "ENTAILS",
    };

    public override Hash128 SourceId     => Source;
    public override string  SourceName   => "ConceptNetDecomposer";
    public override int     LayerOrder   => 2;
    public override Hash128 TrustClassId => TrustClass;

    protected override bool RequiresTwoPass => false;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_DBPEDIA_RELATION");
        foreach (var typeName in RelMap.Values)
            boot.AddRelationType(RelationTypeRegistry.Resolve(typeName).Canonical);
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

        var arenaSumSq = new Dictionary<string, double>(StringComparer.Ordinal);
        var arenaN     = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var line in File.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            var c = line.Split('\t');
            if (c.Length < 5) continue;
            string rel = c[1].StartsWith("/r/", StringComparison.Ordinal) ? c[1][3..] : c[1];
            string? typeName =
                RelMap.TryGetValue(rel, out var mapped) ? mapped
                : rel.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase)
                    ? RelationTypeRegistry.ResolveDbpedia(rel).Canonical
                    : null;
            if (typeName is null) continue;
            (double w, _) = ParseMeta(c[4]);
            arenaSumSq[typeName] = arenaSumSq.GetValueOrDefault(typeName) + w * w;
            arenaN[typeName]     = arenaN.GetValueOrDefault(typeName) + 1;
        }
        var arenaM = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, sq) in arenaSumSq)
            arenaM[k] = arenaN[k] > 0 ? Math.Sqrt(sq / arenaN[k]) : 0.0;

        var seenEntBatch = new HashSet<Hash128>();
        var seenAttRun = new ConcurrentIdSet();
        var b = NewBuilder("conceptnet/batch-0", batch);
        int n = 0, bn = 0;

        await foreach (var line in File.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            var c = line.Split('\t');
            if (c.Length < 5) continue;

            string rel = c[1].StartsWith("/r/", StringComparison.Ordinal) ? c[1][3..] : c[1];
            RelationTypeRegistry.RelationTypeResolution? dbp = null;
            if (!RelMap.TryGetValue(rel, out var typeName))
            {
                if (!rel.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase)) continue;
                var r = RelationTypeRegistry.ResolveDbpedia(rel);
                dbp = r; typeName = r.Canonical;
            }
            if (!ParseConcept(c[2], out string startTerm, out string startLang)) continue;
            if (!ParseConcept(c[3], out string endTerm, out string endLang)) continue;

            var startId = ContentEmitter.Emit(b, startTerm, Source);
            var endId   = ContentEmitter.Emit(b, endTerm, Source);
            if (startId is null || endId is null) continue;

            Hash128 startLangId = LanguageReference.Resolve(startLang);
            Hash128 endLangId   = LanguageReference.Resolve(endLang);
            b.AddEntity(new EntityRow(startLangId, (byte)MetaTier.Meta, LanguageTypeId, Source));
            b.AddEntity(new EntityRow(endLangId, (byte)MetaTier.Meta, LanguageTypeId, Source));

            (double weight, string? surface) = ParseMeta(c[4]);

            if (dbp is { } dyn)
                RelationTypeRegistry.SeedDynamic(b, dyn, Source, seenEntBatch, seenAttRun);

            b.AddAttestation(RelationTypeRegistry.AttestWeighted(
                startId.Value, typeName, endId.Value, Source, SourceTrust.UserCuratedResource,
                magnitude: weight, arenaScale: arenaM.GetValueOrDefault(typeName)));
            b.AddAttestation(RelationTypeRegistry.Attest(
                startId.Value, "HAS_LANGUAGE", startLangId, Source, SourceTrust.UserCuratedResource));
            b.AddAttestation(RelationTypeRegistry.Attest(
                endId.Value, "HAS_LANGUAGE", endLangId, Source, SourceTrust.UserCuratedResource));
            if (surface is not null)
            {
                var sId = ContentEmitter.Emit(b, surface, Source);
                if (sId is not null)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        startId.Value, "HAS_EXAMPLE", sId.Value, Source,
                        SourceTrust.UserCuratedResource, contextId: endId.Value));
            }

            if (++n >= batch)
            {
                yield return b.Build();
                seenEntBatch.Clear();
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
