using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

public sealed class UnicodeDecomposer : IDecomposer
{
    public static readonly Hash128 Source     = Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");
    public static readonly Hash128 TrustClass = Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");
    public static readonly Hash128 CodepointType = Hash128.OfCanonical("substrate/type/Codepoint/v1");

    private const string UnicodeVersion = "17.0.0";
    private const int DefaultBatch = 4096;   // smaller: batches now carry attestations

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private CodepointRecord[]? _records;
    private UcdProperties? _ucd;

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet     = ducet;
    }

    public Hash128 SourceId    => Source;
    public string  SourceName  => "UnicodeDecomposer";
    public int     LayerOrder  => 0;
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        // ── bootstrap: types + attestation kinds ──
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Codepoint");
        boot.AddType("UcdClassifier");
        boot.AddType("OrdinalContext");
        // Rank lives ONLY in KindRegistry (all UCD kinds are StandardsStructural).
        boot.AddKind("HAS_GENERAL_CATEGORY");
        boot.AddKind("HAS_COMBINING_CLASS");
        boot.AddKind("HAS_SCRIPT");
        boot.AddKind("HAS_BLOCK");
        boot.AddKind("HAS_UPPERCASE_MAPPING");
        boot.AddKind("HAS_LOWERCASE_MAPPING");
        boot.AddKind("CANONICAL_DECOMPOSES_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);

        // ── seed classifier entities (category/script/block + ordinal contexts + combining class values) ──
        EnsureUcdProperties(context);
        var ucdClassifierTypeId = Hash128.OfCanonical("substrate/type/UcdClassifier/v1");
        var ordinalContextTypeId = Hash128.OfCanonical("substrate/type/OrdinalContext/v1");
        // 512 classifiers + 2 ordinal + 254 combining-class values
        var classifiers = new SubstrateChangeBuilder(
            Source, "bootstrap/ucd-classifiers", null,
            entityCapacity: 768, physicalityCapacity: 0, attestationCapacity: 0);
        foreach (var row in _ucd!.ClassificationEntities(Source))
            classifiers.AddEntity(row);
        // Ordinal context entities used as context_id on CANONICAL_DECOMPOSES_TO attestations
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx0, (byte)MetaTier.Meta, ordinalContextTypeId, Source));
        classifiers.AddEntity(new EntityRow(UcdProperties.OrdinalCtx1, (byte)MetaTier.Meta, ordinalContextTypeId, Source));
        // Combining class value entities used as object_id on HAS_COMBINING_CLASS attestations (1-254)
        for (int cc = 1; cc <= 254; cc++)
        {
            var ccId = Hash128.OfCanonical($"unicode/combining_class/{cc}/v1");
            classifiers.AddEntity(new EntityRow(ccId, (byte)MetaTier.Meta, ucdClassifierTypeId, Source));
        }
        await context.Writer.ApplyAsync(classifiers.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureComputed(context);
        EnsureUcdProperties(context);
        int total = _records!.Length;
        int batch = options.BatchSize > 1 ? options.BatchSize : DefaultBatch;

        // Pass 1: entities + physicalities only.
        // Attestations reference codepoint entities as object_id (case mappings,
        // decomposition targets). Some targets are in later batches, so we
        // commit all entities before emitting any attestations.
        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end, entitiesOnly: true);
            await Task.Yield();
        }

        // Pass 2: attestations only (all 1.1M codepoint entities now exist).
        for (int start = 0; start < total; start += batch)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batch, total);
            yield return BuildBatch(start, end, entitiesOnly: false);
            await Task.Yield();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(UnicodeSeed.CodepointCount);

    public ValueTask DisposeAsync() { _records = null; _ucd = null; return ValueTask.CompletedTask; }

    private SubstrateChange BuildBatch(int start, int end, bool entitiesOnly)
    {
        int n = end - start;
        string suffix = entitiesOnly ? "/entities" : "/attestations";
        var b = new SubstrateChangeBuilder(
            Source, $"codepoints/U+{start:X4}..U+{(end - 1):X4}{suffix}", null,
            entityCapacity:      entitiesOnly ? n : 0,
            physicalityCapacity: entitiesOnly ? n : 0,
            attestationCapacity: entitiesOnly ? 0 : n * 6);

        CodepointRecord[] recs = _records!;
        UcdProperties ucd = _ucd!;

        for (int cp = start; cp < end; cp++)
        {
            ref readonly CodepointRecord r = ref recs[cp];
            Hash128 entityId = r.Hash;

            if (entitiesOnly)
            {
                b.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);

                Hash128 physId = PhysicalityId.Compute(
                    entityId, Source, PhysicalityKind.Content,
                    r.CoordX, r.CoordY, r.CoordZ, r.CoordM,
                    ReadOnlySpan<double>.Empty);

                b.AddPhysicality(new PhysicalityRow(
                    Id: physId, EntityId: entityId, SourceId: Source,
                    Kind: PhysicalityKind.Content,
                    CoordX: r.CoordX, CoordY: r.CoordY, CoordZ: r.CoordZ, CoordM: r.CoordM,
                    HilbertIndex: r.Hilbert,
                    TrajectoryXyzm: null, NConstituents: 0,
                    AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
            }
            else
            {
                uint ucp = (uint)cp;

                // HAS_GENERAL_CATEGORY
                string? cat = ucd.GeneralCategory[cp];
                if (cat != null && ucd.CategoryEntityIds.TryGetValue(cat, out var catId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasGeneralCategory,
                        catId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_COMBINING_CLASS (only for non-zero — zero is the default)
                if (ucd.CombiningClass[cp] > 0)
                {
                    var ccId = Hash128.OfCanonical($"unicode/combining_class/{ucd.CombiningClass[cp]}/v1");
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasCombiningClass,
                        ccId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                }

                // HAS_SCRIPT
                string? script = ucd.ScriptForCodepoint(ucp);
                if (script != null && ucd.ScriptEntityIds.TryGetValue(script, out var scriptId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasScript,
                        scriptId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_BLOCK
                string? block = ucd.BlockForCodepoint(ucp);
                if (block != null && ucd.BlockEntityIds.TryGetValue(block, out var blockId))
                    b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasBlock,
                        blockId, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));

                // HAS_UPPERCASE_MAPPING
                if (ucd.UppercaseMapping[cp] != 0)
                {
                    uint targetCp = ucd.UppercaseMapping[cp];
                    if (targetCp < (uint)recs.Length)
                        b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasUppercaseMapping,
                            recs[targetCp].Hash, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                }

                // HAS_LOWERCASE_MAPPING
                if (ucd.LowercaseMapping[cp] != 0)
                {
                    uint targetCp = ucd.LowercaseMapping[cp];
                    if (targetCp < (uint)recs.Length)
                        b.AddAttestation(AttestationFactory.Create(entityId, UcdProperties.KindHasLowercaseMapping,
                            recs[targetCp].Hash, Source, null, KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                }

                // CANONICAL_DECOMPOSES_TO
                uint[]? decomp = ucd.CanonDecomp[cp];
                if (decomp != null)
                {
                    for (int di = 0; di < decomp.Length; di++)
                    {
                        uint targetCp = decomp[di];
                        if (targetCp < (uint)recs.Length)
                        {
                            Hash128 ctx = di == 0 ? UcdProperties.OrdinalCtx0 : UcdProperties.OrdinalCtx1;
                            b.AddAttestation(AttestationFactory.Create(entityId,
                                UcdProperties.KindCanonDecomposesTo,
                                recs[targetCp].Hash, Source, ctx,
                                KindRank.StandardsStructural, SourceTrust.StandardsDerived));
                        }
                    }
                }
            }
        }
        return b.Build();
    }

    private void EnsureComputed(IDecomposerContext context)
    {
        if (_records is not null) return;
        var (xml, duc) = ResolveSource(context);
        _records = UnicodeSeed.Compute(xml, duc);
    }

    private void EnsureUcdProperties(IDecomposerContext context)
    {
        if (_ucd is not null) return;
        string ucdDir = Path.Combine(context.EcosystemPath, "Public", UnicodeVersion, "ucd");
        _ucd = UcdProperties.Load(ucdDir);
    }

    private (string xml, string duc) ResolveSource(IDecomposerContext context)
    {
        string baseDir = context.EcosystemPath;
        string xml = _ucdxmlZip ?? Path.Combine(baseDir, "Public", UnicodeVersion, "ucdxml", "ucd.nounihan.flat.zip");
        string duc = _ducet    ?? Path.Combine(baseDir, "Public", UnicodeVersion, "uca", "allkeys.txt");
        return (xml, duc);
    }
}
