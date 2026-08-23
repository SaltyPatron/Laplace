using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ISO;

public sealed class ISODecomposer : DecomposerMultiPhase<ISOSource, FullScope>
{
    public static readonly Hash128 Source = ISOSource.SourceId;
    // ONE SPELLING. Two sites emit this relation -- the ISO 639-3 reference name and the
    // name-index print name -- and both arrived when a misuse of HAS_DEFINITION was
    // corrected: a name attested as a definition rendered "Batui HAS_DEFINITION Batui",
    // subject and object the same string, a claim that cannot be false. The correction was
    // right and it left the literal written twice, which g3_csharp reads as growth even
    // though the file's total fell 500 -> 499. Named once so the ratchet measures what
    // actually happened.
    private const string NameAliasRelation = "HAS_NAME_ALIAS";

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
                langId, "HAS_LANGUAGE_TYPE", typeId, Source, TC.StandardsDerived));
        }
        if (rec.RefName.Length > 0)
        {
            var nameId = ContentEmitter.Emit(b, rec.RefName, Source);
            if (nameId is { } nid)
            {
                // A NAME IS NOT A DEFINITION. rec.RefName is ISO 639-3's reference name for
                // the language; attesting it as HAS_DEFINITION as well produced rows that
                // render "Batui HAS_DEFINITION Batui" — the subject and object are the same
                // string, so the claim cannot be false and carries nothing. ISO 639-3
                // publishes no glosses; the alias below is the whole of what the source says.
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, NameAliasRelation, nid, Source, TC.StandardsDerived));
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

    /// <summary>
    /// ISO 639-3 retires codes for five stated reasons, and the phase used to require a
    /// 3-character Change_To, which dropped 174 of the corpus's 386 retirement rows:
    ///
    ///   N  non-existent  72 rows -- the standard says the code names NO real language.
    ///                    Change_To is empty by construction; there is nothing to point at.
    ///                    This is ISO refuting its own earlier assertion, and it was the
    ///                    single largest block of negative evidence the source states.
    ///   S  split        102 rows -- the code split into several successors, so Change_To is
    ///                    empty and the targets live in Ret_Remedy as bracketed codes
    ///                    ("Split into ... [sfb], and ... [vgt]"). Every one was lost.
    ///   C/D/M          212 rows -- one successor in Change_To; these already worked.
    ///
    /// The N arm folds an object-null REFUTE against HAS_LANGUAGE_TYPE: the relation holds
    /// for no object because ISO says the language is not there. Absence of a row would have
    /// meant UNKNOWN (spec 05); ISO said something stronger, and the substrate now carries it
    /// as evidence that contradicts any other source asserting the code is a language.
    /// </summary>
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
                    retId, "HAS_LANGUAGE_TYPE", null, Source, TC.StandardsDerived,
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

                if (reason == NonExistent) { yield return (retired, reason, []); continue; }

                string[] successors =
                    changeTo.Length == 3 ? [changeTo] : IsoRetirementRemedy.SuccessorsFromRemedy(remedy);
                if (successors.Length == 0) continue;
                yield return (retired, reason, successors);
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
            // Same defect as the RefName site above: PrintName is a NAME. It was the only
            // thing this phase deposited, so the fix is to record it as what it is rather
            // than to drop it — the language keeps its printed name, as an alias.
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

/// <summary>
/// ISO 639-3 names split-retirement successors only inside Ret_Remedy, as bracketed
/// 3-letter codes. A pure function, lifted out of the phase so it is testable without
/// widening the phase hierarchy's accessibility.
/// </summary>
internal static class IsoRetirementRemedy
{
    /// Ret_Remedy names split targets as bracketed 3-letter codes, e.g.
    /// "Split into five languages: Nong Zhuang [zhn];  Yang Zhuang [zyg]; ...".
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
