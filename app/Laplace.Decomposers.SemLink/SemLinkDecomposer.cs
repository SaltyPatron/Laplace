using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

public sealed class SemLinkDecomposer : IDecomposer, IIngestInventoryProvider{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/SemLinkDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "SemLinkDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    // SemLink mapping files are top-level JSON objects (pb-vn2, vn-fn2, optional pb/vn/fn-wn JSON bridges)
    // or Predicate Matrix TSV (PredicateMatrix.txt). Each record stages category anchors and CORRESPONDS_TO
    // edges over content-addressed ids; WordNet synset targets resolve via CILI/ILI (ConceptAnchor).

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["VerbNet_Class", "PropBank_Roleset", "FrameNet_Frame"],
            relationNodeNames: ["CORRESPONDS_TO", "ROLE_CORRESPONDS_TO"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        long cap = options.MaxInputUnits;
        long consumed = 0;

        foreach (var (path, kind, label) in JsonDocumentSpecs(instancesDir))
        {
            if (cap > 0 && consumed >= cap) yield break;
            int jsonBatch = options.BatchSize > 0 ? options.BatchSize : 1;
            ISubstrateReader? reader = context.Reader;
            long fileCap = cap > 0 ? cap - consumed : 0;

            await foreach (var change in SemLinkIngestSupport.IngestJsonDocumentAsync(
                               path, kind, label, jsonBatch, reader, fileCap, ct))
            {
                if (!options.DryRun)
                {
                    consumed += change.Metadata.InputUnitsConsumed;
                    yield return change;
                }
                if (cap > 0 && consumed >= cap) yield break;
            }
        }

        if (cap > 0 && consumed >= cap) yield break;

        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            if (cap > 0 && consumed >= cap) yield break;
            int pmBatch = options.BatchSize > 0 ? options.BatchSize : 4096;
            long fileCap = cap > 0 ? cap - consumed : 0;
            await foreach (var change in PredicateMatrixIngest.StreamAsync(
                               pmPath, pmBatch, options.Languages, fileCap, ct))
            {
                if (!options.DryRun)
                {
                    consumed += change.Metadata.InputUnitsConsumed;
                    yield return change;
                    if (cap > 0 && consumed >= cap) yield break;
                }
            }
            break;
        }

        if (cap > 0 && consumed >= cap) yield break;

        string? roleMappingPath = SemLinkRoleMappingIngest.ResolvePath(context.EcosystemPath);
        if (roleMappingPath is not null)
        {
            int rmBatch = options.BatchSize > 0 ? options.BatchSize : 4096;
            await foreach (var change in SemLinkRoleMappingIngest.StreamAsync(roleMappingPath, rmBatch, context.Reader, ct))
            {
                if (!options.DryRun)
                {
                    consumed += change.Metadata.InputUnitsConsumed;
                    yield return change;
                    if (cap > 0 && consumed >= cap) yield break;
                }
            }
        }
    }

    public async Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        var paths = new List<string>();
        foreach (var (path, _, _) in JsonDocumentSpecs(instancesDir))
            paths.Add(path);
        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            paths.Add(pmPath);
            break;
        }
        string? roleMappingPath = SemLinkRoleMappingIngest.ResolvePath(context.EcosystemPath);
        if (roleMappingPath is not null) paths.Add(roleMappingPath);
        if (paths.Count == 0) return null;
        if (options.MaxInputUnits > 0)
            return IngestInventory.FromFiles("records", paths, options.MaxInputUnits, ct);
        long? total = await EstimateUnitCountAsync(context, ct);
        return total is long n ? IngestInventory.Single(n, "records") : null;
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        long total = 0;
        foreach (var (path, _, _) in JsonDocumentSpecs(instancesDir))
            total += await SemLinkJsonPairStream.CountPairsAsync(path, ct);

        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            long? lines = await PredicateMatrixIngest.EstimateLineCountAsync(pmPath, ct);
            if (lines is not null) total += lines.Value;
            break;
        }

        string? roleMappingPath = SemLinkRoleMappingIngest.ResolvePath(context.EcosystemPath);
        if (roleMappingPath is not null)
        {
            long? roleCount = await SemLinkRoleMappingIngest.EstimateUnitCountAsync(roleMappingPath, ct);
            if (roleCount is not null) total += roleCount.Value;
        }

        return total > 0 ? total : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    /// <summary>
    /// other_resources/ is a sibling of instances/ under semlink-master/, not nested under it
    /// (see <see cref="InstanceDirCandidates"/>). Resolve it relative to the instances dir we already
    /// picked so a vault layout that moved instances/ doesn't silently strand other_resources/ too.
    /// </summary>
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

    internal static string NumericClassId(string classId) =>
        SourceEntityIdConventions.NumericVerbNetClassId(classId);

    private static string ResolveInstancesDir(string ecosystemPath)
    {
        foreach (var c in InstanceDirCandidates(ecosystemPath))
            if (HasJsonMappings(c) || HasPredicateMatrix(c))
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
