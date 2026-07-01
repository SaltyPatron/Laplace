using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Ingests semlink-master/other_resources/VN-FNRoleMapping.txt: an XML document (despite the .txt
/// extension) of <c>vncls</c> elements, each pairing a VerbNet class with the FrameNet frame it was
/// mapped against, and nested <c>role</c> elements giving the per-pair VerbNet-thematic-role to
/// FrameNet-FE role correspondence. This is role-level detail that vn-fn2.json's class/frame-name
/// list does not carry.
///
/// VerbNet role ids are minted via <see cref="ContentEmitter"/> exactly as
/// <c>Laplace.Decomposers.VerbNet.VerbNetDecomposer</c> mints THEMROLE ids (plain content-addressed
/// text root, not a typed category), so the two sources converge on the same id for the same role
/// name. FrameNet FE ids are minted via <see cref="CategoryAnchor"/> on the bare FE name exactly as
/// <c>Laplace.Decomposers.FrameNet.FrameNetDecomposer</c> does, so the two sources converge there too.
/// </summary>
internal static class SemLinkRoleMappingIngest
{
    internal const string FileName = "VN-FNRoleMapping.txt";

    private static readonly Hash128 FeTypeId = EntityTypeRegistry.FrameNetFe;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;

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

        var doc = new XmlDocument();
        using (var stream = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   bufferSize: 1 << 16, FileOptions.SequentialScan))
        {
            doc.Load(stream);
        }

        var root = doc.DocumentElement;
        if (root is null) yield break;

        var batch = NewBuilder("semlink/vn-fn-role-mapping/0", batchSize, containmentReader);
        int count = 0, batchNum = 0;

        foreach (XmlNode clsNode in root.ChildNodes)
        {
            ct.ThrowIfCancellationRequested();
            if (clsNode is not XmlElement cls || !cls.Name.Equals("vncls", StringComparison.Ordinal))
                continue;

            string vnClass = cls.GetAttribute("class").Trim();
            string fnFrame = cls.GetAttribute("fnframe").Trim();
            if (vnClass.Length == 0 || fnFrame.Length == 0) continue;

            Hash128? vnClassId = CategoryAnchor.Emit(
                batch, SemLinkDecomposer.NumericClassId(vnClass), VnClassTypeId,
                SemLinkDecomposer.Source, TC.AcademicCurated);
            if (vnClassId is null) continue;

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

                    Hash128? feId = CategoryAnchor.Emit(
                        batch, fnRole, FeTypeId, SemLinkDecomposer.Source, TC.AcademicCurated);
                    if (feId is null) continue;

                    Hash128? vnRoleId = ContentEmitter.Emit(batch, vnRole, SemLinkDecomposer.Source);
                    if (vnRoleId is null) continue;

                    batch.AddAttestation(NativeAttestation.Categorical(
                        vnRoleId.Value, "ROLE_CORRESPONDS_TO", feId.Value,
                        SemLinkDecomposer.Source, TC.AcademicCurated,
                        contextId: vnClassId.Value));

                    if (++count >= batchSize)
                    {
                        batch.SetInputUnitsConsumed(count);
                        yield return await batch.BuildAsync(ct);
                        IntentStage.ResetContentBank();
                        batch = NewBuilder($"semlink/vn-fn-role-mapping/{++batchNum}", batchSize, containmentReader);
                        count = 0;
                    }
                }
            }
        }

        if (count > 0)
        {
            batch.SetInputUnitsConsumed(count);
            yield return await batch.BuildAsync(ct);
            IntentStage.ResetContentBank();
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
            foreach (XmlNode roleNode in root.GetElementsByTagName("role"))
            {
                ct.ThrowIfCancellationRequested();
                total++;
            }
        }, ct);
        return total > 0 ? total : null;
    }

    // VerbNet role names and FrameNet-FE / VerbNet-class category anchors are CONTENT (ContentEmitter /
    // CategoryAnchor): route them through the SHARED two-phase containment (EnableDeferredContent) like
    // every other content-emitting source; drain via BuildAsync so the deferred probe runs.
    private static SubstrateChangeBuilder NewBuilder(string unit, int batch, ISubstrateReader? reader) =>
        new SubstrateChangeBuilder(SemLinkDecomposer.Source, unit, null,
            entityCapacity: batch * 2,
            physicalityCapacity: 0,
            attestationCapacity: batch * 2);
}
