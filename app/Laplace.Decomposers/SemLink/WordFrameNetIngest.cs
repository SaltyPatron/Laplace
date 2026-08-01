using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.SemLink;

internal static class WordFrameNetIngest
{
    private static readonly string[] MappingFileNames =
    [
        "lu_synset.map",
        "WordFrameNet.txt",
        "wordframenet.txt",
        "WFN.txt",
        "XWFN.txt",
    ];

    private static readonly string[] ExtensionlessMappingNames =
    [
        "WordFrameNet",
        "eXtendedWFN",
        "WFN",
        "XWFN",
    ];

    private static readonly IngestSourceLayout Layout = new()
    {
        Files =
        [
            .. MappingFileNames.Select(IngestFileMatch.Name),
            IngestFileMatch.Glob("*.map"),
            IngestFileMatch.Glob("*.txt", name => name.Equals("README", StringComparison.OrdinalIgnoreCase)),
            .. ExtensionlessMappingNames.Select(IngestFileMatch.Name),
        ],
        EcosystemDirs = [".", "WordFrameNet", "wordframenet", "WFN", "eXtendedWFN", "XWFN"],
        RootDirectoryGlobs = ["WordFrameNet*"],
        RootDirs = ["WordFrameNet", "WFN", "eXtendedWFN", "XWFN"],
        SearchIngestRoots = true,
    };

    internal readonly record struct WordFrameNetFileSpec(string Path, string Label, bool NativeFormat);

    internal static WordFrameNetFileSpec DescribeFile(string path)
    {
        string baseName = Path.GetFileName(path);
        string label = baseName.Equals("lu_synset.map", StringComparison.OrdinalIgnoreCase)
                           || baseName.Equals("lu_synset", StringComparison.OrdinalIgnoreCase)
            ? "wordframenet/lu"
            : $"wordframenet/{baseName}";
        return new WordFrameNetFileSpec(path, label, LooksLikeNativeWfn(path));
    }

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct) =>
        await FnLuSynsetBridgeIngest.EstimateLineCountAsync(path, ct);

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout);

    private static bool LooksLikeNativeWfn(string path)
    {
        using var reader = new StreamReader(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            return line.StartsWith("Frame:", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

}

internal sealed class WordFrameNetMultiFileStream : IMultiFileRecordStream<CategoryCorrespondenceRecord>
{
    private readonly IReadOnlyList<WordFrameNetIngest.WordFrameNetFileSpec> _files;

    public WordFrameNetMultiFileStream(IReadOnlyList<WordFrameNetIngest.WordFrameNetFileSpec> files) => _files = files;

    public async IAsyncEnumerable<IFileRecordSource<CategoryCorrespondenceRecord>> FilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var spec in _files)
        {
            var s = spec;
            yield return new DelegateFileRecordSource<CategoryCorrespondenceRecord>(
                s.Label, token => s.NativeFormat
                    ? FnLuSynsetBridgeIngest.EnumerateWfnNativeRecordsAsync(
                        s.Path, FnLuSynsetBridgeIngest.MultiWordNetVersion, 0, token)
                    : FnLuSynsetBridgeIngest.EnumerateTabRecordsAsync(
                        s.Path, FnLuSynsetBridgeIngest.MultiWordNetVersion, 0, token));
        }
        await Task.CompletedTask;
    }
}
