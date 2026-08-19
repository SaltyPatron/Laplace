using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

internal static class SemLinkRoleMappingIngest
{
    internal const string FileName = "VN-FNRoleMapping.txt";

    // Ecosystem-local: the role mapping ships inside the SemLink unpack, so the shared
    // ingest root is deliberately NOT searched. Root last — an unpacked
    // other_resources/ outranks a stray copy at the top level.
    private static readonly IngestSourceLayout Layout = new()
    {
        Files = [IngestFileMatch.Name(FileName)],
        EcosystemDirs = [Path.Combine("semlink-master", "other_resources"), "other_resources", "."],
    };

    internal static bool ExistsLocally(string dir) => File.Exists(Path.Combine(dir, FileName));

    internal static string? ResolvePath(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout).FirstOrDefault();

    internal static async IAsyncEnumerable<RelationTripleRecord> EnumerateRecordsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var doc = new XmlDocument();
        await Task.Run(() =>
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            doc.Load(stream);
        }, ct);

        var root = doc.DocumentElement;
        if (root is null) yield break;

        foreach (XmlNode clsNode in root.ChildNodes)
        {
            ct.ThrowIfCancellationRequested();
            if (clsNode is not XmlElement cls || !cls.Name.Equals("vncls", StringComparison.Ordinal))
                continue;

            string vnClass = cls.GetAttribute("class").Trim();
            if (vnClass.Length == 0) continue;
            string vnClassKey = SourceEntityIdConventions.NumericVerbNetClassId(vnClass);

            foreach (XmlNode rolesNode in cls.ChildNodes)
            {
                if (rolesNode is not XmlElement roles || !roles.Name.Equals("roles", StringComparison.Ordinal))
                    continue;

                foreach (XmlNode roleNode in roles.ChildNodes)
                {
                    if (roleNode is not XmlElement role || !role.Name.Equals("role", StringComparison.Ordinal))
                        continue;

                    string fnRole = role.GetAttribute("fnrole").Trim();
                    string vnRole = role.GetAttribute("vnrole").Trim();
                    if (fnRole.Length == 0 || vnRole.Length == 0) continue;

                    yield return new RelationTripleRecord(
                        Encoding.UTF8.GetBytes(vnRole),
                        "ROLE_CORRESPONDS_TO",
                        Encoding.UTF8.GetBytes(fnRole),
                        ContextAnchorKey: vnClassKey,
                        ContextCategoryTypeId: EntityTypeRegistry.VerbNetClass);
                }
            }
        }
    }

    // Inventory denominator only — do not DOM-load the XML just to count <role> tags.
    internal static Task<long?> EstimateUnitCountAsync(string path, CancellationToken ct) =>
        Task.FromResult<long?>(Math.Max(1, EtlInventory.EstimateNewlineCount(path, ct)));
}

internal sealed class SemLinkRoleMappingPhase : DecomposerPhase<RelationTripleRecord>
{
    private readonly string _path;

    public SemLinkRoleMappingPhase(string path) => _path = path;

    protected override string PhaseLabel => "semlink/vn-fn-role-mapping";

    public override Hash128 SourceId => SemLinkDecomposer.Source;
    public override string SourceName => "SemLinkDecomposer";
    public override int LayerOrder => 3;
    public override Hash128 TrustClassId => SemLinkDecomposer.TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        Task.CompletedTask;

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SemLinkRoleMappingIngest.EstimateUnitCountAsync(_path, ct);

    protected override IIngestRecordHandler<RelationTripleRecord> CreateHandler() =>
        new RelationTripleHandler(SourceId, SourceTrust);

    protected override IAsyncEnumerable<RelationTripleRecord> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        SemLinkRoleMappingIngest.EnumerateRecordsAsync(_path, ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options)
    {
        var config = IngestPipelineDefaults.RelationTriple(
            SourceId, BatchLabelPrefix, options, context.Reader);
        return IngestPipelineDefaults.ApplyMaxInputUnits(config, options);
    }
}
