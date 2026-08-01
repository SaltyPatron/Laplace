using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

internal static class MapNetIngest
{
    internal const string FrameMappingFile = "mapping_frame_synsets.txt";
    internal const string LuMappingFile = "mapping_lus_synsets.txt";

    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;

    internal readonly record struct MapNetFileSpec(string Path, string Label, bool IsLuFile);

    internal static MapNetFileSpec DescribeFile(string path)
    {
        bool isLu = Path.GetFileName(path).Equals(LuMappingFile, StringComparison.OrdinalIgnoreCase);
        string label = isLu ? "mapnet/lu" : "mapnet/frame";
        return new MapNetFileSpec(path, label, isLu);
    }

    internal static async IAsyncEnumerable<CategoryCorrespondenceRecord> EnumerateFrameRecordsAsync(
        string path,
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var rec in TabBridgeHelpers.ReadTwoColumnBridgeAsync(
                           path,
                           static col0 => System.Text.Encoding.UTF8.GetString(col0),
                           FrameTypeId,
                           static col1 =>
                           {
                               string raw = System.Text.Encoding.UTF8.GetString(col1);
                               var parsed = SourceEntityIdConventions.ParseMapNetSynsetKey(raw);
                               return parsed is null ? default(Hash128)
                                   : ConceptAnchor.SynsetId(parsed.Value.Offset, parsed.Value.SsType,
                                       FnLuSynsetBridgeIngest.MultiWordNetVersion) ?? default;
                           },
                           maxInputUnits: maxInputUnits,
                           ct: ct))
        {
            if (rec.ObjectId != default)
                yield return rec;
        }
    }

    internal static Task<long?> EstimateLineCountAsync(string path, CancellationToken ct) =>
        Task.FromResult<long?>(EtlInventory.EstimateNewlineCount(path, ct));

    private static readonly IngestSourceLayout Layout = new()
    {
        Files = [IngestFileMatch.Name(FrameMappingFile), IngestFileMatch.Name(LuMappingFile)],
        EcosystemDirs = [".", "MapNet", "MapNet-0.1", "mapnet", "mapnet-0.1"],
        RootDirectoryGlobs = ["MapNet*"],
        RootDirs = ["MapNet", "MapNet-0.1"],
        SearchIngestRoots = true,
        IncludeEcosystemParent = true,
    };

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static bool ExistsLocally(string dir) => IngestInput.FilesIn(dir, Layout).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout);

}
