using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Ingests EHU Predicate Matrix TSV (PredicateMatrix.txt): WSD-aligned lexical bridges from
/// PropBank rolesets, VerbNet classes, and FrameNet frames to WordNet synsets via MCR ILI offsets.
/// </summary>
internal static class PredicateMatrixIngest
{
    private const int ColLang = 0;
    private const int ColPos = 1;
    private const int ColVnClass = 4;
    private const int ColVnSubclass = 6;
    private const int ColVnLemma = 8;
    private const int ColMcrIli = 11;
    private const int ColFnFrame = 12;
    private const int ColPbRoleset = 15;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;

    internal static async IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        int batchSize,
        LanguageFilter? langs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (batchSize <= 0) batchSize = 4096;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        string? header = await reader.ReadLineAsync(ct);
        if (header is null) yield break;

        var batch = NewBuilder("semlink/predicate-matrix/0", batchSize);
        var seen = new HashSet<(Hash128 Subject, Hash128 Object)>();
        int count = 0, batchNum = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            var fields = line.Split('\t');
            if (fields.Length <= ColPbRoleset) continue;
            if (fields[ColLang].Equals("1_ID_LANG", StringComparison.Ordinal)) continue;

            string lang = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColLang]);
            string pos = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColPos]);
            if (!lang.Equals("eng", StringComparison.Ordinal) || !pos.Equals("v", StringComparison.Ordinal))
                continue;
            if (langs is { IsActive: true } && !langs.MatchesRaw("eng"))
                continue;

            Hash128? synId = SynsetAnchor(fields[ColMcrIli]);
            if (synId is null) continue;

            if (TryRoleset(fields[ColPbRoleset], out string? roleset) && roleset is not null)
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(roleset), RolesetTypeId, synId.Value);

            if (TryFrame(fields[ColFnFrame], out string? frame) && frame is not null)
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(frame), FrameTypeId, synId.Value);

            string? vnClass = VerbNetClassKey(fields);
            if (vnClass is not null)
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(vnClass), VnClassTypeId, synId.Value);

            if (++count >= batchSize)
            {
                yield return batch.SetInputUnitsConsumed(count).Build();
                batch = NewBuilder($"semlink/predicate-matrix/{++batchNum}", batchSize);
                seen.Clear();
                count = 0;
            }
        }

        if (count > 0)
            yield return batch.SetInputUnitsConsumed(count).Build();
    }

    internal static async Task<long?> EstimateLineCountAsync(string path, CancellationToken ct)
    {
        long lines = 0;
        await foreach (var _ in ReadLinesAsync(path, ct))
            lines++;
        return lines > 1 ? lines - 1 : null;
    }

    internal static bool ExistsUnder(string ecosystemPath) => ResolvePaths(ecosystemPath).Any();

    internal static bool ExistsLocally(string dir) => PredicateMatrixFilesIn(dir).Any();

    internal static IEnumerable<string> ResolvePaths(string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in DataDirs(ecosystemPath))
        {
            foreach (string path in PredicateMatrixFilesIn(dir))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> PredicateMatrixFilesIn(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        string canonical = Path.Combine(dir, "PredicateMatrix.txt");
        if (File.Exists(canonical)) yield return canonical;

        foreach (var file in Directory.EnumerateFiles(dir, "PredicateMatrix*.txt"))
        {
            if (!file.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }

        foreach (var sub in new[] { "PredicateMatrix", "predicate-matrix", "PredicateMatrix.v1.3" })
        {
            string nestedDir = Path.Combine(dir, sub);
            if (!Directory.Exists(nestedDir)) continue;

            string nested = Path.Combine(nestedDir, "PredicateMatrix.txt");
            if (File.Exists(nested)) yield return nested;

            foreach (var file in Directory.EnumerateFiles(nestedDir, "PredicateMatrix*.txt"))
            {
                if (!file.Equals(nested, StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    private static IEnumerable<string> DataDirs(string ecosystemPath)
    {
        yield return ecosystemPath;
        yield return Path.Combine(ecosystemPath, "instances");
        yield return Path.Combine(ecosystemPath, "semlink-master", "instances");
        yield return Path.Combine(ecosystemPath, "PredicateMatrix");
        yield return Path.Combine(ecosystemPath, "predicate-matrix");
        yield return Path.Combine(ecosystemPath, "PredicateMatrix.v1.3");

        foreach (string root in VaultRoots(ecosystemPath))
        {
            yield return root;
            yield return Path.Combine(root, "PredicateMatrix");
            yield return Path.Combine(root, "predicate-matrix");
            yield return Path.Combine(root, "PredicateMatrix.v1.3");
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

    private static string? VerbNetClassKey(string[] fields)
    {
        string lemma = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnLemma]);
        if (lemma.Equals("NULL", StringComparison.OrdinalIgnoreCase) || lemma.Length == 0)
            return null;

        string subclass = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnSubclass]);
        if (!subclass.Equals("NULL", StringComparison.OrdinalIgnoreCase) && subclass.Length > 0)
            return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{subclass}");

        string cls = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnClass]);
        if (cls.Equals("NULL", StringComparison.OrdinalIgnoreCase) || cls.Length == 0)
            return null;
        return SourceEntityIdConventions.NumericVerbNetClassId($"{lemma}-{cls}");
    }

    private static bool TryRoleset(string raw, out string? roleset)
    {
        roleset = null;
        string s = SourceEntityIdConventions.StripPredicateMatrixNamespace(raw);
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase) || s.Length == 0) return false;
        roleset = s;
        return true;
    }

    private static bool TryFrame(string raw, out string? frame)
    {
        frame = null;
        string s = SourceEntityIdConventions.StripPredicateMatrixNamespace(raw);
        if (s.Equals("NULL", StringComparison.OrdinalIgnoreCase) || s.Length == 0) return false;
        frame = s;
        return true;
    }

    private static Hash128? SynsetAnchor(string raw)
    {
        var parsed = SourceEntityIdConventions.ParseMcrSynsetKey(raw);
        return parsed is null ? null : ConceptAnchor.SynsetId(parsed.Value.Offset, parsed.Value.SsType);
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

        b.AddEntity(new EntityRow(subjectId.Value, EntityTier.Vocabulary, subjectType, SemLinkDecomposer.Source));
        CategoryAnchor.AttestCategory(b, subjectId.Value, subjectType, SemLinkDecomposer.Source, TC.AcademicCurated);
        b.AddAttestation(NativeAttestation.Categorical(
            subjectId.Value, "CORRESPONDS_TO", synId, SemLinkDecomposer.Source, TC.AcademicCurated));
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(SemLinkDecomposer.Source, unit, null,
            entityCapacity: batch * 3,
            physicalityCapacity: 0,
            attestationCapacity: batch * 3);
}
