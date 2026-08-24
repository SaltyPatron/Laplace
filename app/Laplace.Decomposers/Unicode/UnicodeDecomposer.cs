using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Tier-0 codepoint law on the ingest spine. Phase order is forced
/// (classifiers → tier-0 entity+phys → mappings → aliases/confusables → bytes);
/// orchestration is <see cref="RunPhaseAsync"/> over <see cref="ComposeDecomposerPhase{T}"/>,
/// not a hand <c>yield return Build*</c> stream. Mapping targets resolve only —
/// they never mint entity-only rows (<see cref="StageCodepointTarget"/>).
/// </summary>
public sealed class UnicodeDecomposer : DecomposerMultiPhase<UnicodeSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = UnicodeSource.SourceId;
    public static readonly Hash128 TrustClass = UnicodeSource.TrustClass;

    public static readonly Hash128 CodepointType = EntityTypeRegistry.Codepoint;

    private static readonly Hash128[] CombiningClassIds = BuildCombiningClassIds();

    private static Hash128[] BuildCombiningClassIds()
    {
        var ids = new Hash128[255];
        for (int cc = 1; cc <= 254; cc++)
            ids[cc] = Hash128.OfCanonical($"unicode/combining_class/{cc}/v1");
        return ids;
    }

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private CodepointRecord[]? _records;
    private UcdProperties? _ucd;

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet = ducet;
    }

    public override int LayerOrder => 0;

    protected override Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        // Precompute only — classifier rows ride ClassifierPhase on the spine.
        // Do not hand-apply via the writer here (that was the bootstrap escape).
        EnsureComputed(context);
        EnsureUcdProperties(context);
        return Task.CompletedTask;
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureComputed(context);
        EnsureUcdProperties(context);

        var recs = _records!;
        var ucd = _ucd!;
        int batch = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Unicode, options);

        await foreach (var c in RunPhaseAsync(
                           new ClassifierPhase(ucd, batch), context, options, ct))
            yield return c;

        await foreach (var c in RunPhaseAsync(
                           new Tier0Phase(recs, batch), context, options, ct))
            yield return c;

        // Cap runs stop after tier-0 (same shape as WordNet data-then-rest).
        if (options.MaxInputUnits > 0) yield break;

        var uncapped = options with { MaxInputUnits = 0 };

        await foreach (var c in RunPhaseAsync(
                           new MappingPhase(recs, ucd, batch), context, uncapped, ct))
            yield return c;

        await foreach (var c in RunPhaseAsync(
                           new AliasConfusablePhase(recs, ucd, batch), context, uncapped, ct))
            yield return c;

        await foreach (var c in RunPhaseAsync(
                           new ByteEncodingPhase(recs, batch), context, uncapped, ct))
            yield return c;
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
        => Task.FromResult<IngestInventory?>(
            IngestInventory.Single(UnicodeSeed.CodepointCount, "codepoints"));

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(UnicodeSeed.CodepointCount);

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var ucd = _ucd;
            if (ucd is null) return Array.Empty<string>();
            var names = new List<string>(2048);
            foreach (var n in ucd.CategoryEntityIds.Keys) names.Add($"unicode/category/{n}/v1");
            foreach (var n in ucd.ScriptEntityIds.Keys) names.Add($"unicode/script/{n}/v1");
            foreach (var n in ucd.BlockEntityIds.Keys) names.Add($"unicode/block/{n}/v1");
            foreach (var n in ucd.BidiClassEntityIds.Keys) names.Add($"unicode/bidi_class/{n}/v1");
            foreach (var n in ucd.AgeEntityIds.Keys) names.Add($"unicode/age/{n}/v1");
            foreach (var n in ucd.EmojiPropEntityIds.Keys) names.Add($"unicode/emoji/{n}/v1");
            foreach (var v in ucd.NumericEntityIds.Keys) names.Add($"unicode/numeric/{v}/v1");
            foreach (var v in ucd.LineBreakEntityIds.Keys) names.Add($"unicode/line_break/{v}/v1");
            foreach (var v in ucd.EastAsianWidthEntityIds.Keys) names.Add($"unicode/east_asian_width/{v}/v1");
            foreach (var v in ucd.JoiningTypeEntityIds.Keys) names.Add($"unicode/joining_type/{v}/v1");
            foreach (var v in ucd.NumericTypeEntityIds.Keys) names.Add($"unicode/numeric_type/{v}/v1");
            foreach (var v in ucd.NormalizationFormEntityIds.Keys) names.Add($"unicode/normalization_form/{v}/v1");
            for (int cc = 1; cc <= 254; cc++) names.Add($"unicode/combining_class/{cc}/v1");
            names.Add("Byte");
            names.Add("substrate/encoding/ISO-8859-1/v1");
            names.Add("substrate/encoding/windows-1252/v1");
            foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
                names.Add($"substrate/utf8/{role}/v1");
            return names;
        }
    }

    public override ValueTask DisposeAsync()
    {
        _records = null;
        _ucd = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves a codepoint referenced as a relation TARGET. Tier-0 entity+physicality
    /// for every codepoint is seeded by <see cref="Tier0Phase"/> before any mapping
    /// phase runs — this method never stages an entity row.
    /// </summary>
    internal static Hash128 StageCodepointTarget(CodepointRecord[] recs, uint targetCp)
    {
        if (targetCp >= (uint)recs.Length)
            throw new ArgumentOutOfRangeException(nameof(targetCp), targetCp,
                "StageCodepointTarget: codepoint is outside the tier-0 seeded record set — "
                + "Tier0Phase must complete before mapping phases reference targets.");
        return recs[targetCp].Hash;
    }

    private void EnsureComputed(IDecomposerContext context)
    {
        if (_records is not null) return;
        // Single-origin law: the DB seed computes from raw UCD, never from the
        // perfcache blob. The blob is a sibling OUTPUT of this same compute
        // (native laplace_unicode_seed_compute), not a seed input.
        var (xml, duc) = ResolveSource(context);
        _records = UnicodeSeed.Compute(xml, duc);
    }

    private void EnsureUcdProperties(IDecomposerContext context)
    {
        if (_ucd is not null) return;
        string ucdDir = Path.Combine(context.EcosystemPath, "ucd");
        _ucd = UcdProperties.Load(ucdDir);
    }

    private (string xml, string duc) ResolveSource(IDecomposerContext context)
    {
        string baseDir = context.EcosystemPath;
        string xml = _ucdxmlZip ?? Path.Combine(baseDir, "ucdxml", "ucd.nounihan.flat.zip");
        string duc = _ducet ?? Path.Combine(baseDir, "uca", "allkeys.txt");
        return (xml, duc);
    }

    // ── Spine phases ──────────────────────────────────────────────────────────

    private abstract class UnicodeComposePhase<T> : ComposeDecomposerPhase<T>
    {
        private readonly int _batch;
        private readonly int _commitEpoch;
        private readonly int? _attestationCapacity;

        protected UnicodeComposePhase(int batch, int commitEpoch, int? attestationCapacity = null)
        {
            _batch = batch;
            _commitEpoch = commitEpoch;
            _attestationCapacity = attestationCapacity;
        }

        public override Hash128 SourceId => Source;
        public override string SourceName => "UnicodeDecomposer";
        public override int LayerOrder => 0;
        public override Hash128 TrustClassId => TrustClass;
        protected override double SourceTrust => TC.StandardsDerived;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId, BatchLabelPrefix, options, context.Reader,
                    IngestSourceProfile.Unicode,
                    attestationCapacity: _attestationCapacity,
                    commitEpoch: _commitEpoch),
                options);
    }

    /// <summary>UCD classifier / ordinal / combining-class entities before tier-0.</summary>
    private sealed class ClassifierPhase : UnicodeComposePhase<EntityRow>
    {
        private readonly UcdProperties _ucd;

        public ClassifierPhase(UcdProperties ucd, int batch) : base(batch, commitEpoch: 0, attestationCapacity: 0)
            => _ucd = ucd;

        protected override string PhaseLabel => "classifiers";

        protected override void Compose(EntityRow row, SubstrateChangeBuilder b) => b.AddEntity(row);

        protected override long UnitsPerRecord(EntityRow row) => 0;

        protected override async IAsyncEnumerable<EntityRow> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var ucdClassifierTypeId = EntityTypeRegistry.UcdClassifier;
            var ordinalContextTypeId = EntityTypeRegistry.OrdinalContext;
            foreach (var row in _ucd.ClassificationEntities(Source))
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            yield return new EntityRow(UcdProperties.OrdinalCtx0, EntityTier.Word, ordinalContextTypeId, Source);
            yield return new EntityRow(UcdProperties.OrdinalCtx1, EntityTier.Word, ordinalContextTypeId, Source);
            for (int cc = 1; cc <= 254; cc++)
                yield return new EntityRow(CombiningClassIds[cc], EntityTier.Word, ucdClassifierTypeId, Source);
        }
    }

    /// <summary>
    /// Sole mint site for tier-0 codepoint entity+physicality pairs. Completes for the
    /// whole (or MaxInputUnits-capped) space before mapping phases run.
    /// </summary>
    private sealed class Tier0Phase : UnicodeComposePhase<int>
    {
        private readonly CodepointRecord[] _recs;

        public Tier0Phase(CodepointRecord[] recs, int batch)
            : base(batch, commitEpoch: 0, attestationCapacity: 0) => _recs = recs;

        protected override string PhaseLabel => "tier0";

        protected override void Compose(int cp, SubstrateChangeBuilder b)
        {
            ref readonly CodepointRecord r = ref _recs[cp];
            Hash128 entityId = r.Hash;
            b.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);
            Hash128 physId = PhysicalityId.Compute(entityId, PhysicalityType.Content);
            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: entityId, SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: r.CoordX, CoordY: r.CoordY, CoordZ: r.CoordZ, CoordM: r.CoordM,
                HilbertIndex: r.Hilbert,
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
        }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            int total = _recs.Length;
            if (options.MaxInputUnits > 0) total = (int)Math.Min(total, options.MaxInputUnits);
            for (int cp = 0; cp < total; cp++)
            {
                ct.ThrowIfCancellationRequested();
                yield return cp;
            }
        }
    }

    /// <summary>
    /// Property/mapping attestations. Never re-mints tier-0 rows — targets via
    /// <see cref="StageCodepointTarget"/> only.
    /// </summary>
    private sealed class MappingPhase : UnicodeComposePhase<int>
    {
        private readonly CodepointRecord[] _recs;
        private readonly UcdProperties _ucd;

        public MappingPhase(CodepointRecord[] recs, UcdProperties ucd, int batch)
            : base(batch, commitEpoch: 0) => (_recs, _ucd) = (recs, ucd);

        protected override string PhaseLabel => "mappings";

        // This phase projects the tier-0 codepoints already counted by Tier0Phase.
        // Re-reading them is work, not another 1,114,112 source input units.
        protected override long UnitsPerRecord(int cp) => 0;

        protected override void Compose(int cp, SubstrateChangeBuilder b)
        {
            var recs = _recs;
            var ucd = _ucd;
            Hash128 entityId = recs[cp].Hash;
            uint ucp = (uint)cp;

            string? name = ucd.Name[cp];
            if (name != null)
            {
                var nameId = ContentEmitter.Emit(b, name, Source);
                if (nameId is { } nid)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasName,
                        nid, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }

            string? lb = ucd.LineBreakForCodepoint(ucp);
            if (lb != null && ucd.LineBreakEntityIds.TryGetValue(lb, out var lbId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasLineBreak,
                    lbId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            string? eaw = ucd.EastAsianWidthForCodepoint(ucp);
            if (eaw != null && ucd.EastAsianWidthEntityIds.TryGetValue(eaw, out var eawId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasEastAsianWidth,
                    eawId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            string? jt = ucd.JoiningTypeForCodepoint(ucp);
            if (jt != null && ucd.JoiningTypeEntityIds.TryGetValue(jt, out var jtId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasJoiningType,
                    jtId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            // The only UCD property that states a NEGATIVE. DerivedNormalizationProps lists
            // only the codepoints whose quick-check is not "Yes": N means the codepoint is
            // never in that normalization form, M means maybe -- it depends on the
            // neighbours. Yes is carried by ABSENCE from the file and is deliberately not
            // asserted: absence is unknown (spec 05), and materialising the ~4.4M derived
            // Confirms would be inventing evidence the standard never gave.
            //
            // N folds as a REFUTE; M folds as a DRAW, because magnitude 0 scores exactly
            // 0.5 (laplace_score_fp) and a draw is what "maybe" means in a Glicko fold.
            // Before this, UnicodeDecomposer had 1,631,783 confirms and neither.
            if (ucd.NormalizationQc.TryGetValue(ucp, out var qcVerdicts))
                foreach (var (form, maybe) in qcVerdicts)
                {
                    if (!ucd.NormalizationFormEntityIds.TryGetValue(form, out var formId)) continue;
                    double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;
                    b.AddAttestation(maybe
                        ? NativeAttestation.ResolvedScored(
                            entityId, UcdProperties.RelTypeHasNormalizationForm, formId, Source, null,
                            weight, signedMagnitude: 0.0, arenaScale: 1.0)
                        : NativeAttestation.CategoricalResolved(
                            entityId, UcdProperties.RelTypeHasNormalizationForm, formId, Source, null,
                            weight, confirm: false));
                }

            string? nt = ucd.NumericTypeForCodepoint(ucp);
            if (nt != null && ucd.NumericTypeEntityIds.TryGetValue(nt, out var ntId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasNumericType,
                    ntId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            string? cat = ucd.GeneralCategory[cp];
            if (cat != null && ucd.CategoryEntityIds.TryGetValue(cat, out var catId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasGeneralCategory,
                    catId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            if (ucd.CombiningClass[cp] > 0)
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasCombiningClass,
                    CombiningClassIds[ucd.CombiningClass[cp]], Source, null,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            string? script = ucd.ScriptForCodepoint(ucp);
            if (script != null && ucd.ScriptEntityIds.TryGetValue(script, out var scriptId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasScript,
                    scriptId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            string? block = ucd.BlockForCodepoint(ucp);
            if (block != null && ucd.BlockEntityIds.TryGetValue(block, out var blockId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasBlock,
                    blockId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            if (ucd.UppercaseMapping[cp] != 0)
            {
                uint targetCp = ucd.UppercaseMapping[cp];
                if (targetCp < (uint)recs.Length)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasUppercaseMapping,
                        StageCodepointTarget(recs, targetCp), Source, null,
                        RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }

            if (ucd.LowercaseMapping[cp] != 0)
            {
                uint targetCp = ucd.LowercaseMapping[cp];
                if (targetCp < (uint)recs.Length)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasLowercaseMapping,
                        StageCodepointTarget(recs, targetCp), Source, null,
                        RelationTypeRank.StandardsStructural * TC.StandardsDerived));
            }

            uint[]? decomp = ucd.CanonDecomp[cp];
            if (decomp != null)
            {
                for (int di = 0; di < decomp.Length; di++)
                {
                    uint targetCp = decomp[di];
                    if (targetCp < (uint)recs.Length)
                    {
                        Hash128 ctx = di == 0 ? UcdProperties.OrdinalCtx0 : UcdProperties.OrdinalCtx1;
                        b.AddAttestation(NativeAttestation.CategoricalResolved(entityId,
                            UcdProperties.RelTypeCanonDecomposesTo,
                            StageCodepointTarget(recs, targetCp), Source, ctx,
                            RelationTypeRank.StandardsStructural * TC.StandardsDerived));
                    }
                }
            }

            if (ucd.TitlecaseMapping[cp] != 0 && ucd.TitlecaseMapping[cp] < (uint)recs.Length)
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasTitlecaseMapping,
                    StageCodepointTarget(recs, ucd.TitlecaseMapping[cp]), Source, null,
                    RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            uint[]? compat = ucd.CompatDecomp[cp];
            if (compat != null)
            {
                for (int di = 0; di < compat.Length; di++)
                {
                    uint targetCp = compat[di];
                    if (targetCp < (uint)recs.Length)
                    {
                        Hash128 ctx = di == 0 ? UcdProperties.OrdinalCtx0 : UcdProperties.OrdinalCtx1;
                        b.AddAttestation(NativeAttestation.CategoricalResolved(entityId,
                            UcdProperties.RelTypeCompatDecomposesTo,
                            StageCodepointTarget(recs, targetCp), Source, ctx,
                            RelationTypeRank.StandardsStructural * TC.StandardsDerived));
                    }
                }
            }

            string? num = ucd.NumericValue[cp];
            if (num != null && ucd.NumericEntityIds.TryGetValue(num, out var numId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasNumericValue,
                    numId, Source, null, RelationTypeRank.ScalarValued * TC.StandardsDerived));

            string? bidi = ucd.BidiClass[cp];
            if (bidi != null && ucd.BidiClassEntityIds.TryGetValue(bidi, out var bidiId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasBidiClass,
                    bidiId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            uint mir = ucd.BidiMirror[cp];
            if (mir != 0 && mir < (uint)recs.Length && cp <= mir)
                b.AddAttestation(NativeAttestation.Categorical(
                    entityId, "HAS_MIRROR", StageCodepointTarget(recs, mir), Source, null, TC.StandardsDerived));

            string? age = ucd.AgeForCodepoint(ucp);
            if (age != null && ucd.AgeEntityIds.TryGetValue(age, out var ageId))
                b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasAge,
                    ageId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));

            byte eprops = ucd.EmojiProps[cp];
            if (eprops != 0)
                for (int bit = 0; bit < UcdProperties.EmojiPropNames.Length; bit++)
                    if ((eprops & (1 << bit)) != 0
                        && ucd.EmojiPropEntityIds.TryGetValue(UcdProperties.EmojiPropNames[bit], out var epId))
                        b.AddAttestation(NativeAttestation.CategoricalResolved(entityId, UcdProperties.RelTypeHasEmojiProperty,
                            epId, Source, null, RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            for (int cp = 0; cp < _recs.Length; cp++)
            {
                ct.ThrowIfCancellationRequested();
                yield return cp;
            }
        }
    }

    /// <summary>Alias text set ⇒ name-alias row; ConfusableText set ⇒ confusable row.</summary>
    private readonly record struct AliasConfusableRow(uint Cp, string? Alias, string? ConfusableText);

    private sealed class AliasConfusablePhase : UnicodeComposePhase<AliasConfusableRow>
    {
        private readonly CodepointRecord[] _recs;
        private readonly UcdProperties _ucd;

        public AliasConfusablePhase(CodepointRecord[] recs, UcdProperties ucd, int batch)
            : base(batch, commitEpoch: 1) => (_recs, _ucd) = (recs, ucd);

        protected override string PhaseLabel => "aliases-confusables";

        // Alias/confusable rows enrich the already-counted Unicode inventory.
        protected override long UnitsPerRecord(AliasConfusableRow row) => 0;

        protected override void Compose(AliasConfusableRow row, SubstrateChangeBuilder b)
        {
            if (row.Alias is { } alias)
            {
                var aliasId = ContentEmitter.Emit(b, alias, Source);
                if (aliasId is { } aid)
                    b.AddAttestation(NativeAttestation.Categorical(
                        StageCodepointTarget(_recs, row.Cp), "HAS_NAME_ALIAS", aid, Source,
                        TC.StandardsDerived));
                return;
            }

            if (row.ConfusableText is not { Length: > 0 } target) return;
            int first = char.ConvertToUtf32(target, 0);
            int firstLen = char.IsSurrogatePair(target, 0) ? 2 : 1;
            Hash128? targetId = target.Length == firstLen
                ? StageCodepointTarget(_recs, (uint)first)
                : ContentEmitter.Emit(b, target, Source);
            if (targetId is { } tid)
                b.AddAttestation(NativeAttestation.Categorical(
                    StageCodepointTarget(_recs, row.Cp), "CONFUSABLE_WITH", tid, Source,
                    TC.StandardsDerived));
        }

        protected override async IAsyncEnumerable<AliasConfusableRow> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var (cp, aliases) in _ucd.NameAliases)
            {
                if (cp >= (uint)_recs.Length) continue;
                foreach (var alias in aliases)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new AliasConfusableRow(cp, alias, null);
                }
            }
            foreach (var (src, target) in _ucd.Confusables)
            {
                ct.ThrowIfCancellationRequested();
                if (src >= (uint)_recs.Length || target.Length == 0) continue;
                yield return new AliasConfusableRow(src, null, target);
            }
        }
    }

    private sealed class ByteEncodingPhase : UnicodeComposePhase<int>
    {
        private readonly CodepointRecord[] _recs;
        // -1 = catalog (encodings + utf8 roles); 0..255 = byte atoms
        public ByteEncodingPhase(CodepointRecord[] recs, int batch)
            : base(batch, commitEpoch: 1) => _recs = recs;

        protected override string PhaseLabel => "bytes";

        // Byte catalog construction is an internal projection of the Unicode seed.
        protected override long UnitsPerRecord(int value) => 0;

        protected override void Compose(int v, SubstrateChangeBuilder b)
        {
            var latin1 = SubstrateCanonicalIds.OfVersioned("encoding", "ISO-8859-1");
            var cp1252 = SubstrateCanonicalIds.OfVersioned("encoding", "windows-1252");
            if (v < 0)
            {
                var encType = EntityTypeRegistry.CharacterEncoding;
                var roleType = EntityTypeRegistry.Utf8Role;
                b.AddEntity(new EntityRow(latin1, EntityTier.Word, encType, Source));
                b.AddEntity(new EntityRow(cp1252, EntityTier.Word, encType, Source));
                foreach (var role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
                {
                    var rid = Hash128.OfCanonical($"substrate/utf8/{role}/v1");
                    b.AddEntity(new EntityRow(rid, EntityTier.Word, roleType, Source));
                }
                return;
            }

            byte bv = (byte)v;
            Hash128 byteId = ByteAtoms.Id(bv);
            b.AddEntity(byteId, tier: 0, ByteAtoms.TypeId, firstObservedBy: Source);
            var coord = ByteAtoms.Coord(bv);
            Hash128 physId = PhysicalityId.Compute(byteId, PhysicalityType.Content);
            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: byteId, SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: coord[0], CoordY: coord[1], CoordZ: coord[2], CoordM: coord[3],
                HilbertIndex: ByteAtoms.Hilbert(bv),
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));

            var roleId = Hash128.OfCanonical($"substrate/utf8/{ByteAtoms.Utf8Role(bv)}/v1");
            b.AddAttestation(NativeAttestation.Categorical(
                byteId, "HAS_UTF8_ROLE", roleId, Source, TC.StandardsDerived));

            b.AddAttestation(NativeAttestation.Categorical(
                byteId, "DECODES_TO", StageCodepointTarget(_recs, (uint)v), Source,
                TC.StandardsDerived, contextId: latin1));

            uint cp1252Target = bv <= 0x9F
                ? ByteAtoms.Cp1252High[bv - 0x80]
                : (uint)bv;
            if (cp1252Target != 0)
                b.AddAttestation(NativeAttestation.Categorical(
                    byteId, "DECODES_TO", StageCodepointTarget(_recs, cp1252Target), Source,
                    TC.StandardsDerived, contextId: cp1252));
        }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return -1;
            for (int v = ByteAtoms.First; v <= 0xFF; v++)
            {
                ct.ThrowIfCancellationRequested();
                yield return v;
            }
        }
    }
}
