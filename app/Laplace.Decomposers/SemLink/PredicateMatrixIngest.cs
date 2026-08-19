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
    private const int ColVnClass = 4;
    private const int ColVnSubclass = 6;
    private const int ColVnLemma = 8;
    private const int ColVnRole = 9;
    private const int ColWnSense = 10;
    private const int ColMcrIli = 11;
    private const int ColFnFrame = 12;
    private const int ColFnFe = 14;
    private const int ColPbRoleset = 15;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;

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

            string line = Encoding.UTF8.GetString(lineMem.Span);
            if (TryParseRecord(line.Split('\t'), langs, out _)) count++;
        }
        return count > 0 ? count : null;
    }

    private static bool TryParseRecord(
        string[] fields, LanguageFilter? langs, out PredicateMatrixRecord record)
    {
        record = default;
        if (fields.Length <= ColPbRoleset) return false;
        if (fields[ColLang].Equals("1_ID_LANG", StringComparison.Ordinal)) return false;

        string lang = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColLang]);
        string pos = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColPos]);
        if (!lang.Equals("eng", StringComparison.Ordinal) || !pos.Equals("v", StringComparison.Ordinal))
            return false;
        if (langs is { IsActive: true } && !langs.MatchesRaw("eng"))
            return false;

        Hash128? synId = SynsetAnchor(fields[ColMcrIli]);
        if (synId is null) return false;

        string wnSenseRaw = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColWnSense]);
        Hash128? senseId = wnSenseRaw.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            ? null : SenseAnchor.Id(wnSenseRaw);
        var edges = new List<PredicateMatrixEdge>(7);

        if (TryRoleset(fields[ColPbRoleset], out string? roleset) && roleset is not null)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(roleset, RolesetTypeId, synId.Value)));
            if (senseId is { } rs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rs)));
        }

        string? frame = null;
        if (TryFrame(fields[ColFnFrame], out frame) && frame is not null)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(frame, FrameTypeId, synId.Value)));
            if (senseId is { } fs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(frame, FrameTypeId, fs)));
        }

        string? vnClass = VerbNetClassKey(fields);
        if (vnClass is not null)
        {
            edges.Add(PredicateMatrixEdge.FromCategory(
                new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, synId.Value)));
            if (senseId is { } vs)
                edges.Add(PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vs)));
        }

        if (vnClass is not null && fields.Length > ColFnFe)
        {
            string vnRole = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnRole]).Trim();
            string fnFe = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColFnFe]).Trim();
            if (vnRole.Length > 0 && !vnRole.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                && fnFe.Length > 0 && !fnFe.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                && frame is not null)
            {
                edges.Add(PredicateMatrixEdge.FromRole(new RoleCorrespondenceRecord(
                    vnClass, VnClassTypeId, vnRole,
                    frame, FrameTypeId, fnFe)));
            }
        }

        record = new PredicateMatrixRecord(edges.ToArray());
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
        string lemma = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnLemma]);
        if (lemma.Equals("NULL", StringComparison.OrdinalIgnoreCase) || lemma.Length == 0)
            return null;

        string subclass = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnSubclass]);
        if (!subclass.Equals("NULL", StringComparison.OrdinalIgnoreCase) && subclass.Length > 0)
            return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{subclass}");

        string cls = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnClass]);
        if (cls.Equals("NULL", StringComparison.OrdinalIgnoreCase) || cls.Length == 0)
            return null;
        return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{cls}");
    }

    private static bool TryRoleset(string raw, out string? roleset)
    {
        roleset = null;
        string s = SourceEntityIdConventions.StripPredicateMatrixNamespace(raw);
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase) || s.Length == 0) return false;
        roleset = s;
        return true;
    }

    private static bool TryFrame(string raw, out string? frame)
    {
        frame = null;
        string s = SourceEntityIdConventions.StripPredicateMatrixNamespace(raw);
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase) || s.Length == 0) return false;
        frame = s;
        return true;
    }

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

    internal readonly record struct PredicateMatrixRecord(PredicateMatrixEdge[] Edges);

    internal static IIngestRecordHandler<PredicateMatrixRecord> CreateRecordHandler(double trust) =>
        new PredicateMatrixRecordHandler(Source, trust);

    private sealed class PredicateMatrixRecordHandler : IIngestRecordHandler<PredicateMatrixRecord>
    {
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        private readonly ConcurrentIdSet _declarations = new();
        private readonly ConcurrentIdSet _roleEntities = new();
        private readonly ConcurrentIdSet _relations = new();

        public PredicateMatrixRecordHandler(Hash128 sourceId, double trust)
        {
            _sourceId = sourceId;
            _trust = trust;
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
            Hash128 root = default;
            foreach (var edge in record.Edges)
            {
                Hash128? emitted = edge.Category is { } category
                    ? EmitCategory(category, builder)
                    : EmitRole(edge.Role!.Value, builder);
                if (root == default && emitted is { } id) root = id;
            }
            return root;
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
                subjectRole.Value, SemLinkSource.RoleCorrespondsToTypeId, objectRole.Value,
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
            SourceId, BatchLabelPrefix, BatchConfigDefaults.HighVolume, options, context.Reader);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
