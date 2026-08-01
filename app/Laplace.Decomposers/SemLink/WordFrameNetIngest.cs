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

    internal static Task<long?> EstimateLineCountAsync(string path, CancellationToken ct) =>
        Task.FromResult<long?>(EtlInventory.EstimateNewlineCount(path, ct));

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath) =>
        IngestInput.Locate(ecosystemPath, Layout);

    private static bool LooksLikeNativeWfn(string path)
    {
        // Head probe only — first non-empty, non-comment line starts with Frame:
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan);
        var buf = new byte[4096];
        int n = fs.Read(buf, 0, buf.Length);
        if (n <= 0) return false;
        int i = 0;
        if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
        while (i < n)
        {
            int start = i;
            while (i < n && buf[i] != (byte)'\n' && buf[i] != (byte)'\r') i++;
            int len = i - start;
            if (i < n && buf[i] == (byte)'\r') i++;
            if (i < n && buf[i] == (byte)'\n') i++;
            if (len == 0) continue;
            if (buf[start] == (byte)'#') continue;
            return AsciiStartsWithIgnoreCase(buf.AsSpan(start, len), "Frame:"u8);
        }
        return false;
    }

    private static bool AsciiStartsWithIgnoreCase(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle)
    {
        if (hay.Length < needle.Length) return false;
        for (int i = 0; i < needle.Length; i++)
        {
            byte a = hay[i], b = needle[i];
            if (a >= (byte)'A' && a <= (byte)'Z') a = (byte)(a + 32);
            if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

}
