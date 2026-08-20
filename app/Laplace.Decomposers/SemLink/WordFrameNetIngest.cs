using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

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

    internal static Task<long?> EstimateLineCountAsync(string path, CancellationToken ct) =>
        Task.FromResult<long?>(EtlInventory.EstimateNewlineCount(path, ct));

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout);

    private static bool LooksLikeNativeWfn(string path)
    {
        // Head probe only — first non-empty, non-comment line starts with Frame:
        using var fs = IngestIo.OpenSequentialRead(path);
        using var reader = new StreamReader(
            fs,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: IngestSizing.ResolveSequentialIoBufferBytes(),
            leaveOpen: false);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            return line.StartsWith("Frame:", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

}
