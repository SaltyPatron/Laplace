using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

public sealed class Atomic2020Decomposer : RelationTripleDecomposerBase, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/Atomic2020Decomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 MarkerTypeId = EntityTypeRegistry.AtomicMarker;
    private static readonly Hash128 SplitTypeId = EntityTypeRegistry.AtomicSplit;




    private static readonly Hash128 NoneId = Hash128.OfCanonical("substrate/atomic/none/v1");




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

    public override Hash128 SourceId => Source;
    public override string SourceName => "Atomic2020Decomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.StructuredCorpus;


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
                "Atomic_Marker",
                "Atomic_Split",
            };
            foreach (var name in Relations.Select(r => r.Type).Distinct())
            {
                names.Add(VocabularyNames.RelationType(
                    RelationTypeRegistry.Resolve(name).Canonical));
            }
            return names;
        }
    }

    // Extraction only. ATOMIC-2020 rows are already-delimited `head <TAB> relation
    // <TAB> tail`; there is no container to unpack, so no tree-sitter. Blanks ('_',
    // e.g. "PersonX abandons ___ altogether") canonicalize to spaces. Everything
    // downstream — content-address, dedup, bulk COPY, fold — is the shared pipeline.
    protected override async IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cap = options.MaxInputUnits;
        long consumed = 0;

        foreach (var split in Splits)
        {
            string file = Path.Combine(ecosystemPath, $"{split}.tsv");
            if (!File.Exists(file)) continue;
            Hash128 splitId = SplitId(split);

            await using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;

                int t1 = line.IndexOf('\t');
                if (t1 <= 0) continue;
                int t2 = line.IndexOf('\t', t1 + 1);
                if (t2 <= t1 + 1) continue;

                string rel = line.Substring(t1 + 1, t2 - t1 - 1);
                if (!RelTypeId.TryGetValue(rel, out var relType)) continue;

                string head = line[..t1];
                string tail = line[(t2 + 1)..];
                if (head.Length == 0 || tail.Length == 0) continue;

                yield return new RelationTripleRecord(
                    UnderscoredUtf8Canonicalize.ToSpacesBytes(head), relType, UnderscoredUtf8Canonicalize.ToSpacesBytes(tail), splitId, 1.0);

                if (cap > 0 && ++consumed >= cap) yield break;
            }
        }
    }
}
