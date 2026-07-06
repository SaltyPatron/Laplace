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

    internal static bool ExistsLocally(string dir) => File.Exists(Path.Combine(dir, FileName));

    internal static string? ResolvePath(string ecosystemPath)
    {
        foreach (var dir in CandidateDirs(ecosystemPath))
        {
            string candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs(string ecosystemPath)
    {
        yield return Path.Combine(ecosystemPath, "semlink-master", "other_resources");
        yield return Path.Combine(ecosystemPath, "other_resources");
        yield return ecosystemPath;
    }

    internal static async IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        int batchSize,
        ISubstrateReader? containmentReader = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (batchSize <= 0) batchSize = 4096;
        var stream = new AsyncEnumerableRecordStream<RelationTripleRecord>(EnumerateRecordsAsync(path, ct));
        var handler = new RelationTripleHandler(SemLinkDecomposer.Source, TC.AcademicCurated);
        var config = new IngestBatchConfig
        {
            SourceId = SemLinkDecomposer.Source,
            BatchLabelPrefix = "semlink/vn-fn-role-mapping",
            BatchSize = batchSize,
            ProbeChunkSize = Math.Clamp(batchSize, 64, 4096),
            ContainmentReader = containmentReader,
            WorkingSet = WorkingSetMode.Enabled,
        };
        await foreach (var change in IngestBatchPipeline.RunAsync(stream, handler, config, ct))
            yield return change;
    }

    private static async IAsyncEnumerable<RelationTripleRecord> EnumerateRecordsAsync(
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

    internal static async Task<long?> EstimateUnitCountAsync(string path, CancellationToken ct)
    {
        long total = 0;
        await Task.Run(() =>
        {
            var doc = new XmlDocument();
            doc.Load(path);
            var root = doc.DocumentElement;
            if (root is null) return;
            foreach (XmlNode _ in root.GetElementsByTagName("role"))
            {
                ct.ThrowIfCancellationRequested();
                total++;
            }
        }, ct);
        return total > 0 ? total : null;
    }
}
