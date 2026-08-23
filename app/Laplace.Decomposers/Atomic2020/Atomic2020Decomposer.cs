using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Extractors;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Atomic2020;

/// <summary>
/// Multi-file relation-triple source. train/dev/test.tsv each go through
/// <see cref="ExtractFileAsync"/> — the same StreamingUtf8LineReader masticator
/// ConceptNet uses for its monolith file; the multi-file pool runs those units in parallel.
/// </summary>
public sealed class Atomic2020Decomposer
    : RelationTripleMultiFileDecomposerBase<Atomic2020Source, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = Atomic2020Source.SourceId;
    public static readonly Hash128 TrustClass = Atomic2020Source.TrustClass;

    private static readonly Hash128 MarkerTypeId = EntityTypeRegistry.AtomicMarker;
    private static readonly Hash128 SplitTypeId = EntityTypeRegistry.AtomicSplit;

    private static readonly Hash128 NoneId = SubstrateCanonicalIds.OfVersioned("atomic", "none");

    private static Hash128 SplitId(string s) => Hash128.OfCanonical($"atomic/split/{s}");

    internal static readonly Dictionary<string, string> RelTypeId =
        Atomic2020Source.RelPairs.ToDictionary(r => r.Rel, r => r.Type);

    private static readonly string[] Splits = ["train", "dev", "test"];

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.StructuredCorpus;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
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
        var paths = ListFiles(context.EcosystemPath, options).Select(f => f.Path).ToList();
        return Task.FromResult(IngestInventory.FromFiles(
            "records", paths, options.MaxInputUnits, ct, tracksFileCompletion: true));
    }

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new List<string>
            {
                "substrate/atomic/none/v1",
                "Atomic_Marker",
                "Atomic_Split",
            };
            foreach (var name in Atomic2020Source.RelPairs.Select(r => r.Type).Distinct())
            {
                names.Add(VocabularyNames.RelationType(
                    RelationTypeRegistry.Resolve(name).Canonical));
            }
            return names;
        }
    }

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        var list = new List<(string, string)>(Splits.Length);
        foreach (var split in Splits)
        {
            string file = Path.Combine(ecosystemPath, $"{split}.tsv");
            if (File.Exists(file))
                list.Add((file, $"atomic/{split}"));
        }
        return list;
    }

    // head <TAB> relation <TAB> tail — UTF-8 span parse, same reader as ConceptNet.
    protected override async IAsyncEnumerable<RelationTripleRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int slash = fileLabel.LastIndexOf('/');
        string split = slash >= 0 ? fileLabel[(slash + 1)..] : Path.GetFileNameWithoutExtension(filePath);
        Hash128 splitId = SplitId(split);

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            if (lineMem.Length == 0) continue;
            if (!TryExtract(lineMem.Span, splitId, out var record)) continue;
            yield return record;
        }
    }

    internal static bool TryExtract(
        ReadOnlySpan<byte> line, Hash128 splitId, out RelationTripleRecord record)
    {
        record = default;
        int t1 = line.IndexOf((byte)'\t');
        if (t1 <= 0) return false;
        var rest = line[(t1 + 1)..];
        int t2 = rest.IndexOf((byte)'\t');
        if (t2 <= 0) return false;
        var relBytes = rest[..t2];
        var tail = rest[(t2 + 1)..];
        var head = line[..t1];
        if (head.IsEmpty || tail.IsEmpty) return false;

        string rel = Encoding.UTF8.GetString(relBytes);
        if (!RelTypeId.TryGetValue(rel, out var relType)) return false;

        // Sign is the outcome: a negated relation folds as a Refute against the cell its
        // positive form asserts (laplace_score_fp scores v < 0 below 0.5).
        double magnitude = Atomic2020Source.NegatedRelations.Contains(rel) ? -1.0 : 1.0;

        // ATOMIC2020 spells "no tail exists for this head under this relation" as the
        // literal tail "none" -- 147,608 of 1,331,113 rows, 11.09%. That is the corpus
        // stating a negative, not omitting a row, and it entered the fold as a CONFIRM
        // toward the entity `none`: the substrate was told the head DOES stand in that
        // relation to something. Measured on the 2026-08-23 seed: 83,224 such edges.
        //
        // A null object is the record's way of carrying an asserted absence, and the spine
        // folds it as an object-null REFUTE. This does NOT filter the entity: `none` is a
        // real word, content-addressed like any other, and WordNet and OMW witness it here
        // as the same id. Only this decomposer knows ATOMIC's grammar spells absence that
        // way, which is why the test is here and not in the spine.
        bool assertsAbsence = tail.SequenceEqual("none"u8);

        record = new RelationTripleRecord(
            UnderscoredUtf8Canonicalize.ToSpaces(head),
            relType,
            assertsAbsence ? null : UnderscoredUtf8Canonicalize.ToSpaces(tail),
            splitId, magnitude);
        return true;
    }
}
