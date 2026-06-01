using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

/// <summary>
/// Emits the ATOMIC 2020 commonsense knowledge graph as content + attestations.
///
/// Each triple (head ⇥ relation ⇥ tail) from {train,dev,test}.tsv: the PersonX/PersonY
/// templated head/tail events are decomposed as content (ContentEmitter) — they're real
/// text. The relation becomes a typed kind (xIntent, xNeed, oEffect, …). A "none" tail is
/// PRESERVED as signal (absence-is-signal): it attests head → the canonical NONE marker,
/// recording "annotators inferred nothing here" rather than dropping the row. The split
/// (train/dev/test) is carried as context_id provenance.
///
/// Single-pass (acyclic): entities + attestations emit together; the writer orders
/// entities before attestations within each batch so the head/tail content FK always holds.
/// </summary>
public sealed class Atomic2020Decomposer : RelationTripleDecomposerBase
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/Atomic2020Decomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    private static readonly Hash128 MarkerTypeId = Hash128.OfCanonical("substrate/type/Atomic_Marker/v1");
    private static readonly Hash128 SplitTypeId  = Hash128.OfCanonical("substrate/type/Atomic_Split/v1");
    private static readonly Hash128 NoneId       = Hash128.OfCanonical("substrate/atomic/none/v1");

    private static Hash128 Kind(string n) => Hash128.OfCanonical($"substrate/kind/{n}/v1");
    private static Hash128 SplitId(string s) => Hash128.OfCanonical($"atomic/split/{s}");

    // ATOMIC 2020 relation → canonical kind name.
    private static readonly (string Rel, string Kind)[] Relations =
    {
        ("oEffect", "O_EFFECT"), ("oReact", "O_REACT"), ("oWant", "O_WANT"),
        ("xAttr", "X_ATTR"), ("xEffect", "X_EFFECT"), ("xIntent", "X_INTENT"),
        ("xNeed", "X_NEED"), ("xReact", "X_REACT"), ("xWant", "X_WANT"), ("xReason", "X_REASON"),
        ("HinderedBy", "HINDERED_BY"), ("isAfter", "IS_AFTER"), ("isBefore", "IS_BEFORE"),
        ("isFilledBy", "IS_FILLED_BY"), ("Causes", "CAUSES"), ("ObjectUse", "OBJECT_USE"),
        ("AtLocation", "AT_LOCATION"), ("HasSubEvent", "HAS_SUBEVENT"), ("HasSubevent", "HAS_SUBEVENT"),
        ("CapableOf", "CAPABLE_OF"), ("Desires", "DESIRES"), ("HasProperty", "HAS_PROPERTY"),
        ("MadeUpOf", "MADE_UP_OF"), ("NotDesires", "NOT_DESIRES"),
    };

    private static readonly Dictionary<string, Hash128> RelKind =
        Relations.ToDictionary(r => r.Rel, r => Kind(r.Kind));

    private static readonly string[] Splits = ["train", "dev", "test"];

    public override Hash128 SourceId     => Source;
    public override string  SourceName   => "Atomic2020Decomposer";
    public override int     LayerOrder   => 2;   // needs only unicode(0) — independent of wordnet/omw
    public override Hash128 TrustClassId => TrustClass;

    protected override bool RequiresTwoPass => false;

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Atomic_Marker");
        boot.AddType("Atomic_Split");
        foreach (var name in Relations.Select(r => r.Kind).Distinct())
            boot.AddKind(name, KindRank.Causal, SourceTrust.StructuredCorpus);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var seed = new SubstrateChangeBuilder(Source, "bootstrap/atomic-vocab", null,
            entityCapacity: 1 + Splits.Length, physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(NoneId, 0, MarkerTypeId, Source));
        foreach (var s in Splits) seed.AddEntity(new EntityRow(SplitId(s), 0, SplitTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(1_331_113L);

    protected override async IAsyncEnumerable<SubstrateChange> StreamTriplesAsync(
        string ecosystemPath, TriplePass pass, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        var b = NewBuilder("atomic/batch-0", batch);
        int n = 0, bn = 0;

        foreach (var split in Splits)
        {
            string file = Path.Combine(ecosystemPath, $"{split}.tsv");
            if (!File.Exists(file)) continue;
            Hash128 splitId = SplitId(split);

            await foreach (var line in File.ReadLinesAsync(file, ct))
            {
                ct.ThrowIfCancellationRequested();
                var c = line.Split('\t');
                if (c.Length < 3) continue;
                string head = c[0].Trim(), rel = c[1].Trim(), tail = c[2].Trim();
                if (head.Length == 0 || !RelKind.TryGetValue(rel, out var kindId)) continue;

                var headId = ContentEmitter.Emit(b, head, Source);
                if (headId is null) continue;

                Hash128 tailId;
                if (tail.Length == 0 || tail.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    tailId = NoneId; // absence-is-signal: explicit "no inference"
                }
                else
                {
                    var t = ContentEmitter.Emit(b, tail, Source);
                    if (t is null) continue;
                    tailId = t.Value;
                }

                b.AddAttestation(AttestationFactory.Create(
                    headId.Value, kindId, tailId, Source, splitId,
                    KindRank.Causal, SourceTrust.StructuredCorpus));

                if (++n >= batch)
                {
                    yield return b.Build();
                    b = NewBuilder($"atomic/batch-{++bn}", batch); n = 0; await Task.Yield();
                }
            }
        }
        if (n > 0) yield return b.Build();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 12,
            physicalityCapacity: batch * 12,
            attestationCapacity: batch);
}
