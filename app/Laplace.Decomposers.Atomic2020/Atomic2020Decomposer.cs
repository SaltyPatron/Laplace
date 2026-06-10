using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

public sealed class Atomic2020Decomposer : RelationTripleDecomposerBase
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/Atomic2020Decomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 MarkerTypeId = EntityTypeRegistry.AtomicMarker;
    private static readonly Hash128 SplitTypeId  = EntityTypeRegistry.AtomicSplit;
    private static readonly Hash128 NoneId       = Hash128.OfCanonical("substrate/atomic/none/v1");

    private static Hash128 SplitId(string s) => Hash128.OfCanonical($"atomic/split/{s}");

    private static readonly (string Rel, string Type)[] Relations =
    {
        ("oEffect", "O_EFFECT"), ("oReact", "O_REACT"), ("oWant", "O_WANT"),
        ("xAttr", "X_ATTR"), ("xEffect", "X_EFFECT"), ("xIntent", "X_INTENT"),
        ("xNeed", "X_NEED"), ("xReact", "X_REACT"), ("xWant", "X_WANT"), ("xReason", "X_REASON"),
        ("HinderedBy", "OBSTRUCTED_BY"), ("isAfter", "IS_AFTER"), ("isBefore", "IS_BEFORE"),
        ("isFilledBy", "X_FILLED_BY"), ("Causes", "CAUSES"), ("ObjectUse", "OBJECT_USE"),
        ("AtLocation", "AT_LOCATION"), ("HasSubEvent", "HAS_SUBEVENT"), ("HasSubevent", "HAS_SUBEVENT"),
        ("CapableOf", "CAPABLE_OF"), ("Desires", "DESIRES"), ("HasProperty", "HAS_PROPERTY"),
        ("MadeUpOf", "MADE_UP_OF"), ("NotDesires", "NOT_DESIRES"),
    };

    internal static readonly Dictionary<string, string> RelTypeId =
        Relations.ToDictionary(r => r.Rel, r => r.Type);

    private static readonly string[] Splits = ["train", "dev", "test"];

    public override Hash128 SourceId     => Source;
    public override string  SourceName   => "Atomic2020Decomposer";
    public override int     LayerOrder   => 2;
    public override Hash128 TrustClassId => TrustClass;

    protected override bool RequiresTwoPass => false;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Atomic_Marker");
        boot.AddType("Atomic_Split");
        foreach (var name in Relations.Select(r => r.Type).Distinct())
            boot.AddRelationType(RelationTypeRegistry.Resolve(name).Canonical);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var seed = new SubstrateChangeBuilder(Source, "bootstrap/atomic-vocab", null,
            entityCapacity: 1 + Splits.Length, physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(NoneId, EntityTier.Vocabulary, MarkerTypeId, Source));
        foreach (var s in Splits) seed.AddEntity(new EntityRow(SplitId(s), EntityTier.Vocabulary, SplitTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(1_331_113L);

    public IReadOnlyCollection<string> CanonicalNamesForReadback =>
        [.. Splits.Select(s => $"atomic/split/{s}"), "substrate/atomic/none/v1"];

    protected override async IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        var witness = new Atomic2020Witness();

        foreach (var split in Splits)
        {
            string file = Path.Combine(ecosystemPath, $"{split}.tsv");
            if (!File.Exists(file)) continue;

            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                file, "tsv", Source, witness, batch, SourceTrust.StructuredCorpus,
                $"atomic/{split}", reportUnits: null, contextId: SplitId(split), commitEpoch: 0, ct: ct))
            {
                yield return change;
            }
        }
    }
}
