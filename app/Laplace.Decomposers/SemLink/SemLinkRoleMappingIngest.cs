using System.Runtime.CompilerServices;
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

    internal static async IAsyncEnumerable<RoleCorrespondenceRecord> EnumerateRecordsAsync(
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
            string fnFrame = cls.GetAttribute("fnframe").Trim();
            if (fnFrame.Length == 0)
                fnFrame = cls.GetAttribute("fnclass").Trim();
            if (vnClass.Length == 0 || fnFrame.Length == 0) continue;
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

                    yield return new RoleCorrespondenceRecord(
                        vnClassKey, EntityTypeRegistry.VerbNetClass, vnRole,
                        fnFrame, EntityTypeRegistry.FrameNetFrame, fnRole);
                }
            }
        }
    }

    // Exact source-grain inventory without constructing the extraction DOM. A physical line is
    // not a role mapping: the current v1.2 file has 4,026 lines but only 1,663 admitted roles.
    internal static async Task<long?> EstimateUnitCountAsync(string path, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(path, settings);
        bool validClass = false;
        int classDepth = -1;
        int rolesDepth = -1;
        long count = 0;
        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name.Equals("vncls", StringComparison.Ordinal))
                {
                    string vnClass = reader.GetAttribute("class")?.Trim() ?? string.Empty;
                    string fnFrame = reader.GetAttribute("fnframe")?.Trim() ?? string.Empty;
                    if (fnFrame.Length == 0)
                        fnFrame = reader.GetAttribute("fnclass")?.Trim() ?? string.Empty;
                    validClass = vnClass.Length > 0 && fnFrame.Length > 0;
                    classDepth = reader.Depth;
                    rolesDepth = -1;
                    if (reader.IsEmptyElement)
                    {
                        validClass = false;
                        classDepth = -1;
                    }
                }
                else if (validClass && reader.Name.Equals("roles", StringComparison.Ordinal))
                {
                    rolesDepth = reader.Depth;
                    if (reader.IsEmptyElement) rolesDepth = -1;
                }
                else if (validClass && rolesDepth >= 0
                         && reader.Name.Equals("role", StringComparison.Ordinal))
                {
                    string fnRole = reader.GetAttribute("fnrole")?.Trim() ?? string.Empty;
                    string vnRole = reader.GetAttribute("vnrole")?.Trim() ?? string.Empty;
                    if (fnRole.Length > 0 && vnRole.Length > 0) count++;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == rolesDepth && reader.Name.Equals("roles", StringComparison.Ordinal))
                    rolesDepth = -1;
                if (reader.Depth == classDepth && reader.Name.Equals("vncls", StringComparison.Ordinal))
                {
                    validClass = false;
                    classDepth = -1;
                    rolesDepth = -1;
                }
            }
        }
        return count;
    }
}

internal sealed class SemLinkRoleMappingPhase : DecomposerPhase<RoleCorrespondenceRecord>
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

    protected override IIngestRecordHandler<RoleCorrespondenceRecord> CreateHandler() =>
        new RoleCorrespondenceHandler(
            SourceId, SemLinkSource.RoleCorrespondsToTypeId, SourceTrust);

    protected override IAsyncEnumerable<RoleCorrespondenceRecord> ExtractRecordsAsync(
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
