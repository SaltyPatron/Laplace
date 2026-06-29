using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Ingests MapNet TSV (mapping_frame_synsets.txt, mapping_lus_synsets.txt): FrameNet frame/LU → WordNet synset
/// bridges in MultiWordNet pos#offset notation, resolved to ILI synsets via CILI.
/// </summary>
internal static class MapNetIngest
{
    private const string FrameMappingFile = "mapping_frame_synsets.txt";
    private const string LuMappingFile = "mapping_lus_synsets.txt";

    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;
    private static readonly Hash128 LuTypeId = EntityTypeRegistry.FrameNetLu;

    internal static async IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        int batchSize,
        long maxInputUnits = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Path.GetFileName(path).Equals(LuMappingFile, StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var change in FnLuSynsetBridgeIngest.StreamAsync(
                               path, MapNetDecomposer.Source, "mapnet/lu", batchSize,
                               FnLuSynsetBridgeIngest.MultiWordNetVersion, maxInputUnits, ct))
                yield return change;
            yield break;
        }

        if (batchSize <= 0) batchSize = 4096;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        var batch = NewBuilder("mapnet/frame/0", batchSize);
        var seen = new HashSet<(Hash128 Subject, Hash128 Object)>();
        int count = 0, batchNum = 0;
        long rowsTotal = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            var fields = line.Split('\t');
            if (fields.Length < 2) continue;

            string frame = fields[0].Trim();
            string synRaw = fields[1].Trim();
            if (frame.Length == 0 || synRaw.Length == 0) continue;

            Hash128? synId = SynsetAnchor(synRaw);
            if (synId is null) continue;

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;

            StageCorrespondsTo(batch, seen, CategoryAnchor.Id(frame), FrameTypeId, synId.Value);

            if (++count >= batchSize)
            {
                yield return batch.SetInputUnitsConsumed(count).Build();
                batch = NewBuilder($"mapnet/frame/{++batchNum}", batchSize);
                seen.Clear();
                count = 0;
            }
            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
        }

        if (count > 0)
            yield return batch.SetInputUnitsConsumed(count).Build();
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

        string? env = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string full = Path.GetFullPath(env);
            if (seen.Add(full)) yield return full;
        }

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

    private static Hash128? SynsetAnchor(string raw)
    {
        var parsed = SourceEntityIdConventions.ParseMapNetSynsetKey(raw);
        return parsed is null ? null
            : ConceptAnchor.SynsetId(parsed.Value.Offset, parsed.Value.SsType,
                                     FnLuSynsetBridgeIngest.MultiWordNetVersion);
    }

    private static void StageCorrespondsTo(
        SubstrateChangeBuilder b,
        HashSet<(Hash128 Subject, Hash128 Object)> seen,
        Hash128? subjectId,
        Hash128 subjectType,
        Hash128 synId)
    {
        if (subjectId is null) return;
        if (!seen.Add((subjectId.Value, synId))) return;

        b.AddEntity(new EntityRow(subjectId.Value, EntityTier.Vocabulary, subjectType, MapNetDecomposer.Source));
        CategoryAnchor.AttestCategory(b, subjectId.Value, subjectType, MapNetDecomposer.Source, TC.AcademicCurated);
        b.AddAttestation(NativeAttestation.Categorical(
            subjectId.Value, "CORRESPONDS_TO", synId, MapNetDecomposer.Source, TC.AcademicCurated));
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(MapNetDecomposer.Source, unit, null,
            entityCapacity: batch * 2,
            physicalityCapacity: 0,
            attestationCapacity: batch * 2);
}
