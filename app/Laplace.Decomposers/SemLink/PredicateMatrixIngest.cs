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

    internal static async IAsyncEnumerable<PredicateMatrixEdge> EnumerateEdgesAsync(
        string path,
        LanguageFilter? langs,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        string? header = await reader.ReadLineAsync(ct);
        if (header is null) yield break;

        long rowsTotal = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            var fields = line.Split('\t');
            if (fields.Length <= ColPbRoleset) continue;
            if (fields[ColLang].Equals("1_ID_LANG", StringComparison.Ordinal)) continue;

            string lang = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColLang]);
            string pos = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColPos]);
            if (!lang.Equals("eng", StringComparison.Ordinal) || !pos.Equals("v", StringComparison.Ordinal))
                continue;
            if (langs is { IsActive: true } && !langs.MatchesRaw("eng"))
                continue;

            Hash128? synId = SynsetAnchor(fields[ColMcrIli]);
            if (synId is null) continue;

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;

            string wnSenseRaw = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColWnSense]);
            Hash128? senseId = wnSenseRaw.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                ? null : SenseAnchor.Id(wnSenseRaw);

            if (TryRoleset(fields[ColPbRoleset], out string? roleset) && roleset is not null)
            {
                yield return PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(roleset, RolesetTypeId, synId.Value));
                if (senseId is { } rs)
                    yield return PredicateMatrixEdge.FromCategory(
                        new CategoryCorrespondenceRecord(roleset, RolesetTypeId, rs));
            }

            if (TryFrame(fields[ColFnFrame], out string? frame) && frame is not null)
            {
                yield return PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(frame, FrameTypeId, synId.Value));
                if (senseId is { } fs)
                    yield return PredicateMatrixEdge.FromCategory(
                        new CategoryCorrespondenceRecord(frame, FrameTypeId, fs));
            }

            string? vnClass = VerbNetClassKey(fields);
            if (vnClass is not null)
            {
                yield return PredicateMatrixEdge.FromCategory(
                    new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, synId.Value));
                if (senseId is { } vs)
                    yield return PredicateMatrixEdge.FromCategory(
                        new CategoryCorrespondenceRecord(vnClass, VnClassTypeId, vs));
            }

            if (vnClass is not null && fields.Length > ColFnFe)
            {
                string vnRole = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnRole]).Trim();
                string fnFe = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColFnFe]).Trim();
                if (vnRole.Length > 0 && !vnRole.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    && fnFe.Length > 0 && !fnFe.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    yield return PredicateMatrixEdge.FromTriple(new RelationTripleRecord(
                        Encoding.UTF8.GetBytes(vnRole),
                        "ROLE_CORRESPONDS_TO",
                        Encoding.UTF8.GetBytes(fnFe),
                        ContextAnchorKey: vnClass,
                        ContextCategoryTypeId: VnClassTypeId));
                }
            }

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
        }
    }

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct)
    {
        long lines = 0;
        await foreach (var _ in ReadLinesAsync(path, ct))
            lines++;
        return lines > 1 ? lines - 1 : null;
    }

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static bool ExistsLocally(string dir) => PredicateMatrixFilesIn(dir).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in DataDirs(ecosystemPath))
        {
            foreach (string path in PredicateMatrixFilesIn(dir))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> PredicateMatrixFilesIn(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        string canonical = Path.Combine(dir, "PredicateMatrix.txt");
        if (File.Exists(canonical)) yield return canonical;

        foreach (var file in Directory.EnumerateFiles(dir, "PredicateMatrix*.txt"))
        {
            if (!file.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }

        foreach (var sub in new[] { "PredicateMatrix", "predicate-matrix", "PredicateMatrix.v1.3" })
        {
            string nestedDir = Path.Combine(dir, sub);
            if (!Directory.Exists(nestedDir)) continue;

            string nested = Path.Combine(nestedDir, "PredicateMatrix.txt");
            if (File.Exists(nested)) yield return nested;

            foreach (var file in Directory.EnumerateFiles(nestedDir, "PredicateMatrix*.txt"))
            {
                if (!file.Equals(nested, StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    private static IEnumerable<string> DataDirs(string ecosystemPath)
    {
        yield return ecosystemPath;
        yield return Path.Combine(ecosystemPath, "instances");
        yield return Path.Combine(ecosystemPath, "semlink-master", "instances");
        yield return Path.Combine(ecosystemPath, "PredicateMatrix");
        yield return Path.Combine(ecosystemPath, "predicate-matrix");
        yield return Path.Combine(ecosystemPath, "PredicateMatrix.v1.3");

        foreach (string root in VaultRoots(ecosystemPath))
        {
            yield return root;
            yield return Path.Combine(root, "PredicateMatrix");
            yield return Path.Combine(root, "predicate-matrix");
            yield return Path.Combine(root, "PredicateMatrix.v1.3");
        }
    }

    private static IEnumerable<string> VaultRoots(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string ingest = LaplaceInstall.ResolveIngestRoot();
        if (seen.Add(ingest)) yield return ingest;

        string platformDefault = OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
        if (seen.Add(platformDefault)) yield return platformDefault;

        string? parent = Path.GetDirectoryName(Path.GetFullPath(ecosystemPath));
        if (!string.IsNullOrEmpty(parent) && seen.Add(parent))
            yield return parent;
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

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
        RelationTripleRecord? Triple)
    {
        public static PredicateMatrixEdge FromCategory(CategoryCorrespondenceRecord c) => new(c, null);
        public static PredicateMatrixEdge FromTriple(RelationTripleRecord t) => new(null, t);
    }

    internal static IIngestRecordHandler<PredicateMatrixEdge> CreateEdgeHandler(double trust) =>
        new PredicateMatrixEdgeHandler(Source, trust);

    private sealed class PredicateMatrixEdgeHandler : IIngestRecordHandler<PredicateMatrixEdge>
    {
        private readonly CategoryCorrespondenceHandler _category;
        private readonly RelationTripleHandler _triple;

        public PredicateMatrixEdgeHandler(Hash128 sourceId, double trust)
        {
            _category = new CategoryCorrespondenceHandler(sourceId, trust);
            _triple = new RelationTripleHandler(sourceId, trust);
        }

        public ValueTask<bool> TryTrunkShortcircuitAsync(
            PredicateMatrixEdge record, SubstrateChangeBuilder builder, ISubstrateReader reader,
            double witnessWeight, CancellationToken ct) =>
            ValueTask.FromResult(false);

        public IIngestDeferredUnit CreateDeferredUnit(PredicateMatrixEdge record) =>
            record.Category is { } cat
                ? _category.CreateDeferredUnit(cat)
                : _triple.CreateDeferredUnit(record.Triple!.Value);

        public void WalkWitness(
            PredicateMatrixEdge record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
        { }
    }
}

internal sealed class PredicateMatrixPhase : DecomposerPhase<PredicateMatrixIngest.PredicateMatrixEdge>
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
        PredicateMatrixIngest.EstimateLineCountAsync(_path, ct);

    protected override IIngestRecordHandler<PredicateMatrixIngest.PredicateMatrixEdge> CreateHandler() =>
        PredicateMatrixIngest.CreateEdgeHandler(SourceTrust);

    protected override IAsyncEnumerable<PredicateMatrixIngest.PredicateMatrixEdge> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        PredicateMatrixIngest.EnumerateEdgesAsync(_path, _langs, options.MaxInputUnits, ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        int batchSize = options.BatchSize > 0 ? options.BatchSize : BatchConfigDefaults.HighVolume;
        var config = IngestPipelineDefaults.CategoryCorrespondence(
            SourceId, BatchLabelPrefix, batchSize, options, context.Reader);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
