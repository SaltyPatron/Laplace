using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ISO;

public sealed class ISODecomposer : DecomposerMultiPhase<ISOSource, FullScope>
{
    public static readonly Hash128 Source = ISOSource.SourceId;
    public static readonly Hash128 TrustClass = ISOSource.TrustClass;

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;
    private static readonly Hash128 Iso639CodeTypeId = EntityTypeRegistry.Iso639Code;
    private static readonly Hash128 RelTypeIsLanguageCode =
        RelationTypeRegistry.RelationTypeId("IS_LANGUAGE_CODE");
    private static readonly Hash128 RelTypeHasIso6391Code =
        RelationTypeRegistry.RelationTypeId("HAS_ISO639_1_CODE");
    private static readonly Hash128 RelTypeUsesScript =
        RelationTypeRegistry.RelationTypeId("USES_SCRIPT");
    private static readonly Hash128 RelTypeMemberOfMacrolanguage =
        RelationTypeRegistry.RelationTypeId("MEMBER_OF_MACROLANGUAGE");
    private static readonly Hash128 UcdClassifierTypeId = EntityTypeRegistry.UcdClassifier;
    private static readonly Hash128 LanguageVariantTypeId = EntityTypeRegistry.LanguageVariant;

    public override int LayerOrder => 1;

    private readonly ConcurrentStringSet _codeNames = new(StringComparer.Ordinal);

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _codeNames;

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (File.Exists(Path.Combine(context.EcosystemPath, "iso-639-3.tab")))
        {
            await foreach (var change in RunPhaseAsync(new Iso6393Phase(this), context, options, ct))
                yield return change;
        }

        await foreach (var change in RunPhaseAsync(new MacrolanguagePhase(this), context, options, ct))
            yield return change;

        await foreach (var change in RunPhaseAsync(new ScriptPhase(this), context, options, ct))
            yield return change;

        string retPath = Path.Combine(context.EcosystemPath, "iso-639-3_Retirements.tab");
        if (File.Exists(retPath))
        {
            await foreach (var change in RunPhaseAsync(new RetirementPhase(this), context, options, ct))
                yield return change;
        }

        await foreach (var change in RunPhaseAsync(new VariantPhase(this), context, options, ct))
            yield return change;

        if (File.Exists(Path.Combine(context.EcosystemPath, "iso-639-3_Name_Index.tab")))
        {
            await foreach (var change in RunPhaseAsync(new NameIndexPhase(this), context, options, ct))
                yield return change;
        }

