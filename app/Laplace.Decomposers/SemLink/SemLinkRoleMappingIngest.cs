using System.Runtime.CompilerServices;
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
        byte[] utf8 = await File.ReadAllBytesAsync(path, ct);
        using var ast = GrammarDecomposer.Parse(utf8, "xml");
        IReadOnlyList<int> roleTags = XmlGrammarHelper.StartTags(ast, utf8, "role");
        foreach (int classTag in XmlGrammarHelper.StartTags(ast, utf8, "vncls"))
        {
            ct.ThrowIfCancellationRequested();
            XmlGrammarHelper.TryAttribute(ast, utf8, classTag, "class", out string vnClass);
            XmlGrammarHelper.TryAttribute(ast, utf8, classTag, "fnframe", out string fnFrame);
            if (string.IsNullOrWhiteSpace(fnFrame))
                XmlGrammarHelper.TryAttribute(ast, utf8, classTag, "fnclass", out fnFrame);
            vnClass = vnClass.Trim();
            fnFrame = fnFrame.Trim();
            if (vnClass.Length == 0 || fnFrame.Length == 0) continue;
            string vnClassKey = SourceEntityIdConventions.NumericVerbNetClassId(vnClass);

            int classElement = XmlGrammarHelper.ContainingElement(ast, classTag);
            if (classElement < 0) continue;
            foreach (int roleTag in roleTags)
            {
                if (!XmlGrammarHelper.IsDescendantOf(ast, roleTag, classElement)) continue;
                XmlGrammarHelper.TryAttribute(ast, utf8, roleTag, "fnrole", out string fnRole);
                XmlGrammarHelper.TryAttribute(ast, utf8, roleTag, "vnrole", out string vnRole);
                fnRole = fnRole.Trim();
                vnRole = vnRole.Trim();
                if (fnRole.Length == 0 || vnRole.Length == 0) continue;

                yield return new RoleCorrespondenceRecord(
                    vnClassKey, EntityTypeRegistry.VerbNetClass, vnRole,
                    fnFrame, EntityTypeRegistry.FrameNetFrame, fnRole);
            }
        }
    }

    // Exact source-grain inventory without constructing the extraction DOM. A physical line is
    // not a role mapping: the current v1.2 file has 4,026 lines but only 1,663 admitted roles.
    internal static async Task<long?> EstimateUnitCountAsync(string path, CancellationToken ct)
    {
        long count = 0;
        await foreach (var _ in EnumerateRecordsAsync(path, ct)) count++;
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
