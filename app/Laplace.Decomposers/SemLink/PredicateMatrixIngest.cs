using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

public static class PredicateMatrixIngest
{
    // PredicateMatrix is a resource distinct from SemLink's JSON maps: each row ties a VN class,
    // FN frame, PB roleset, WN sense and MCR/ILI. Its rows carry their own source id, so
    // consensus folds PredicateMatrix and SemLink as two witnesses of the same VN↔FN↔synset
    // links. SemLinkDecomposer.InitializeAsync registers that source entity.
    internal static readonly Hash128 Source = PredicateMatrixSource.SourceId;
    internal static readonly Hash128 TrustClass = PredicateMatrixSource.TrustClass;

    private const int ColLang = 0;
    private const int ColPos = 1;
    private const int ColPredicate = 2;
    private const int ColPredicateRole = 3;
    private const int ColVnClass = 4;
    private const int ColVnClassNumber = 5;
    private const int ColVnSubclass = 6;
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

            // The first non-empty line is the column header.
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
                new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rsSynset, "11_MCR_iliOffset")));
            if (senseId is { } rs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rs, "10_WN_SENSE")));
        }

        string? frame = Field(fields, ColFnFrame);
        if (frame is not null && synId is { } fnSynset)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(frame, FrameTypeId, fnSynset, "11_MCR_iliOffset")));
            if (senseId is { } fs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(frame, FrameTypeId, fs, "10_WN_SENSE")));
        }

        string? vnClass = VerbNetClassKey(fields);
        if (vnClass is not null && synId is { } vnSynset)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vnSynset, "11_MCR_iliOffset")));
            if (senseId is { } vs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vs, "10_WN_SENSE")));
        }

        string? vnRole = Field(fields, ColVnRole);
        string? fnFe = Field(fields, ColFnFe);

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
        // Canonical name first: only the first matched path is ingested, so versioned
        // siblings the glob also matches must not outrank PredicateMatrix.txt.
        Files = [IngestFileMatch.Name("PredicateMatrix.txt"), IngestFileMatch.Glob("PredicateMatrix*.txt")],
        EcosystemDirs =
        [
            ".",
            "instances",
            Path.Combine("semlink-cu-clear", "instances"),
            Path.Combine("semlink-master", "instances"),
            .. UnpackDirs,
        ],
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
        // The class file writes give-13.1-1. The matrix names that class in the class
        // column and its number beside it. The member lemma is not the class name.
        string? className = Field(fields, ColVnClass);
        if (className is null) return null;
        string? number = Field(fields, ColVnSubclass) ?? Field(fields, ColVnClassNumber);
        if (number is null) return className;
        return number.StartsWith(className + "-", StringComparison.Ordinal)
            ? number
            : $"{className}-{number}";
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
                                      parsed.Value.WnVersion);
    }

    public readonly record struct PredicateMatrixEdge(
        CategoryCorrespondenceRecord? Category,
        RoleCorrespondenceRecord? Role)
    {
        public static PredicateMatrixEdge FromCategory(CategoryCorrespondenceRecord c) => new(c, null);
        public static PredicateMatrixEdge FromRole(RoleCorrespondenceRecord r) => new(null, r);
    }

    public readonly record struct PredicateMatrixRecord(
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

    // The row is the composition [predicate, language, pos], one boundary above
    // those constituents. The predicate name (give.01) stays the PropBank roleset.
    internal static Hash128? PredicateId(string language, string pos, string predicateKey)
    {
        if (!TryContent(predicateKey, out Hash128 predicate, out byte predicateTier)
            || !TryContent(language, out Hash128 lang, out byte langTier)
            || !TryContent(pos, out Hash128 tag, out byte tagTier))
            return null;
        byte tier = (byte)(Math.Max(predicateTier, Math.Max(langTier, tagTier)) + 1);
        return Hash128.Merkle(tier, [predicate, lang, tag]);
    }

    private static bool TryContent(string surface, out Hash128 id, out byte tier)
    {
        id = default;
        tier = 0;
        if (string.IsNullOrEmpty(surface)) return false;
        using var tree = ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(surface));
        if (tree is null) return false;
        var node = tree.GetNode(tree.NaturalUnitIndex());
        id = node.Id;
        tier = node.Tier;
        return true;
    }

    internal static Hash128? PredicateRoleId(
        string language, string pos, string predicateKey, string roleKey) =>
        PredicateId(language, pos, predicateKey) is { } predicateId
            ? RoleAnchor.Id(RoleIdentityKind.PredicateMatrix, predicateId, roleKey)
            : null;

    private sealed class PredicateMatrixRecordHandler : IIngestRecordHandler<PredicateMatrixRecord>
    {
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        // DrainInto is serial for this handler, so plain sets suffice for run-wide
        // suppression of repeated declarations.
        private readonly HashSet<Hash128> _declarations = new();
        private readonly HashSet<Hash128> _roleEntities = new();
        private readonly HashSet<Hash128> _relations = new();

        public PredicateMatrixRecordHandler(Hash128 sourceId, double trust)
        {
            _sourceId = sourceId;
            _trust = trust;
        }

        // Parsing already produced the row's compact projection and admission is an ordered
        // builder drain, so there is no compose work to fan out to workers.
        public bool ParallelizeDeferredUnitCreation => false;

        public IIngestDeferredUnit CreateDeferredUnit(PredicateMatrixRecord record) =>
            new Unit(this, record);

        public void WalkWitness(
            PredicateMatrixRecord record, Hash128 root,
            SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
        { }

        private Hash128 Drain(PredicateMatrixRecord record, SubstrateChangeBuilder builder)
        {
            if (ContentEmitter.StageComponent(builder, record.PredicateKey, _sourceId) is not { } predicatePart
                || ContentEmitter.StageComponent(builder, record.Language, _sourceId) is not { } languagePart
                || ContentEmitter.StageComponent(builder, record.Pos, _sourceId) is not { } posPart)
                return default;

            Span<OrderedCompositionResult> composed = stackalloc OrderedCompositionResult[1];
            OrderedComposition.StageBatch(
                builder.ContentStage,
                [new OrderedCompositionRequest(
                    [predicatePart, languagePart, posPart],
                    EntityTypeRegistry.PredicateMatrixPredicate,
                    _sourceId,
                    0)],
                composed);
            Hash128 predicateId = composed[0].Id;
            Hash128 expected = Hash128.Merkle(
                EntityTier.Document, [predicatePart.Id, languagePart.Id, posPart.Id]);
            if (predicateId != expected)
                throw new InvalidOperationException(
                    "predicate-matrix identity diverged from [predicate, language, pos]");

            Hash128? predicateRoleId = EmitScopedRole(
                RoleIdentityKind.PredicateMatrix, predicateId, composed[0].Tier,
                record.PredicateRoleKey, EntityTypeRegistry.PredicateMatrixRole, builder);

            if (record.SynsetId is { } synset)
                AddClaim(predicateId, "11_MCR_iliOffset", synset, builder);
            if (record.SenseId is { } sense)
                AddClaim(predicateId, "10_WN_SENSE", sense, builder);

            Hash128? vnClassId = record.VerbNetClass is { } vnClass
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    vnClass, VnClassTypeId, predicateId, "4_VN_CLASS"), builder)
                : null;
            Hash128? frameId = record.Frame is { } frame
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    frame, FrameTypeId, predicateId, "12_FN_FRAME"), builder)
                : null;
            // A lexical unit is FrameNet's composition [frame, lemma, UPOS], never "run.v".
            if (record.FrameLu is { } frameLu && record.Frame is { } luFrame)
                _ = EmitCategory(new CategoryCorrespondenceRecord(
                    $"{luFrame} {frameLu}", FrameLuTypeId, predicateId, "13_FN_LE",
                    DeclareSubject: (b, source) =>
                        FrameNetLexicalUnit.Declare(b, luFrame, frameLu, source)), builder);
            Hash128? rolesetId = record.PropBankRoleset is { } roleset
                ? EmitCategory(new CategoryCorrespondenceRecord(
                    roleset, RolesetTypeId, predicateId, "15_PB_ROLESET"), builder)
                : null;

            if (predicateRoleId is { } pmRole)
            {
                if (vnClassId is { } vnParent && record.VerbNetRole is { } vnRole)
                    AddClaim(pmRole, "9_VN_ROLE", EmitScopedRole(
                        RoleIdentityKind.VerbNet, vnParent, ContentFloor(record.VerbNetClass), vnRole,
                        EntityTypeRegistry.VerbNetRole, builder), builder);
                if (frameId is { } fnParent && record.FrameFe is { } frameFe)
                    AddClaim(pmRole, "14_FN_FRAME_ELEMENT", EmitScopedRole(
                        RoleIdentityKind.FrameNet, fnParent, ContentFloor(record.Frame), frameFe,
                        EntityTypeRegistry.FrameNetFe, builder), builder);
                if (rolesetId is { } pbParent && NormalizePropBankRole(record.PropBankArg) is { } pbRole)
                    AddClaim(pmRole, "16_PB_ARG", EmitScopedRole(
                        RoleIdentityKind.PropBank, pbParent, ContentFloor(record.PropBankRoleset), pbRole,
                        EntityTypeRegistry.PropBankRole, builder), builder);
            }

            Hash128 propertySubject = record.SynsetId ?? predicateId;
            if (record.McrDomain is { } domain)
                AddClaim(propertySubject, "19_MCR_DOMAIN", EmitValue(domain, builder), builder);
            if (record.McrSumo is { } sumo)
                AddClaim(propertySubject, "20_MCR_SUMO", EmitValue(sumo, builder), builder);
            foreach (string top in record.McrTopOntology)
                AddClaim(propertySubject, "21_MCR_TO", EmitValue(top, builder), builder);
            if (record.McrLexname is { } lexname)
                AddClaim(propertySubject, "22_MCR_LEXNAME", EmitValue(lexname, builder), builder);
            if (record.McrBlcId is { } blc)
                AddClaim(propertySubject, "23_MCR_BLC", blc, builder);

            Hash128 annotationSubject = record.SenseId ?? propertySubject;
            AddClaim(annotationSubject, "18_MCR_BC", EmitValue(record.McrBaseConcept, builder), builder);
            AddClaim(annotationSubject, "24_WN_SENSEFREC", EmitValue(record.SenseFrequency, builder), builder);
            AddClaim(propertySubject, "25_WN_SYNSET_REL_NUM", EmitValue(record.SynsetRelationCount, builder), builder);

            Hash128? esoClassId = EmitValue(record.EsoClass, builder);
            AddClaim(predicateId, "26_ESO_CLASS", esoClassId, builder);
            if (predicateRoleId is { } matrixRole
                && esoClassId is { } esoParent
                && record.EsoRole is { } esoRole)
            {
                AddClaim(matrixRole, "27_ESO_ROLE", EmitScopedRole(
                    RoleIdentityKind.Eso, esoParent, ContentFloor(record.EsoClass), esoRole,
                    EntityTypeRegistry.EsoRole, builder), builder);
            }

            foreach (var edge in record.Edges)
                if (edge.Category is { } category)
                    _ = EmitCategory(category, builder);
            return predicateId;
        }

        private static Hash128? EmitValue(string? value, SubstrateChangeBuilder builder) =>
            string.IsNullOrEmpty(value) ? null : ContentEmitter.Emit(builder, value, Source);

        private static byte ContentFloor(string? value) =>
            value is not null && CompositionLaw.TryFace(value, out CompositionLaw.Face face)
                ? face.Tier
                : (byte)0;

        private Hash128? EmitScopedRole(
            RoleIdentityKind kind,
            Hash128 parentId,
            byte parentTier,
            string roleKey,
            Hash128 entityTypeId,
            SubstrateChangeBuilder builder)
        {
            Hash128? id = RoleAnchor.Id(kind, parentId, roleKey);
            if (id is null) return null;
            if (_roleEntities.Add(id.Value))
                id = RoleAnchor.Declare(
                    builder, kind, parentId, parentTier, roleKey, entityTypeId, _sourceId);
            return id;
        }

        private void AddClaim(
            Hash128 subjectId, string predicate, Hash128? objectId, SubstrateChangeBuilder builder) =>
            AddRelation(subjectId, RelationTypeRegistry.RelationTypeId(predicate), objectId, builder);

        private void AddRelation(
            Hash128 subjectId,
            Hash128 relationTypeId,
            Hash128? objectId,
            SubstrateChangeBuilder builder,
            Mask256 qualifiers = default)
        {
            if (objectId is null) return;
            var relation = NativeAttestation.CategoricalResolved(
                subjectId, relationTypeId, objectId, _sourceId, null, _trust)
                with { QualifierMask = qualifiers };
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
            Hash128? subjectId;
            if (record.DeclareSubject is { } declare)
            {
                subjectId = declare(builder, _sourceId);
            }
            else
            {
                subjectId = AnchorAdmission.Id(record.SubjectKey, record.SubjectTypeId);
                if (subjectId is null) return null;
                if (_declarations.Add(subjectId.Value))
                    subjectId = AnchorAdmission.Emit(
                        builder, record.SubjectKey, record.SubjectTypeId, _sourceId, _trust);
            }
            if (subjectId is null) return null;

            var relation = NativeAttestation.Categorical(
                subjectId.Value, record.RelationType, record.ObjectId, _sourceId, _trust,
                magnitude: record.Magnitude, arenaScale: 1.0, contextId: record.ContextId);
            if (_relations.Add(relation.Id)) builder.AddAttestation(relation);
            return subjectId.Value;
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
            SourceId, BatchLabelPrefix, options, context.Reader,
            PredicateMatrixSource.Profile);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
