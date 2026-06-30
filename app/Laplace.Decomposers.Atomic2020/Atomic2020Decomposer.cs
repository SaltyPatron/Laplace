using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

[UsesNativeIngest]
public sealed class Atomic2020Decomposer : RelationTripleDecomposerBase, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/Atomic2020Decomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 MarkerTypeId = EntityTypeRegistry.AtomicMarker;
    private static readonly Hash128 SplitTypeId  = EntityTypeRegistry.AtomicSplit;

    // SCHEMA, not content: the single absence-sentinel for ATOMIC's literal "none" tail
    // (a relation with no annotated filler). It is a fixed marker, not a concept, and must
    // NOT converge with the content word "none" — so it stays app/meta vocabulary (no geometry).
    private static readonly Hash128 NoneId       = Hash128.OfCanonical("substrate/atomic/none/v1");

    // SCHEMA, not content: the fixed dataset-split enum (train/dev/test). Used only as
    // attestation contextId (the provenance edge's context), never baked into a content id.
    // Small, fixed, correctly geometry-free app/meta vocabulary.
    private static Hash128 SplitId(string s) => Hash128.OfCanonical($"atomic/split/{s}");

    private static readonly (string Rel, string Type)[] Relations =
    {
        ("oEffect", "O_EFFECT"), ("oReact", "O_REACT"), ("oWant", "O_WANT"),
        ("xAttr", "X_ATTR"), ("xEffect", "X_EFFECT"), ("xIntent", "X_INTENT"),
        ("xNeed", "X_NEED"), ("xReact", "X_REACT"), ("xWant", "X_WANT"), ("xReason", "X_REASON"),
        ("HinderedBy", "OBSTRUCTED_BY"), ("isAfter", "IS_AFTER"), ("isBefore", "IS_BEFORE"),
        ("isFilledBy", "X_FILLED_BY"), ("Causes", "CAUSES"), ("ObjectUse", "OBJECT_USE"),
        ("AtLocation", "AT_LOCATION"), ("HasSubEvent", "HAS_SUBEVENT"),
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


    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var relTypes = Relations.Select(r => RelationTypeRegistry.Resolve(r.Type).Canonical).Distinct();
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["Atomic_Marker", "Atomic_Split"],
            relationNodeNames: relTypes, ct: ct);

        var seed = new SubstrateChangeBuilder(Source, "bootstrap/atomic-vocab", null,
            entityCapacity: 1 + Splits.Length, physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(NoneId, EntityTier.Word, MarkerTypeId, Source));
        foreach (var s in Splits) seed.AddEntity(new EntityRow(SplitId(s), EntityTier.Word, SplitTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(1_331_113L);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = Splits
            .Select(s => Path.Combine(context.EcosystemPath, $"{s}.tsv"))
            .Where(File.Exists)
            .ToList();
        return Task.FromResult(IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct));
    }

    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new List<string>
            {
                "substrate/atomic/none/v1",
                "substrate/type/Atomic_Marker/v1",
                "substrate/type/Atomic_Split/v1",
            };
            foreach (var name in Relations.Select(r => r.Type).Distinct())
            {
                names.Add($"substrate/type/{name}/v1");
                names.Add(VocabularyNames.RelationType(
                    RelationTypeRegistry.Resolve(name).Canonical));
            }
            return names;
        }
    }

    protected override async IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        long cap = options.MaxInputUnits;
        long consumed = 0;

        foreach (var split in Splits)
        {
            if (cap > 0 && consumed >= cap) yield break;
            long fileCap = cap > 0 ? cap - consumed : 0;
            string file = Path.Combine(ecosystemPath, $"{split}.tsv");
            if (!File.Exists(file)) continue;

            var witness = new Atomic2020GrammarWitness();
            Hash128 splitId = SplitId(split);

            await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
                file,
                EtlManifest.Get("atomic2020"),
                witness: witness,
                batchSize: batch,
                witnessWeight: 1.0,
                batchLabelPrefix: $"atomic/{split}",
                reportUnits: null,
                contextId: splitId,
                commitEpoch: 0,
                maxInputUnits: fileCap,
                containmentReader: ContainmentReader,
                ct: ct))
            {
                consumed += change.Metadata.InputUnitsConsumed;
                yield return change;
            }
        }
    }
}
