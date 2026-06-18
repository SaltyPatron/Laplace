using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ConceptNet;

public sealed class ConceptNetDecomposer : RelationTripleDecomposerBase, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/ConceptNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserCuratedResource/v1");

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    internal static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
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

    internal static readonly ConcurrentDictionary<string, byte> LanguageNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => LanguageNames.Keys.ToArray();

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_EXAMPLE");
        foreach (var typeName in RelMap.Values)
            boot.AddRelationType(RelationTypeRegistry.Resolve(typeName).Canonical);
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            LanguageNames.TryAdd(n, 0);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        
        if (options.Languages?.IsActive == true)
            return Task.FromResult<IngestInventory?>(null);

        string file = Path.Combine(context.EcosystemPath, "assertions.csv");
        if (!File.Exists(file)) return Task.FromResult<IngestInventory?>(null);
        return CountInventoryAsync(file, options.Languages, ct);
    }

    private static async Task<IngestInventory?> CountInventoryAsync(
        string file, LanguageFilter? langs, CancellationToken ct)
    {
        long n = await EtlInventory.CountDataLinesAsync(file, line =>
        {
            if (langs?.IsActive != true) return true;
            return ConceptNetRowFilter.MatchesLanguageFilter(line, langs);
        }, ct: ct);
        return new IngestInventory("assertions", n, [new IngestFileSpec("assertions", file, n)]);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        
        
        var inv = await DescribeInputAsync(context, DecomposerOptions.ForWitness(SourceName), ct);
        return inv?.TotalInputUnits;
    }

    protected override async IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string file = Path.Combine(ecosystemPath, "assertions.csv");
        if (!File.Exists(file)) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 8192;

        await foreach (var change in ConceptNetFastIngest.IngestAssertionsAsync(
            file, batch, options.Languages, ct))
        {
            yield return change;
        }
    }
}
