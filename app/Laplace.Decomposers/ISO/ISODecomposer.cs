using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ISO;

public sealed class ISODecomposer : DecomposerMultiPhase<ISOSource, FullScope>, IIngestInventoryProvider, IIngestArtifactGraphProvider
{
    public static readonly Hash128 Source = ISOSource.SourceId;
    private const string LanguageTypeRelation = "HAS_LANGUAGE_TYPE";

    // One relation per meaning: which code scheme an identifier belongs to and which
    // name a name is are the claim's qualifiers (qualifiers.toml).
    private static readonly Hash128 RelTypeHasExternalId =
        RelationTypeRegistry.RelationTypeId(RelationSymbol.CanonicalFromField(nameof(RelTypeHasExternalId)));
    private static readonly Hash128 RelTypeHasName =
        RelationTypeRegistry.RelationTypeId(RelationSymbol.CanonicalFromField(nameof(RelTypeHasName)));
    private static readonly Mask256 Iso6391 = ClaimQualifiers.Of("identifier", "iso639-1");
    private static readonly Mask256 Iso6392B = ClaimQualifiers.Of("identifier", "iso639-2b");
    private static readonly Mask256 Iso6392T = ClaimQualifiers.Of("identifier", "iso639-2t");
    private static readonly Mask256 ReferenceName = ClaimQualifiers.Of("name", "reference");
    private static readonly Mask256 PrintName = ClaimQualifiers.Of("name", "print");

    public static readonly Hash128 TrustClass = ISOSource.TrustClass;

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;
    private static readonly Hash128 Iso639CodeTypeId = EntityTypeRegistry.Iso639Code;
    private static readonly Hash128 RelTypeIsLanguageCode =
        RelationTypeRegistry.RelationTypeId("IS_LANGUAGE_CODE");
    private static readonly Hash128 RelTypeHasScript =
        RelationTypeRegistry.RelationTypeId(RelationSymbol.CanonicalFromField(nameof(RelTypeHasScript)));
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
        "ISO-639-2_utf-8.txt",
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
        string root = context.EcosystemPath;
        var foundation = new List<ArtifactPhaseWork>();
        var dependents = new List<ArtifactPhaseWork>();

        void Add(List<ArtifactPhaseWork> level, string relative, IDecomposer phase)
        {
            string path = Path.Combine(root, relative);
            if (SelectedOrUnmanifested(context, path))
                level.Add(new ArtifactPhaseWork(phase, relative.Replace('\\', '/'), path));
        }

        Add(foundation, "iso-639-3.tab", new Iso6393Phase(this));
        Add(dependents, "iso-639-3-macrolanguages.tab", new MacrolanguagePhase(this));
        Add(dependents, Path.Combine("cldr", "supplementalData.xml"), new ScriptPhase(this));
        Add(dependents, "iso-639-3_Retirements.tab", new RetirementPhase(this));
        Add(dependents, Path.Combine("iana", "language-subtag-registry.txt"), new VariantPhase(this));
        Add(dependents, "iso-639-3_Name_Index.tab", new NameIndexPhase(this));
        Add(dependents, "ISO-639-2_utf-8.txt", new Iso6392Phase(this));

        await foreach (SubstrateChange change in RunArtifactDependencyLevelsAsync(
                           [foundation, dependents], context, options,
                           "iso639/dependency", ct).ConfigureAwait(false))
            yield return change;