        IntentStage.ResetContentBank();
    }

    private static int ResolveBatch(DecomposerOptions options) =>
        options.BatchSize > 1 ? options.BatchSize : 2048;

    internal void StageIsoTabRecord(IsoRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageEntityId.FromIso639_3(rec.Id);
        b.AddEntity(langId, EntityTier.Word, LanguageTypeId, Source);
        _codeNames.Add(VocabularyNames.LanguageIso639_3(rec.Id));
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeIsLanguageCode, null, Source, null,
            RelationTypeRank.StandardsStructural * TC.StandardsDerived));

        if (rec.Part1.Length > 0)
        {
            var iso1Name = $"iso639-1:{rec.Part1}";
            _codeNames.Add(iso1Name);
            var iso1Id = Hash128.OfCanonical(iso1Name);
            b.AddEntity(iso1Id, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                langId, RelTypeHasIso6391Code, iso1Id, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        foreach (var (p2, rel) in new[] { (rec.Part2b, "HAS_ISO639_2B_CODE"), (rec.Part2t, "HAS_ISO639_2T_CODE") })
        {
            if (p2.Length == 0) continue;
            var iso2Name = $"iso639-2:{p2}";
            _codeNames.Add(iso2Name);
            var iso2Id = Hash128.OfCanonical(iso2Name);
            b.AddEntity(iso2Id, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, rel, iso2Id, Source, TC.StandardsDerived));
        }
        if (rec.Scope.Length > 0)
        {
            var scopeId = Hash128.OfCanonical($"substrate/iso639/scope/{rec.Scope}/v1");
            _codeNames.Add($"substrate/iso639/scope/{rec.Scope}/v1");
            b.AddEntity(scopeId, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, "HAS_LANGUAGE_SCOPE", scopeId, Source, TC.StandardsDerived));
        }
        if (rec.Type.Length > 0)
        {
            var typeId = Hash128.OfCanonical($"substrate/iso639/type/{rec.Type}/v1");
            _codeNames.Add($"substrate/iso639/type/{rec.Type}/v1");
            b.AddEntity(typeId, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, "HAS_LANGUAGE_TYPE", typeId, Source, TC.StandardsDerived));
        }
        if (rec.RefName.Length > 0)
        {
            var nameId = ContentEmitter.Emit(b, rec.RefName, Source);
            if (nameId is { } nid)
            {
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, "HAS_NAME_ALIAS", nid, Source, TC.StandardsDerived));
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, "HAS_DEFINITION", nid, Source, TC.StandardsDerived));
            }
        }
    }

    internal void StageScriptRecord(ScriptRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageReference.Resolve(rec.Subtag);
        if (langId.Equals(LanguageEntityId.FromIso639_3("und"))) return;
        b.AddEntity(langId, EntityTier.Word, LanguageTypeId, Source);
        _codeNames.Add($"unicode/script/{rec.ScriptName}/v1");
        var scriptId = LanguageGraph.ScriptEntityId(rec.ScriptName);
        b.AddEntity(scriptId, EntityTier.Word, UcdClassifierTypeId, Source);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeUsesScript, scriptId, Source, null,
            RelationTypeRank.StandardsStructural * TC.StandardsDerived));
    }

    internal void StageVariantRecord((string Subtag, string Prefix) rec, SubstrateChangeBuilder b)
    {
        var variantId = LanguageGraph.VariantEntityId(rec.Subtag);
        _codeNames.Add($"substrate/iso639/variant/{rec.Subtag.ToLowerInvariant()}/v1");
        b.AddEntity(variantId, EntityTier.Word, LanguageVariantTypeId, Source);
        var parentId = LanguageReference.Resolve(rec.Prefix);
        if (parentId.Equals(LanguageEntityId.FromIso639_3("und"))) return;
        b.AddEntity(parentId, EntityTier.Word, LanguageTypeId, Source);
        b.AddAttestation(NativeAttestation.Categorical(
            variantId, "HAS_VARIANT_OF", parentId, Source, TC.StandardsDerived));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(7929L);

    internal readonly record struct IsoRecord(
        string Id, string Part2b, string Part2t, string Part1,
        string Scope, string Type, string RefName);

    internal readonly record struct ScriptRecord(string Subtag, string ScriptName);

    private abstract class IsoComposePhase<T> : ComposeDecomposerPhase<T>
    {
        protected readonly ISODecomposer Owner;

        protected IsoComposePhase(ISODecomposer owner) => Owner = owner;

        public override Hash128 SourceId => Owner.SourceId;
        public override string SourceName => Owner.SourceName;
        public override int LayerOrder => Owner.LayerOrder;
        public override Hash128 TrustClassId => Owner.TrustClassId;
        protected override double SourceTrust => TC.StandardsDerived;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context, DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId, BatchLabelPrefix, ResolveBatch(options), options, context.Reader, PipelineProfile),
                options);
    }

    private sealed class Iso6393Phase : IsoComposePhase<IsoRecord>
    {
        public Iso6393Phase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639-3";
        protected override void Compose(IsoRecord rec, SubstrateChangeBuilder b) => Owner.StageIsoTabRecord(rec, b);
        protected override async IAsyncEnumerable<IsoRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var rec in ParseAsync(Path.Combine(ecosystemPath, "iso-639-3.tab"), ct))
                yield return rec;
        }
    }

    private sealed class MacrolanguagePhase : IsoComposePhase<(string Indiv, string Macro)>
    {
        public MacrolanguagePhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/macrolanguages";
        protected override void Compose((string Indiv, string Macro) rec, SubstrateChangeBuilder b)
        {
            var indivId = LanguageEntityId.FromIso639_3(rec.Indiv);
            var macroId = LanguageEntityId.FromIso639_3(rec.Macro);
            b.AddEntity(indivId, EntityTier.Word, LanguageTypeId, Source);
            b.AddEntity(macroId, EntityTier.Word, LanguageTypeId, Source);
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                indivId, RelTypeMemberOfMacrolanguage, macroId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }
        protected override async IAsyncEnumerable<(string Indiv, string Macro)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var pair in LanguageGraph.Macrolanguages(ecosystemPath))
            {
                ct.ThrowIfCancellationRequested();
                yield return pair;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class ScriptPhase : IsoComposePhase<ScriptRecord>
    {
        public ScriptPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/scripts";
        protected override void Compose(ScriptRecord rec, SubstrateChangeBuilder b) => Owner.StageScriptRecord(rec, b);
        protected override async IAsyncEnumerable<ScriptRecord> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            string unidata = Path.GetFullPath(
                Path.Combine(ecosystemPath, "..", "UCD", "Public", "UCD", "latest", "ucd"));
            var scriptName = LanguageGraph.LoadScriptCodeToUcdName(unidata);
            foreach (var (subtag, scriptCodes) in LanguageGraph.LanguageScripts(ecosystemPath))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var code in scriptCodes)
                {
                    if (!scriptName.TryGetValue(code, out var name)) continue;
                    yield return new ScriptRecord(subtag, name);
                }
            }
            await Task.CompletedTask;
        }
    }

    private sealed class RetirementPhase : IsoComposePhase<(string Retired, string Successor)>
    {
        public RetirementPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/retirements";
        protected override void Compose((string Retired, string Successor) rec, SubstrateChangeBuilder b)
        {
            var retId = LanguageEntityId.FromIso639_3(rec.Retired);
            var sucId = LanguageEntityId.FromIso639_3(rec.Successor);
            b.AddEntity(retId, EntityTier.Word, LanguageTypeId, Source);
            b.AddEntity(sucId, EntityTier.Word, LanguageTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                retId, "SUPERSEDED_BY", sucId, Source, TC.StandardsDerived));
        }
        protected override async IAsyncEnumerable<(string Retired, string Successor)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            bool hdr = false;
            await foreach (var line in File.ReadLinesAsync(
                               Path.Combine(ecosystemPath, "iso-639-3_Retirements.tab"), ct))
            {
                if (!hdr) { hdr = true; continue; }
                var c = line.Split('\t');
                if (c.Length < 4) continue;
                string retired = c[0].Trim(), changeTo = c[3].Trim();
                if (retired.Length != 3 || changeTo.Length != 3) continue;
                yield return (retired, changeTo);
            }
        }
    }

    private sealed class VariantPhase : IsoComposePhase<(string Subtag, string Prefix)>
    {
        public VariantPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/variants";
        protected override void Compose((string Subtag, string Prefix) rec, SubstrateChangeBuilder b) =>
            Owner.StageVariantRecord(rec, b);
        protected override async IAsyncEnumerable<(string Subtag, string Prefix)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var (subtag, prefixes) in LanguageGraph.Variants(ecosystemPath))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var prefix in prefixes)
                    yield return (subtag, prefix);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class NameIndexPhase : IsoComposePhase<(string Id, string PrintName)>
    {
        public NameIndexPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/names";
        protected override void Compose((string Id, string PrintName) rec, SubstrateChangeBuilder b)
        {
            var lid = LanguageEntityId.FromIso639_3(rec.Id);
            b.AddEntity(lid, EntityTier.Word, LanguageTypeId, Source);
            if (ContentEmitter.Emit(b, rec.PrintName, Source) is { } nid)
                b.AddAttestation(NativeAttestation.Categorical(
                    lid, "HAS_DEFINITION", nid, Source, TC.StandardsDerived));
        }
        protected override async IAsyncEnumerable<(string Id, string PrintName)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            bool hdr = false;
            await foreach (var line in File.ReadLinesAsync(
                               Path.Combine(ecosystemPath, "iso-639-3_Name_Index.tab"), ct))
            {
                if (!hdr) { hdr = true; continue; }
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                string id = c[0].Trim(), printName = c[1].Trim();
                if (id.Length != 3 || printName.Length == 0) continue;
                yield return (id, printName);
            }
        }
    }

    private static async IAsyncEnumerable<IsoRecord> ParseAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool headerSkipped = false;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            string id = parts[0].Trim();
            string part2b = parts[1].Trim();
            string part2t = parts[2].Trim();
            string part1 = parts[3].Trim();
            string scope = parts[4].Trim();
            string type = parts[5].Trim();
            string refName = parts[6].Trim();
            if (id.Length != 3) continue;

            yield return new IsoRecord(id, part2b, part2t, part1, scope, type, refName);
        }
    }
}
