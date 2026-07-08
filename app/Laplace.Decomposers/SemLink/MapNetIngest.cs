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

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct)
    {
        if (Path.GetFileName(path).Equals(LuMappingFile, StringComparison.OrdinalIgnoreCase))
            return await FnLuSynsetBridgeIngest.EstimateLineCountAsync(path, ct);

        long lines = 0;
        await foreach (var _ in ReadLinesAsync(path, ct))
            lines++;
        return lines > 0 ? lines : null;
    }

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static bool ExistsLocally(string dir) => MappingFilesIn(dir).Any();

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

        foreach (var name in new[] { FrameMappingFile, LuMappingFile })
        {
            string canonical = Path.Combine(dir, name);
            if (File.Exists(canonical)) yield return canonical;
        }
    }

    private static IEnumerable<string> DataDirs(string ecosystemPath)
    {
        yield return ecosystemPath;

        foreach (var sub in new[] { "MapNet", "MapNet-0.1", "mapnet", "mapnet-0.1" })
        {
            string nested = Path.Combine(ecosystemPath, sub);
            if (Directory.Exists(nested)) yield return nested;
        }

        foreach (string root in VaultRoots(ecosystemPath))
        {
            yield return root;

            foreach (var dir in Directory.EnumerateDirectories(root, "MapNet*"))
                yield return dir;

            foreach (var sub in new[] { "MapNet", "MapNet-0.1" })
            {
                string nested = Path.Combine(root, sub);
                if (Directory.Exists(nested)) yield return nested;
            }
        }
    }

    private static IEnumerable<string> VaultRoots(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string ingest = LaplaceInstall.ResolveIngestRoot();
        if (seen.Add(ingest)) yield return ingest;

        string platformDefault = OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
        if (seen.Add(platformDefault)) yield return platformDefault;

        string? parent = Path.GetDirectoryName(Path.GetFullPath(ecosystemPath));
        if (!string.IsNullOrEmpty(parent) && seen.Add(parent))
            yield return parent;
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }
}

internal sealed class MapNetMultiFileStream : IMultiFileRecordStream<CategoryCorrespondenceRecord>
{
    private readonly IReadOnlyList<MapNetIngest.MapNetFileSpec> _files;

    public MapNetMultiFileStream(IReadOnlyList<MapNetIngest.MapNetFileSpec> files) => _files = files;

    public async IAsyncEnumerable<(string FileLabel, CategoryCorrespondenceRecord Record)> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var spec in _files)
        {
            var records = spec.IsLuFile
                ? FnLuSynsetBridgeIngest.EnumerateTabRecordsAsync(
                    spec.Path, FnLuSynsetBridgeIngest.MultiWordNetVersion, 0, ct)
                : MapNetIngest.EnumerateFrameRecordsAsync(spec.Path, 0, ct);
            await foreach (var rec in records)
                yield return (spec.Label, rec);
        }
    }
}