        IntentStage.ResetContentBank();
    }

    private static int ResolveBatch(DecomposerOptions options) =>
        IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Iso, options);

    internal void StageIsoTabRecord(IsoRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageReference.EmitResolvedCode(
            b, rec.Id, Source, TC.StandardsDerived);
        _codeNames.Add(rec.Id.ToLowerInvariant());
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeIsLanguageCode, null, Source, null,
            RelationTypeRank.StandardsStructural * TC.StandardsDerived));

        if (rec.Part1.Length > 0)
        {
            string iso1 = rec.Part1.Trim().ToLowerInvariant();
            _codeNames.Add(iso1);
            var iso1Id = ContentEmitter.Emit(b, iso1, Source)
                ?? throw new InvalidOperationException($"ISO 639-1 code could not be composed: {iso1}");
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                langId, RelTypeHasExternalId, iso1Id, Source, null, TC.StandardsDerived)
                with { QualifierMask = Iso6391 });
        }

        // One claim per distinct ISO 639-2 code, qualified by the scheme(s) it serves.
        string part2b = rec.Part2b.Trim().ToLowerInvariant();
        string part2t = rec.Part2t.Trim().ToLowerInvariant();
        foreach (var (iso2, scheme) in part2b == part2t
                     ? new[] { (part2b, Iso6392B | Iso6392T) }
                     : new[] { (part2b, Iso6392B), (part2t, Iso6392T) })
        {
            if (iso2.Length == 0) continue;
            _codeNames.Add(iso2);
            var iso2Id = ContentEmitter.Emit(b, iso2, Source)
                ?? throw new InvalidOperationException($"ISO 639-2 code could not be composed: {iso2}");
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                langId, RelTypeHasExternalId, iso2Id, Source, null, TC.StandardsDerived)
                with { QualifierMask = scheme });
        }
        if (rec.Scope.Length > 0)
        {
            string scope = rec.Scope.Trim();
            var scopeId = ContentEmitter.Emit(b, scope, Source)
                ?? throw new InvalidOperationException($"language scope could not be composed: {scope}");
            b.AddAttestation(NativeAttestation.Categorical(
                langId, "HAS_LANGUAGE_SCOPE", scopeId, Source, TC.StandardsDerived));
        }
        if (rec.Type.Length > 0)
        {
            string languageType = rec.Type.Trim();
            var typeId = ContentEmitter.Emit(b, languageType, Source)
                ?? throw new InvalidOperationException($"language type could not be composed: {languageType}");
            b.AddAttestation(NativeAttestation.Categorical(
                langId, LanguageTypeRelation, typeId, Source, TC.StandardsDerived));
        }
        if (rec.RefName.Length > 0)
        {
            var nameId = ContentEmitter.Emit(b, rec.RefName, Source);
            if (nameId is { } nid)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    langId, RelTypeHasName, nid, Source, null, TC.StandardsDerived)
                    with { QualifierMask = ReferenceName });
        }
    }

    internal void StageScriptRecord(ScriptRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageReference.EmitResolvedCode(
            b, rec.LanguageCode, Source, TC.StandardsDerived);
        string script = rec.ScriptName.Trim();
        var scriptId = ContentEmitter.Emit(b, script, Source)
            ?? throw new InvalidOperationException($"script name could not be composed: {script}");
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeHasScript, scriptId, Source, null,
            RelationTypeRank.StandardsStructural * TC.StandardsDerived));
    }

    internal void StageVariantRecord((string Subtag, string ParentCode) rec, SubstrateChangeBuilder b)
    {
        string subtag = rec.Subtag.Trim();
        var variantId = ContentEmitter.Emit(b, subtag, Source)
            ?? throw new InvalidOperationException($"language variant could not be composed: {subtag}");
        var parentId = LanguageReference.EmitResolvedCode(
            b, rec.ParentCode, Source, TC.StandardsDerived);
        b.AddAttestation(NativeAttestation.Categorical(
            variantId, "HAS_VARIANT_OF", parentId, Source, TC.StandardsDerived));
    }

    public Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(ecosystemPath))
            return Task.FromResult<IngestArtifactGraph?>(null);

        string root = Path.GetFullPath(ecosystemPath);
        var admitted = RelativePhysicalFiles
            .Select(static relative => relative.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = new List<IngestArtifact>();

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                      .OrderBy(static p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(file);
            string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            IngestArtifactDisposition disposition;
            string notes;

            if (admitted.Contains(relative))
            {
                disposition = IngestArtifactDisposition.Admitted;
                notes = "";
            }
            else if (IsEquivalentIsoPackaging(relative, root))
            {
                disposition = IngestArtifactDisposition.EquivalentPackaging;
                notes = "archive/extracted packaging duplicates an admitted ISO language artifact";
            }
            else if (IsIsoControlArtifact(relative))
            {
                disposition = IngestArtifactDisposition.ExcludedWithReason;
                notes = "release/cache/provenance control artifact; retained but not admitted as language testimony";
            }
            else if (IsAlternateLanguageEstateArtifact(relative))
            {
                disposition = IngestArtifactDisposition.ExcludedWithReason;
                notes = "alternate/reference packaging outside the canonical ISO foundation inputs; retained for provenance but not double-admitted";
            }
            else
            {
                disposition = IngestArtifactDisposition.Unsupported;
                notes = "physical ISO/IANA/CLDR/Glottolog artifact is installed but has no semantic handler yet";
            }

            var info = new FileInfo(full);
            artifacts.Add(new IngestArtifact(
                ISOSource.SourceName,
                "installed",
                relative,
                relative,
                full,
                disposition,
                UpstreamUrl: "",
                FetchedAtUtc: "",
                Bytes: info.Length,
                Sha256: "",
                UpstreamChecksum: "",
                MediaType: IsoMediaType(relative),
                License: "",
                Citation: "",
                Language: "",
                Split: "",
                AnnotationOrigin: "language-standard",
                Notes: notes,
                JournalLabel: $"iso639/{relative}",
                ModifiedAt: info.LastWriteTimeUtc));
        }

        return Task.FromResult<IngestArtifactGraph?>(new IngestArtifactGraph(artifacts));
    }

    private static bool IsEquivalentIsoPackaging(string relative, string root)
    {
        string first = relative.Split('/', 2)[0];
        if (first.StartsWith("iso-639-3_Code_Tables_", StringComparison.Ordinal)
            && relative.Contains('/', StringComparison.Ordinal))
        {
            string name = Path.GetFileName(relative);
            if (File.Exists(Path.Combine(root, name))) return true;
        }

        if (relative.StartsWith("iso-639-3_Code_Tables_", StringComparison.Ordinal)
            && relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Directory.EnumerateDirectories(
                    root, "iso-639-3_Code_Tables_*", SearchOption.TopDirectoryOnly)
                .Any();

        return false;
    }

    private static bool IsAlternateLanguageEstateArtifact(string relative) =>
        relative.StartsWith("cldr/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("loc/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("sil/change_request", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("sil/change_requests/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals("iso639-5.atom10.xml", StringComparison.OrdinalIgnoreCase)
        || relative.Equals("iso639-5.rss20.xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsIsoControlArtifact(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals("checkpoint.bin", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || name.Contains("readme", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("copyright", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("index.html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);
    }

    private static string IsoMediaType(string relative) =>
        Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".txt" or ".tab" or ".csv" => "text/plain",
            ".xml" => "application/xml",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream",
        };

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

    internal readonly record struct ScriptRecord(string LanguageCode, string ScriptName);

    internal readonly record struct Iso6392Record(
        string Bibliographic,
        string Terminological,
        string Part1,
        string English,
        string French,
        string? LanguageCode);

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
            var indivId = LanguageReference.EmitResolvedCode(b, rec.Indiv, Source, TC.StandardsDerived);
            var macroId = LanguageReference.EmitResolvedCode(b, rec.Macro, Source, TC.StandardsDerived);
            // A macrolanguage has its individual languages as members: HAS_PART read from
            // the member's side, with meronymy/member.
            b.AddAttestation(NativeAttestation.Categorical(
                indivId, ISOSource.MemberRelation, macroId, Source, null,
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
            var languageAliases = LanguageGraph.LoadIso6393Aliases(ecosystemPath);
            foreach (var (subtag, scriptCodes) in LanguageGraph.LanguageScripts(ecosystemPath))
            {
                ct.ThrowIfCancellationRequested();
                string? languageCode =
                    LanguageGraph.ResolveIso6393Code(languageAliases, subtag);
                if (languageCode is null) continue;
                foreach (var code in scriptCodes)
                {
                    if (!scriptName.TryGetValue(code, out var name)) continue;
                    yield return new ScriptRecord(languageCode, name);
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
            var retId = LanguageReference.EmitResolvedCode(b, rec.Retired, Source, TC.StandardsDerived);

            if (rec.Reason == NonExistent)
            {
                b.AddAttestation(NativeAttestation.Categorical(
                    retId, LanguageTypeRelation, null, Source, TC.StandardsDerived,
                    confirm: false));
                return;
            }

            foreach (var successor in rec.Successors)
            {
                var sucId = LanguageReference.EmitResolvedCode(b, successor, Source, TC.StandardsDerived);
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
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                               path, "tsv", GrammarRecordFraming.Line, ct))
            {
                if (!hdr) { hdr = true; continue; }
                if (fields.Length < 4) continue;
                string retired = fields[0].Trim();
                if (retired.Length != 3) continue;
                string reason = fields[2].Trim();
                string changeTo = fields[3].Trim();
                string remedy = fields.Length > 4 ? fields[4].Trim() : "";

                var (reasonOut, successors, keep) =
                    IsoRetirementRemedy.Classify(reason, changeTo, remedy);
                if (!keep) continue;
                yield return (retired, reasonOut, successors);
            }
        }
    }

    private sealed class VariantPhase : IsoComposePhase<(string Subtag, string ParentCode)>
    {
        public VariantPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/variants";
        protected override void Compose((string Subtag, string ParentCode) rec, SubstrateChangeBuilder b) =>
            Owner.StageVariantRecord(rec, b);
        protected override async IAsyncEnumerable<(string Subtag, string ParentCode)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var languageAliases = LanguageGraph.LoadIso6393Aliases(ecosystemPath);
            foreach (var (subtag, prefixes) in LanguageGraph.Variants(ecosystemPath))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var prefix in prefixes)
                {
                    string? parentCode =
                        LanguageGraph.ResolveIso6393Code(languageAliases, prefix);
                    if (parentCode is not null)
                        yield return (subtag, parentCode);
                }
            }
            await Task.CompletedTask;
        }
    }

    private sealed class Iso6392Phase : IsoComposePhase<Iso6392Record>
    {
        public Iso6392Phase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639-2";

        protected override void Compose(Iso6392Record rec, SubstrateChangeBuilder b)
        {
            Hash128? languageId = rec.LanguageCode is { Length: 3 } languageCode
                ? LanguageReference.EmitResolvedCode(b, languageCode, Source, TC.StandardsDerived)
                : null;

            // One claim per distinct code: a code that is both the bibliographic and the
            // terminologic code is "lang HAS_EXTERNAL_ID code {iso639-2b, iso639-2t}".
            string bibliographic = rec.Bibliographic.ToLowerInvariant();
            string terminologic = rec.Terminological.ToLowerInvariant();
            if (bibliographic == terminologic)
                StageCode(bibliographic, Iso6392B | Iso6392T);
            else
            {
                StageCode(bibliographic, Iso6392B);
                StageCode(terminologic, Iso6392T);
            }

            void StageCode(string canonical, Mask256 scheme)
            {
                if (canonical.Length != 3) return;
                Owner._codeNames.Add(canonical);
                Hash128 codeId = ContentEmitter.Emit(b, canonical, Source)
                    ?? throw new InvalidOperationException($"ISO 639-2 code could not be composed: {canonical}");

                if (ContentEmitter.Emit(b, rec.English, Source) is { } english)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        codeId, RelTypeHasName, english, Source, null, TC.StandardsDerived)
                        with { QualifierMask = ReferenceName });
                if (ContentEmitter.Emit(b, rec.French, Source) is { } french)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        codeId, RelTypeHasName, french, Source, null, TC.StandardsDerived)
                        with { QualifierMask = ReferenceName });

                if (languageId is { } lid)
                {
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        lid, RelTypeHasExternalId, codeId, Source, null, TC.StandardsDerived)
                        with { QualifierMask = scheme });
                }
            }
        }

        protected override async IAsyncEnumerable<Iso6392Record> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var languageAliases = LanguageGraph.LoadIso6393Aliases(ecosystemPath);
            string path = Path.Combine(ecosystemPath, "ISO-639-2_utf-8.txt");
            await foreach (var (fields, _) in GrammarRowReader.ReadDelimitedFieldsAsync(path, '|', ct))
            {
                if (fields.Length < 5) continue;
                string b = fields[0].Trim().ToLowerInvariant();
                string t = fields[1].Trim().ToLowerInvariant();
                string p1 = fields[2].Trim().ToLowerInvariant();
                string en = fields[3].Trim();
                string fr = fields[4].Trim();
                if (b.Length != 3) continue;

                string? languageCode =
                    LanguageGraph.ResolveIso6393Code(languageAliases, t)
                    ?? LanguageGraph.ResolveIso6393Code(languageAliases, b)
                    ?? LanguageGraph.ResolveIso6393Code(languageAliases, p1);
                yield return new Iso6392Record(b, t, p1, en, fr, languageCode);
            }
        }
    }

    private sealed class NameIndexPhase : IsoComposePhase<(string Id, string PrintName)>
    {
        public NameIndexPhase(ISODecomposer owner) : base(owner) { }
        protected override string PhaseLabel => "iso639/names";
        protected override void Compose((string Id, string PrintName) rec, SubstrateChangeBuilder b)
        {
            var lid = LanguageReference.EmitResolvedCode(b, rec.Id, Source, TC.StandardsDerived);
            if (ContentEmitter.Emit(b, rec.PrintName, Source) is { } nid)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    lid, RelTypeHasName, nid, Source, null, TC.StandardsDerived)
                    with { QualifierMask = PrintName });
        }
        protected override async IAsyncEnumerable<(string Id, string PrintName)> ExtractRecordsAsync(
            string ecosystemPath, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            bool hdr = false;
            string path = Path.Combine(ecosystemPath, "iso-639-3_Name_Index.tab");
            await foreach (var (fields, _) in GrammarRowReader.ReadFieldsAsync(
                               path, "tsv", GrammarRecordFraming.Line, ct))
            {
                if (!hdr) { hdr = true; continue; }
                if (fields.Length < 2) continue;
                string id = fields[0].Trim(), printName = fields[1].Trim();
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
        await foreach (var (parts, _) in GrammarRowReader.ReadFieldsAsync(
                           path, "tsv", GrammarRecordFraming.Line, ct))
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
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
