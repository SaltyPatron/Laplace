using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ISO;

public sealed class ISODecomposer : DecomposerMultiPhase<ISOSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = ISOSource.SourceId;
    private const string NameAliasRelation = "HAS_NAME_ALIAS";
    private const string LanguageTypeRelation = "HAS_LANGUAGE_TYPE";

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

    private static readonly string[] RelativePhysicalFiles =
    [
        "iso-639-3.tab",
        "iso-639-3-macrolanguages.tab",
        Path.Combine("cldr", "supplementalData.xml"),
        "iso-639-3_Retirements.tab",
        Path.Combine("iana", "language-subtag-registry.txt"),
        "iso-639-3_Name_Index.tab",
    ];

    private static bool SelectedOrUnmanifested(IDecomposerContext context, string path)
    {
        if (!File.Exists(path)) return false;
        if (!context.HasArtifactGraph) return true;
        string full = Path.GetFullPath(path);
        return context.SelectedArtifacts.Any(artifact => string.Equals(
            Path.GetFullPath(artifact.Path), full, StringComparison.Ordinal));
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string iso639 = Path.Combine(context.EcosystemPath, "iso-639-3.tab");
        if (SelectedOrUnmanifested(context, iso639))
        {
            await foreach (var change in RunPhaseAsync(
                new Iso6393Phase(this), context, options, "iso-639-3.tab", iso639, ct))
                yield return change;
        }

        string macro = Path.Combine(context.EcosystemPath, "iso-639-3-macrolanguages.tab");
        if (SelectedOrUnmanifested(context, macro))
        {
            await foreach (var change in RunPhaseAsync(
                new MacrolanguagePhase(this), context, options,
                "iso-639-3-macrolanguages.tab", macro, ct))
                yield return change;
        }

        string script = Path.Combine(context.EcosystemPath, "cldr", "supplementalData.xml");
        if (SelectedOrUnmanifested(context, script))
        {
            await foreach (var change in RunPhaseAsync(
                new ScriptPhase(this), context, options, "cldr/supplementalData.xml", script, ct))
                yield return change;
        }

        string retPath = Path.Combine(context.EcosystemPath, "iso-639-3_Retirements.tab");
        if (SelectedOrUnmanifested(context, retPath))
        {
            await foreach (var change in RunPhaseAsync(
                new RetirementPhase(this), context, options,
                "iso-639-3_Retirements.tab", retPath, ct))
                yield return change;
        }

        string variant = Path.Combine(context.EcosystemPath, "iana", "language-subtag-registry.txt");
        if (SelectedOrUnmanifested(context, variant))
        {
            await foreach (var change in RunPhaseAsync(
                new VariantPhase(this), context, options,
                "iana/language-subtag-registry.txt", variant, ct))
                yield return change;
        }

        string names = Path.Combine(context.EcosystemPath, "iso-639-3_Name_Index.tab");
        if (SelectedOrUnmanifested(context, names))
        {
            await foreach (var change in RunPhaseAsync(
                new NameIndexPhase(this), context, options,
                "iso-639-3_Name_Index.tab", names, ct))
                yield return change;
        }

        IntentStage.ResetContentBank();
    }

    private static int ResolveBatch(DecomposerOptions options) =>
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Iso, options);

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
                langId, LanguageTypeRelation, typeId, Source, TC.StandardsDerived));
        }
        if (rec.RefName.Length > 0)
        {
            var nameId = ContentEmitter.Emit(b, rec.RefName, Source);
            if (nameId is { } nid)
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, NameAliasRelation, nid, Source, TC.StandardsDerived));
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

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        List<IngestFileSpec> files = context.HasArtifactGraph
            ? context.SelectedArtifacts
                .Select(artifact => new IngestFileSpec(
                    artifact.FileLabel, artifact.Path,
                    EtlInventory.EstimateNewlineCount(artifact.Path, ct)))
                .ToList()
            : RelativePhysicalFiles
                .Select(relative => (Relative: relative, Path: Path.Combine(context.EcosystemPath, relative)))
                .Where(static file => File.Exists(file.Path))
                .Select(file => new IngestFileSpec(
                    file.Relative.Replace(Path.DirectorySeparatorChar, '/'), file.Path,
                    EtlInventory.EstimateNewlineCount(file.Path, ct)))
                .ToList();
        if (files.Count == 0) return Task.FromResult<IngestInventory?>(null);
        long total = files.Sum(static file => file.InputUnits);
        long effective = options.MaxInputUnits > 0 ? Math.Min(total, options.MaxInputUnits) : total;
        return Task.FromResult<IngestInventory?>(new IngestInventory("records", effective, files));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default)
    {
        var inventory = await DescribeInputAsync(context, DecomposerOptions.Default, ct).ConfigureAwait(false);
        return inventory?.TotalInputUnits;
    }

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
                    SourceId, BatchLabelPrefix, options, context.Reader, PipelineProfile),
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

    private sealed class RetirementPhase : IsoComposePhase<(string Retired, string Reason, string[] Successors)>
    {
        public RetirementPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/retirements";

        private const string NonExistent = "N";

        protected override void Compose(
            (string Retired, string Reason, string[] Successors) rec, SubstrateChangeBuilder b)
        {
            var retId = LanguageEntityId.FromIso639_3(rec.Retired);
            b.AddEntity(retId, EntityTier.Word, LanguageTypeId, Source);

            if (rec.Reason == NonExistent)
            {
                b.AddAttestation(NativeAttestation.Categorical(
                    retId, LanguageTypeRelation, null, Source, TC.StandardsDerived,
                    confirm: false));
                return;
            }

            foreach (var successor in rec.Successors)
            {
                var sucId = LanguageEntityId.FromIso639_3(successor);
                b.AddEntity(sucId, EntityTier.Word, LanguageTypeId, Source);
                b.AddAttestation(NativeAttestation.Categorical(
                    retId, "SUPERSEDED_BY", sucId, Source, TC.StandardsDerived));
            }
        }

        protected override async IAsyncEnumerable<(string Retired, string Reason, string[] Successors)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            bool hdr = false;
            string path = Path.Combine(ecosystemPath, "iso-639-3_Retirements.tab");
            await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
            {
                if (!hdr) { hdr = true; continue; }
                if (lineMem.Length == 0) continue;
                string line = Encoding.UTF8.GetString(lineMem.Span);
                var c = line.Split('\t');
                if (c.Length < 4) continue;
                string retired = c[0].Trim();
                if (retired.Length != 3) continue;
                string reason = c[2].Trim();
                string changeTo = c[3].Trim();
                string remedy = c.Length > 4 ? c[4].Trim() : "";

                var (reasonOut, successors, keep) =
                    IsoRetirementRemedy.Classify(reason, changeTo, remedy);
                if (!keep) continue;
                yield return (retired, reasonOut, successors);
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
                    lid, NameAliasRelation, nid, Source, TC.StandardsDerived));
        }
        protected override async IAsyncEnumerable<(string Id, string PrintName)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            bool hdr = false;
            string path = Path.Combine(ecosystemPath, "iso-639-3_Name_Index.tab");
            await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
            {
                if (!hdr) { hdr = true; continue; }
                if (lineMem.Length == 0) continue;
                string line = Encoding.UTF8.GetString(lineMem.Span);
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
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            if (lineMem.Length == 0) continue;
            string line = Encoding.UTF8.GetString(lineMem.Span);
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

internal static class IsoRetirementRemedy
{
    internal static (string Reason, string[] Successors, bool Keep) Classify(
        string reason, string changeTo, string remedy)
    {
        if (reason == "N") return (reason, [], true);
        string[] successors =
            changeTo.Length == 3 ? [changeTo] : SuccessorsFromRemedy(remedy);
        return (reason, successors, successors.Length > 0);
    }

    internal static string[] SuccessorsFromRemedy(string remedy)
    {
        if (remedy.Length == 0) return [];
        var found = new List<string>();
        for (int i = 0; i + 4 < remedy.Length + 1; i++)
        {
            if (remedy[i] != '[') continue;
            int close = remedy.IndexOf(']', i + 1);
            if (close != i + 4) continue;
            var code = remedy.AsSpan(i + 1, 3);
            bool lower = true;
            foreach (char ch in code) if (ch is < 'a' or > 'z') { lower = false; break; }
            if (!lower) continue;
            string c = new(code);
            if (!found.Contains(c)) found.Add(c);
        }
        return [.. found];
    }
}
