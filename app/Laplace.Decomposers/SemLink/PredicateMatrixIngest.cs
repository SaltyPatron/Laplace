using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

internal static class PredicateMatrixIngest
{
    // PredicateMatrix is a DISTINCT resource from SemLink's own JSON maps — it independently
    // ties VN class + FN frame + PB roleset + WN sense + MCR/ILI per row. Stamping its rows
    // with this dedicated source (not the SemLink source) lets consensus see PM and SemLink as
    // two witnesses corroborating the same VN↔FN↔synset links, which is the whole point of the
    // EVIDENCE layer. Its source id is registered as an entity in SemLinkDecomposer.InitializeAsync
    // so the attestations' source_id FK is satisfied. See docs/specs/16 §3a.
    internal static readonly Hash128 Source = PredicateMatrixSource.SourceId;
    internal static readonly Hash128 TrustClass = PredicateMatrixSource.TrustClass;

    private const int ColLang = 0;
    private const int ColPos = 1;
    private const int ColPredicate = 2;
    private const int ColPredicateRole = 3;
    private const int ColVnClass = 4;
    private const int ColVnSubclass = 6;
    private const int ColVnLemma = 8;
    private const int ColVnRole = 9;
    private const int ColWnSense = 10;
    private const int ColMcrIli = 11;
    private const int ColFnFrame = 12;
    private const int ColFnLu = 13;
    private const int ColFnFe = 14;
    private const int ColPbRoleset = 15;
    private const int ColPbArg = 16;
    private const int ColMcrBaseConcept = 17;
    private const int ColMcrDomain = 18;
    private const int ColMcrSumo = 19;
    private const int ColMcrTopOntology = 20;
    private const int ColMcrLexname = 21;
    private const int ColMcrBlc = 22;
    private const int ColSenseFrequency = 23;
    private const int ColSynsetRelationCount = 24;
    private const int ColEsoClass = 25;
    private const int ColEsoRole = 26;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;
    private static readonly Hash128 FrameLuTypeId = EntityTypeRegistry.FrameNetLu;

