using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Ingests WordFrameNet / eXtended WordFrameNet TSV mappings (Adimen): FrameNet LU → WordNet synset.
/// Expected layout under vault-root WordFrameNet/ (WFN.tar.gz → WFN/WordFrameNet, XWFN.tar.gz → XWFN/eXtendedWFN):
/// native Adimen text (extensionless WordFrameNet / eXtendedWFN), or TSV (*.txt / *.map) with frame, lu, synset rows.
/// Synset keys may be MCR ili-30-* tokens, bare offset-pos (02244956-v), or MapNet pos#offset.
/// </summary>
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

    internal static async IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string baseName = Path.GetFileName(path);
        string label = baseName.Equals("lu_synset.map", StringComparison.OrdinalIgnoreCase)
                           || baseName.Equals("lu_synset", StringComparison.OrdinalIgnoreCase)
            ? "wordframenet/lu"
            : $"wordframenet/{baseName}";

        var stream = LooksLikeNativeWfn(path)
            ? FnLuSynsetBridgeIngest.StreamWfnNativeAsync(
                  path, WordFrameNetDecomposer.Source, label, batchSize,
                  FnLuSynsetBridgeIngest.MultiWordNetVersion, ct)
            : FnLuSynsetBridgeIngest.StreamAsync(
                  path, WordFrameNetDecomposer.Source, label, batchSize,
                  FnLuSynsetBridgeIngest.MultiWordNetVersion, ct);
        await foreach (var change in stream)
            yield return change;
    }

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct) =>
        await FnLuSynsetBridgeIngest.EstimateLineCountAsync(path, ct);

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in DataDirs(ecosystemPath))
        {
            foreach (string path in MappingFilesIn(dir))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> MappingFilesIn(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        foreach (string name in MappingFileNames)
        {
            string canonical = Path.Combine(dir, name);
            if (File.Exists(canonical)) yield return canonical;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.map"))
            yield return file;

        foreach (var file in Directory.EnumerateFiles(dir, "*.txt"))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("README", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }

        foreach (string name in ExtensionlessMappingNames)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) yield return path;
        }
    }

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

    private static IEnumerable<string> DataDirs(string ecosystemPath)
    {
        yield return ecosystemPath;

        foreach (var sub in new[] { "WordFrameNet", "wordframenet", "WFN", "eXtendedWFN", "XWFN" })
        {
            string nested = Path.Combine(ecosystemPath, sub);
            if (Directory.Exists(nested)) yield return nested;
        }

        foreach (string root in VaultRoots(ecosystemPath))
        {
            yield return root;

            foreach (var dir in Directory.EnumerateDirectories(root, "WordFrameNet*"))
                yield return dir;

            foreach (var sub in new[] { "WordFrameNet", "WFN", "eXtendedWFN", "XWFN" })
            {
                string nested = Path.Combine(root, sub);
                if (Directory.Exists(nested)) yield return nested;
            }
        }
    }

    private static IEnumerable<string> VaultRoots(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? env = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string full = Path.GetFullPath(env);
            if (seen.Add(full)) yield return full;
        }

        string platformDefault = OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
        if (seen.Add(platformDefault)) yield return platformDefault;

        // NOTE: we deliberately do NOT treat the ecosystem path's parent directory as a vault root.
        // Doing so globbed arbitrary unrelated files when the ecosystem path was a temp/working dir
        // (e.g. the OS temp root's locked installer logs → IOException). The real vault is reached via
        // LAPLACE_DATA_ROOT, the platform default, and the ecosystem path itself + its nested WFN dirs.
    }
}
