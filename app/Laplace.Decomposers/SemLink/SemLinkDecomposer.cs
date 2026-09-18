using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

public sealed class SemLinkDecomposer : DecomposerMultiPhase<SemLinkSource, FullScope>, IIngestInventoryProvider
{
    private static readonly ConcurrentDictionary<string, byte> VocabularyNames = new(StringComparer.Ordinal);

    public static readonly Hash128 Source = SemLinkSource.SourceId;
    public static readonly Hash128 TrustClass = SemLinkSource.TrustClass;

    public override int LayerOrder => 3;

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => VocabularyNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => VocabularyNames;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        // PredicateMatrix rides SemLink's seed step but is a distinct witness: register its
        // source entity so its attestations' source_id FK resolves. See docs/specs/16 §3a.
        await SourceVocabularyBootstrap.RegisterManifestAsync(
            context, SeedSourceManifest<PredicateMatrixSource>.Instance,
            readbackNames: VocabularyNames, ct: ct);
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        var peers = new List<ArtifactPhaseWork>();

        string? annotatedInstances = SemLinkInstanceIngest.ResolvePath(instancesDir);
        if (annotatedInstances is not null)
            peers.Add(new ArtifactPhaseWork(
                new SemLinkInstancePhase(annotatedInstances),
                "semlink/annotated-instances", annotatedInstances));

        foreach (var (path, kind, label) in JsonDocumentSpecs(instancesDir))
            peers.Add(new ArtifactPhaseWork(
                new SemLinkJsonDocumentPhase(path, kind, label), label, path));

        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            peers.Add(new ArtifactPhaseWork(
                new PredicateMatrixPhase(pmPath, options.Languages),
                "semlink/predicate-matrix", pmPath));
            break;
        }

        string? roleMappingPath = SemLinkRoleMappingIngest.ResolvePath(context.EcosystemPath);
        if (roleMappingPath is not null)
            peers.Add(new ArtifactPhaseWork(
                new SemLinkRoleMappingPhase(roleMappingPath),
                "semlink/vn-fn-role-mapping", roleMappingPath));

        await foreach (SubstrateChange change in RunArtifactDependencyLevelsAsync(
                           [peers], context, options,
                           "semlink/dependency", ct).ConfigureAwait(false))
            yield return change;
    }

    private static DecomposerOptions RemainingOptions(DecomposerOptions options, long cap, long consumed) =>
        cap > 0 ? options with { MaxInputUnits = cap - consumed } : options;

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        var files = new List<IngestFileSpec>();
        long total = 0;
        string? annotatedInstances = SemLinkInstanceIngest.ResolvePath(instancesDir);
        if (annotatedInstances is not null)
        {
            long units = await SemLinkInstanceIngest.EstimateUnitCountAsync(
                annotatedInstances, ct) ?? 0;
            files.Add(new IngestFileSpec(
                "semlink/annotated-instances", annotatedInstances, units));
            total += units;
        }
        foreach (var (path, _, label) in JsonDocumentSpecs(instancesDir))
        {
            long units = await SemLinkJsonPairStream.CountRecordsAsync(path, ct) ?? 0;
            files.Add(new IngestFileSpec(label, path, units));
            total += units;
        }
        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            long units = await PredicateMatrixIngest.EstimateRecordCountAsync(
                pmPath, options.Languages, ct) ?? 0;
            files.Add(new IngestFileSpec("semlink/predicate-matrix", pmPath, units));
            total += units;
            break;
        }
        string? roleMappingPath = SemLinkRoleMappingIngest.ResolvePath(context.EcosystemPath);
        if (roleMappingPath is not null)
        {
            long units = await SemLinkRoleMappingIngest.EstimateUnitCountAsync(roleMappingPath, ct) ?? 0;
            files.Add(new IngestFileSpec("semlink/vn-fn-role-mapping", roleMappingPath, units));
            total += units;
        }
        if (files.Count == 0) return null;
        if (options.MaxInputUnits > 0) total = Math.Min(total, options.MaxInputUnits);
        return new IngestInventory(
            "records", total, files, TracksFileCompletion: true);
    }

    public override async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    private static IEnumerable<(string Path, SemLinkDocumentKind Kind, string Label)> JsonDocumentSpecs(string dir)
    {
        string pbVn = Path.Combine(dir, "pb-vn2.json");
        if (File.Exists(pbVn))
            yield return (pbVn, SemLinkDocumentKind.PbVn, "semlink/pb-vn2");

        string vnFn = Path.Combine(dir, "vn-fn2.json");
        if (File.Exists(vnFn))
            yield return (vnFn, SemLinkDocumentKind.VnFn, "semlink/vn-fn2");

        string pbWn = Path.Combine(dir, "pb-wn.json");
        if (File.Exists(pbWn))
            yield return (pbWn, SemLinkDocumentKind.PbWn, "semlink/pb-wn");

        string vnWn = Path.Combine(dir, "vn-wn.json");
        if (File.Exists(vnWn))
            yield return (vnWn, SemLinkDocumentKind.VnWn, "semlink/vn-wn");

        string fnWn = Path.Combine(dir, "fn-wn.json");
        if (File.Exists(fnWn))
            yield return (fnWn, SemLinkDocumentKind.FnWn, "semlink/fn-wn");

        string vnPbExternal = Path.Combine(OtherResourcesDir(dir), "external_vn2pb.json");
        if (File.Exists(vnPbExternal))
            yield return (vnPbExternal, SemLinkDocumentKind.VnPbExternal, "semlink/external_vn2pb");
    }

    private static string OtherResourcesDir(string instancesDir)
    {
        string? parent = Path.GetDirectoryName(instancesDir);
        if (parent is not null)
        {
            string sibling = Path.Combine(parent, "other_resources");
            if (Directory.Exists(sibling)) return sibling;
        }
        return instancesDir;
    }

    internal static string VnClassFromKey(string key) =>
        SourceEntityIdConventions.VerbNetClassFromSemLinkKey(key);

    private static string ResolveInstancesDir(string ecosystemPath)
    {
        foreach (var c in InstanceDirCandidates(ecosystemPath))
            if (HasJsonMappings(c) || HasPredicateMatrix(c)
                || SemLinkInstanceIngest.ResolvePath(c) is not null)
                return c;
        return ecosystemPath;
    }

    private static IEnumerable<string> InstanceDirCandidates(string ecosystemPath)
    {
        yield return Path.Combine(ecosystemPath, "semlink-master", "instances");
        yield return Path.Combine(ecosystemPath, "instances");
        yield return ecosystemPath;
    }

    private static bool HasJsonMappings(string dir) =>
        File.Exists(Path.Combine(dir, "pb-vn2.json"))
        || File.Exists(Path.Combine(dir, "vn-fn2.json"))
        || File.Exists(Path.Combine(dir, "pb-wn.json"))
        || File.Exists(Path.Combine(dir, "vn-wn.json"))
        || File.Exists(Path.Combine(dir, "fn-wn.json"));

    private static bool HasPredicateMatrix(string dir) =>
        PredicateMatrixIngest.ExistsLocally(dir);

}