    internal static async IAsyncEnumerable<PredicateMatrixRecord> EnumerateRecordsAsync(
        string path,
        LanguageFilter? langs,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool skippedHeader = false;
        long rowsTotal = 0;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (lineMem.Length == 0) continue;

            // First non-empty line is the column header (same as the old StreamReader path).
            if (!skippedHeader)
            {
                skippedHeader = true;
                continue;
            }

            string line = Encoding.UTF8.GetString(lineMem.Span);
            if (!TryParseRecord(line.Split('\t'), langs, out var record)) continue;
            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;
            yield return record;
        }
    }

    internal static async Task<long?> EstimateRecordCountAsync(
        string path, LanguageFilter? langs, CancellationToken ct)
    {
        if (langs is not { IsActive: true })
        {
            long lines = EtlInventory.EstimateNewlineCount(path, ct);
            return lines > 1 ? lines - 1 : null;
        }

        bool skippedHeader = false;
        long count = 0;
        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (lineMem.Length == 0) continue;
            if (!skippedHeader)
            {
                skippedHeader = true;
                continue;
            }

            int tab = lineMem.Span.IndexOf((byte)'\t');
            if (tab <= 0) continue;
            string language = SourceEntityIdConventions.StripPredicateMatrixNamespace(
                Encoding.UTF8.GetString(lineMem.Span[..tab]));
            if (langs.MatchesRaw(language)) count++;
        }
        return count > 0 ? count : null;
    }

    private static bool TryParseRecord(
        string[] fields, LanguageFilter? langs, out PredicateMatrixRecord record)
    {
        record = default;
        if (fields.Length <= ColPbArg) return false;
        if (fields[ColLang].Equals("1_ID_LANG", StringComparison.Ordinal)) return false;

        string? lang = Field(fields, ColLang);
        string? pos = Field(fields, ColPos);
        string? predicate = Field(fields, ColPredicate);
        string? predicateRole = Field(fields, ColPredicateRole);
        if (lang is null || pos is null || predicate is null || predicateRole is null) return false;
        if (langs is { IsActive: true } && !langs.MatchesRaw(lang)) return false;

        Hash128? synId = SynsetAnchor(fields[ColMcrIli]);
        string? wnSenseRaw = Field(fields, ColWnSense);
        Hash128? senseId = wnSenseRaw is null ? null : SenseAnchor.Id(wnSenseRaw);
        var edges = new List<PredicateMatrixEdge>(7);

        string? roleset = Field(fields, ColPbRoleset);
        if (roleset is not null && synId is { } rsSynset)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rsSynset)));
            if (senseId is { } rs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rs)));
        }

        string? frame = Field(fields, ColFnFrame);
        if (frame is not null && synId is { } fnSynset)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(frame, FrameTypeId, fnSynset)));
            if (senseId is { } fs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(frame, FrameTypeId, fs)));
        }

        string? vnClass = VerbNetClassKey(fields);
        if (vnClass is not null && synId is { } vnSynset)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vnSynset)));
            if (senseId is { } vs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vs)));
        }

        string? vnRole = Field(fields, ColVnRole);
        string? fnFe = Field(fields, ColFnFe);
        if (vnClass is not null && vnRole is not null && fnFe is not null && frame is not null)
        {
            edges.Add(PredicateMatrixEdge.FromRole(new RoleCorrespondenceRecord(
                vnClass, VnClassTypeId, vnRole,
                frame, FrameTypeId, fnFe)));
        }

        record = new PredicateMatrixRecord(
            lang, pos, predicate, predicateRole,
            synId, senseId,
            vnClass, vnRole,
            frame, Field(fields, ColFnLu), fnFe,
            roleset, Field(fields, ColPbArg),
            Field(fields, ColMcrBaseConcept), Field(fields, ColMcrDomain),
            Field(fields, ColMcrSumo), MultiField(fields, ColMcrTopOntology),
            Field(fields, ColMcrLexname), SynsetAnchor(FieldRaw(fields, ColMcrBlc)),
            Field(fields, ColSenseFrequency), Field(fields, ColSynsetRelationCount),
            Field(fields, ColEsoClass), Field(fields, ColEsoRole),
            edges.ToArray());
        return true;
    }

    private static readonly string[] UnpackDirs = ["PredicateMatrix", "predicate-matrix", "PredicateMatrix.v1.3"];

    private static readonly IngestSourceLayout Layout = new()
    {
        // Canonical name first: SemLinkDecomposer ingests the FIRST path only, so the
        // versioned siblings the glob also matches must not outrank PredicateMatrix.txt.
        Files = [IngestFileMatch.Name("PredicateMatrix.txt"), IngestFileMatch.Glob("PredicateMatrix*.txt")],
        EcosystemDirs = [".", "instances", Path.Combine("semlink-master", "instances"), .. UnpackDirs],
        RootDirs = UnpackDirs,
        NestedDirs = UnpackDirs,
        SearchIngestRoots = true,
        IncludeEcosystemParent = true,
    };

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static bool ExistsLocally(string dir) => IngestInput.FilesIn(dir, Layout).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout);

    private static string? VerbNetClassKey(string[] fields)
    {
        string? lemma = Field(fields, ColVnLemma);
        if (lemma is null) return null;

        string? subclass = Field(fields, ColVnSubclass);
        if (subclass is not null)
            return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{subclass}");

        string? cls = Field(fields, ColVnClass);
        if (cls is null) return null;
        return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{cls}");
    }

    private static string? Field(string[] fields, int index)
    {
        if ((uint)index >= (uint)fields.Length) return null;
        string s = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[index]).Trim();
        return s.Length == 0 || s.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : s.Normalize(NormalizationForm.FormC);
    }

    private static string FieldRaw(string[] fields, int index) =>
        (uint)index < (uint)fields.Length ? fields[index] : string.Empty;

    private static string[] MultiField(string[] fields, int index) =>
        Field(fields, index) is { } value
            ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    private static Hash128? SynsetAnchor(string raw)
    {
        var parsed = SourceEntityIdConventions.ParseMcrSynsetKey(raw);
        return parsed is null
            ? null
            : ConceptAnchor.SynsetId(parsed.Value.Offset, parsed.Value.SsType,
                                      parsed.Value.WnVersion ?? "pwn30");
    }

    internal readonly record struct PredicateMatrixEdge(
        CategoryCorrespondenceRecord? Category,
        RoleCorrespondenceRecord? Role)
    {
        public static PredicateMatrixEdge FromCategory(CategoryCorrespondenceRecord c) => new(c, null);
        public static PredicateMatrixEdge FromRole(RoleCorrespondenceRecord r) => new(null, r);
    }

    internal readonly record struct PredicateMatrixRecord(
        string Language,
        string Pos,
        string PredicateKey,
        string PredicateRoleKey,
        Hash128? SynsetId,
        Hash128? SenseId,
        string? VerbNetClass,
        string? VerbNetRole,
        string? Frame,
        string? FrameLu,
        string? FrameFe,
        string? PropBankRoleset,
        string? PropBankArg,
        string? McrBaseConcept,
        string? McrDomain,
        string? McrSumo,
        string[] McrTopOntology,
        string? McrLexname,
        Hash128? McrBlcId,
        string? SenseFrequency,
        string? SynsetRelationCount,
        string? EsoClass,
        string? EsoRole,
        PredicateMatrixEdge[] Edges);

    internal static IIngestRecordHandler<PredicateMatrixRecord> CreateRecordHandler(double trust) =>
        new PredicateMatrixRecordHandler(Source, trust);

    internal static Hash128? PredicateId(string language, string pos, string predicateKey) =>
        ReferenceAnchor.Id(
            ReferenceIdentityKind.PredicateMatrixPredicate,
            PredicateIdentityKey(language, pos, predicateKey));

    internal static Hash128? PredicateRoleId(
        string language, string pos, string predicateKey, string roleKey) =>
        PredicateId(language, pos, predicateKey) is { } predicateId
            ? RoleAnchor.Id(RoleIdentityKind.PredicateMatrix, predicateId, roleKey)
            : null;

    private static string PredicateIdentityKey(string language, string pos, string predicateKey) =>
        $"{language}\0{pos}\0{predicateKey}";

    private sealed class PredicateMatrixRecordHandler : IIngestRecordHandler<PredicateMatrixRecord>
    {
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        // DrainInto is deliberately serial for this direct-admission handler. Plain sets avoid
        // paying ConcurrentDictionary synchronization on every projected field while retaining
        // run-wide suppression of repeated package projections.
        private readonly HashSet<Hash128> _declarations = new();
        private readonly HashSet<Hash128> _roleEntities = new();
        private readonly HashSet<Hash128> _relations = new();

        public PredicateMatrixRecordHandler(Hash128 sourceId, double trust)
        {
            _sourceId = sourceId;
            _trust = trust;
            LanguageReference.EnsureLoaded();
        }

        // Parsing already produced the row's compact projection. All admission is a cheap,
        // ordered builder drain, so consuming compose-worker slots here would be fake fan-out.
        public bool ParallelizeDeferredUnitCreation => false;

        public IIngestDeferredUnit CreateDeferredUnit(PredicateMatrixRecord record) =>
            new Unit(this, record);

        public void WalkWitness(
            PredicateMatrixRecord record, Hash128 root,
            SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
        { }

        private Hash128 Drain(PredicateMatrixRecord record, SubstrateChangeBuilder builder)
        {
            Hash128? predicateId = EmitReference(
                ReferenceIdentityKind.PredicateMatrixPredicate,
                PredicateIdentityKey(record.Language, record.Pos, record.PredicateKey),
                EntityTypeRegistry.PredicateMatrixPredicate,
                builder);
            if (predicateId is null) return default;

            Hash128 languageId = LanguageReference.Resolve(record.Language);
            Hash128 posId = PosReference.Resolve(record.Pos, PosReference.PosTagset.WordNet);
            AddRelation(predicateId.Value, PredicateMatrixSource.HasLanguageTypeId, languageId, builder);
            AddRelation(predicateId.Value, PredicateMatrixSource.HasPosTypeId, posId, builder);

            Hash128? predicateRoleId = EmitScopedRole(
                RoleIdentityKind.PredicateMatrix, predicateId.Value, record.PredicateRoleKey,
                EntityTypeRegistry.PredicateMatrixRole, builder);

            if (record.SynsetId is { } synset)
                AddCorrespondence(predicateId.Value, synset, builder);
            if (record.SenseId is { } sense)
                AddCorrespondence(predicateId.Value, sense, builder);

            Hash128? vnClassId = record.VerbNetClass is { } vnClass
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    vnClass, VnClassTypeId, predicateId.Value), builder)
                : null;
            Hash128? frameId = record.Frame is { } frame
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    frame, FrameTypeId, predicateId.Value), builder)
                : null;
            if (record.FrameLu is { } frameLu)
                _ = EmitCategory(new CategoryCorrespondenceRecord(
                    frameLu, FrameLuTypeId, predicateId.Value), builder);
            Hash128? rolesetId = record.PropBankRoleset is { } roleset
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    roleset, RolesetTypeId, predicateId.Value), builder)
                : null;

            if (predicateRoleId is { } pmRole)
            {
                if (vnClassId is { } vnParent && record.VerbNetRole is { } vnRole)
                    AddRoleCorrespondence(pmRole, EmitScopedRole(
                        RoleIdentityKind.VerbNet, vnParent, vnRole,
                        EntityTypeRegistry.VerbNetRole, builder), builder);
                if (frameId is { } fnParent && record.FrameFe is { } frameFe)
                    AddRoleCorrespondence(pmRole, EmitScopedRole(
                        RoleIdentityKind.FrameNet, fnParent, frameFe,
                        EntityTypeRegistry.FrameNetFe, builder), builder);
                if (rolesetId is { } pbParent && NormalizePropBankRole(record.PropBankArg) is { } pbRole)
                    AddRoleCorrespondence(pmRole, EmitScopedRole(
                        RoleIdentityKind.PropBank, pbParent, pbRole,
                        EntityTypeRegistry.PropBankRole, builder), builder);
            }

            Hash128 propertySubject = record.SynsetId ?? predicateId.Value;
            if (record.McrDomain is { } domain)
            {
                Hash128? domainId = EmitVocabulary(
                    "mcr-domain", domain, EntityTypeRegistry.McrDomain, builder);
                AddRelation(propertySubject, PredicateMatrixSource.HasDomainTopicTypeId, domainId, builder);
            }
            if (record.McrSumo is { } sumo)
                AddCorrespondence(propertySubject, EmitVocabulary(
                    "mcr-sumo", sumo, EntityTypeRegistry.McrSumo, builder), builder);
            foreach (string top in record.McrTopOntology)
                AddCorrespondence(propertySubject, EmitVocabulary(
                    "mcr-top", top, EntityTypeRegistry.McrTopOntology, builder), builder);
            if (record.McrLexname is { } lexname)
            {
                Hash128? lexnameId = EmitVocabulary(
                    "mcr-lexname", lexname, EntityTypeRegistry.McrLexname, builder);
                AddRelation(propertySubject, PredicateMatrixSource.HasLexCategoryTypeId, lexnameId, builder);
            }
            if (record.McrBlcId is { } blc)
                AddCorrespondence(propertySubject, blc, builder);

            Hash128 annotationSubject = record.SenseId ?? propertySubject;
            AddAnnotation(
                annotationSubject, PredicateMatrixSource.HasBaseConceptStatusTypeId,
                "mcr-base-concept", record.McrBaseConcept, builder);
            AddAnnotation(
                annotationSubject, PredicateMatrixSource.HasSenseFrequencyTypeId,
                "wn-sense-frequency", record.SenseFrequency, builder);
            AddAnnotation(
                propertySubject, PredicateMatrixSource.HasSynsetRelationCountTypeId,
                "wn-synset-relation-count", record.SynsetRelationCount, builder);

            Hash128? esoClassId = record.EsoClass is { } esoClass
                ? EmitVocabulary("eso-class", esoClass, EntityTypeRegistry.EsoClass, builder)
                : null;
            AddCorrespondence(predicateId.Value, esoClassId, builder);
            if (predicateRoleId is { } matrixRole
                && esoClassId is { } esoParent
                && record.EsoRole is { } esoRole)
            {
                AddRoleCorrespondence(matrixRole, EmitScopedRole(
                    RoleIdentityKind.Eso, esoParent, esoRole,
                    EntityTypeRegistry.EsoRole, builder), builder);
            }

            foreach (var edge in record.Edges)
            {
                _ = edge.Category is { } category
                    ? EmitCategory(category, builder)
                    : EmitRole(edge.Role!.Value, builder);
            }
            return predicateId.Value;
        }

        private Hash128? EmitReference(
            ReferenceIdentityKind kind,
            string key,
            Hash128 entityTypeId,
            SubstrateChangeBuilder builder)
        {
            Hash128? id = ReferenceAnchor.Id(kind, key);
            if (id is null) return null;
            Hash128 declarationId = CategoryAnchor.CategoryAttestationId(
                id.Value, entityTypeId, _sourceId);
            if (_declarations.Add(declarationId))
                id = ReferenceAnchor.Emit(builder, kind, key, entityTypeId, _sourceId, _trust);
            return id;
        }

        private Hash128? EmitVocabulary(
            string field, string value, Hash128 entityTypeId, SubstrateChangeBuilder builder) =>
            EmitReference(
                ReferenceIdentityKind.PredicateMatrixVocabulary,
                $"{field}\0{value}", entityTypeId, builder);

        private Hash128? EmitScopedRole(
            RoleIdentityKind kind,
            Hash128 parentId,
            string roleKey,
            Hash128 entityTypeId,
            SubstrateChangeBuilder builder)
        {
            Hash128? id = RoleAnchor.Id(kind, parentId, roleKey);
            if (id is null) return null;
            if (_roleEntities.Add(id.Value))
                id = RoleAnchor.Declare(builder, kind, parentId, roleKey, entityTypeId, _sourceId);
            return id;
        }

        private void AddAnnotation(
            Hash128 subjectId,
            Hash128 relationTypeId,
            string field,
            string? value,
            SubstrateChangeBuilder builder)
        {
            if (value is null) return;
            Hash128? valueId = EmitReference(
                ReferenceIdentityKind.PredicateMatrixAnnotationValue,
                $"{field}\0{value}", EntityTypeRegistry.PredicateMatrixAnnotationValue, builder);
            AddRelation(subjectId, relationTypeId, valueId, builder);
        }

        private void AddCorrespondence(
            Hash128 subjectId, Hash128? objectId, SubstrateChangeBuilder builder) =>
            AddRelation(subjectId, PredicateMatrixSource.CorrespondsToTypeId, objectId, builder);

        private void AddRoleCorrespondence(
            Hash128 subjectId, Hash128? objectId, SubstrateChangeBuilder builder) =>
            AddRelation(subjectId, PredicateMatrixSource.RoleCorrespondsToTypeId, objectId, builder);

        private void AddRelation(
            Hash128 subjectId,
            Hash128 relationTypeId,
            Hash128? objectId,
            SubstrateChangeBuilder builder)
        {
            if (objectId is null) return;
            var relation = NativeAttestation.CategoricalResolved(
                subjectId, relationTypeId, objectId, _sourceId, null, _trust);
            if (_relations.Add(relation.Id)) builder.AddAttestation(relation);
        }

        private static string? NormalizePropBankRole(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string key = raw.Trim().ToUpperInvariant();
            if (key.All(char.IsDigit)) return $"ARG{key}";
            if (key.StartsWith("ARG", StringComparison.Ordinal)) return key;
            if (key.StartsWith("R-A", StringComparison.Ordinal)
                || key.StartsWith("C-A", StringComparison.Ordinal))
            {
                int at = key.IndexOf('A', 2);
                if (at >= 0 && at + 1 < key.Length && char.IsDigit(key[at + 1]))
                    return $"ARG{key[(at + 1)..]}";
            }
            return "ARGM";
        }

        private Hash128? EmitCategory(
            CategoryCorrespondenceRecord record, SubstrateChangeBuilder builder)
        {
            Hash128? subjectId = AnchorAdmission.Id(record.SubjectKey, record.SubjectTypeId);
            if (subjectId is null) return null;

            Hash128 declarationId = CategoryAnchor.CategoryAttestationId(
                subjectId.Value, record.SubjectTypeId, _sourceId);
            if (_declarations.Add(declarationId))
                subjectId = AnchorAdmission.Emit(
                    builder, record.SubjectKey, record.SubjectTypeId, _sourceId, _trust);
            if (subjectId is null) return null;

            var relation = NativeAttestation.Categorical(
                subjectId.Value, record.RelationType, record.ObjectId, _sourceId, _trust,
                magnitude: record.Magnitude, arenaScale: 1.0, contextId: record.ContextId);
            if (_relations.Add(relation.Id)) builder.AddAttestation(relation);
            return subjectId.Value;
        }

        private Hash128? EmitRole(RoleCorrespondenceRecord record, SubstrateChangeBuilder builder)
        {
            Hash128? subjectParent = AnchorAdmission.Id(
                record.SubjectParentKey, record.SubjectParentTypeId);
            Hash128? objectParent = AnchorAdmission.Id(
                record.ObjectParentKey, record.ObjectParentTypeId);
            if (subjectParent is null || objectParent is null) return null;

            RoleIdentityKind subjectKind = RoleAnchor.KindForParentType(record.SubjectParentTypeId);
            RoleIdentityKind objectKind = RoleAnchor.KindForParentType(record.ObjectParentTypeId);
            Hash128? subjectRole = RoleAnchor.Id(subjectKind, subjectParent.Value, record.SubjectRoleKey);
            Hash128? objectRole = RoleAnchor.Id(objectKind, objectParent.Value, record.ObjectRoleKey);
            if (subjectRole is null || objectRole is null) return null;

            if (_roleEntities.Add(subjectRole.Value))
                RoleAnchor.Declare(
                    builder, subjectKind, subjectParent.Value, record.SubjectRoleKey,
                    RoleAnchor.EntityTypeFor(subjectKind), _sourceId);
            if (_roleEntities.Add(objectRole.Value))
                RoleAnchor.Declare(
                    builder, objectKind, objectParent.Value, record.ObjectRoleKey,
                    RoleAnchor.EntityTypeFor(objectKind), _sourceId);

            var relation = NativeAttestation.ResolvedScored(
                subjectRole.Value, PredicateMatrixSource.RoleCorrespondsToTypeId, objectRole.Value,
                _sourceId, null, _trust, record.Magnitude, arenaScale: 1.0);
            if (_relations.Add(relation.Id)) builder.AddAttestation(relation);
            return subjectRole.Value;
        }

        private sealed class Unit(
            PredicateMatrixRecordHandler owner,
            PredicateMatrixRecord record) : IIngestDeferredUnit
        {
            public TierTree? TreeForBatchProbe => null;

            public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
                Task.FromResult<byte[]?>(null);

            public Hash128 DrainInto(
                SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
                owner.Drain(record, builder);

            public void Dispose() { }
        }
    }
}

internal sealed class PredicateMatrixPhase : DecomposerPhase<PredicateMatrixIngest.PredicateMatrixRecord>
{
    private readonly string _path;
    private readonly LanguageFilter? _langs;

    public PredicateMatrixPhase(string path, LanguageFilter? langs)
    {
        _path = path;
        _langs = langs;
    }

    protected override string PhaseLabel => "semlink/predicate-matrix";

    public override Hash128 SourceId => PredicateMatrixIngest.Source;
    public override string SourceName => "PredicateMatrixDecomposer";
    public override int LayerOrder => 3;
    public override Hash128 TrustClassId => PredicateMatrixIngest.TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        Task.CompletedTask;

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default) =>
        PredicateMatrixIngest.EstimateRecordCountAsync(_path, _langs, ct);

    protected override IIngestRecordHandler<PredicateMatrixIngest.PredicateMatrixRecord> CreateHandler() =>
        PredicateMatrixIngest.CreateRecordHandler(SourceTrust);

    protected override IAsyncEnumerable<PredicateMatrixIngest.PredicateMatrixRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        PredicateMatrixIngest.EnumerateRecordsAsync(_path, _langs, options.MaxInputUnits, ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        var config = IngestPipelineDefaults.CategoryCorrespondence(
            SourceId, BatchLabelPrefix, BatchConfigDefaults.HighVolume, options, context.Reader,
            PredicateMatrixSource.Profile);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
