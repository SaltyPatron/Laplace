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
    private const int ColVnRole = 9;     // 10_VN_ROLE  — the thematic role (was dropped)
    private const int ColWnSense = 10;   // 11_WN_SENSE — lemma-specific WN sense key (was dropped)
    private const int ColMcrIli = 11;
    private const int ColFnFrame = 12;
    private const int ColFnFe = 14;      // 15_FN_FRAME_ELEMENT — the frame element (was dropped)
    private const int ColPbRoleset = 15;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VnClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;
    private static readonly Hash128 FeTypeId = EntityTypeRegistry.FrameNetFe;

    internal static async IAsyncEnumerable<SubstrateChange> StreamAsync(
        string path,
        int batchSize,
        LanguageFilter? langs,
        long maxInputUnits = 0,
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
        long rowsTotal = 0;

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

            if (maxInputUnits > 0 && rowsTotal >= maxInputUnits) yield break;
            rowsTotal++;

            // The lemma-specific WN sense (was dropped) — sense-level precision that converges with
            // WordNet's own sense entities, so each predicate corresponds not just to the broad synset
            // but to the exact sense. null when the row carries no sense ("wn:NULL").
            string wnSenseRaw = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColWnSense]);
            Hash128? senseId = wnSenseRaw.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                ? null : SenseAnchor.Id(wnSenseRaw);

            if (TryRoleset(fields[ColPbRoleset], out string? roleset) && roleset is not null)
            {
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(roleset), RolesetTypeId, synId.Value);
                if (senseId is { } rs) StageCorrespondsTo(batch, seen, CategoryAnchor.Id(roleset), RolesetTypeId, rs);
            }

            if (TryFrame(fields[ColFnFrame], out string? frame) && frame is not null)
            {
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(frame), FrameTypeId, synId.Value);
                if (senseId is { } fs) StageCorrespondsTo(batch, seen, CategoryAnchor.Id(frame), FrameTypeId, fs);
            }

            string? vnClass = VerbNetClassKey(fields);
            if (vnClass is not null)
            {
                StageCorrespondsTo(batch, seen, CategoryAnchor.Id(vnClass), VnClassTypeId, synId.Value);
                if (senseId is { } vs) StageCorrespondsTo(batch, seen, CategoryAnchor.Id(vnClass), VnClassTypeId, vs);
            }

            // Role-level alignment — PredicateMatrix uniquely carries the VN thematic-role <-> FN
            // frame-element correspondence in the SAME row as the predicate links, but it was dropped,
            // so only predicate-level links existed (no ARG/role circularization). Emit it keyed
            // EXACTLY as SemLinkRoleMappingIngest (VN role via ContentEmitter, FN FE via CategoryAnchor +
            // FrameNetFe, scoped by VN class) so the two sources reinforce one consensus edge, not fork.
            if (vnClass is not null && fields.Length > ColFnFe)
            {
                string vnRole = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColVnRole]).Trim();
                string fnFe   = SourceEntityIdConventions.StripPredicateMatrixNamespace(fields[ColFnFe]).Trim();
                if (vnRole.Length > 0 && !vnRole.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    && fnFe.Length > 0 && !fnFe.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                {
                    var vnRoleId = ContentEmitter.Emit(batch, vnRole, SemLinkDecomposer.Source);
                    var feId     = CategoryAnchor.Emit(batch, fnFe, FeTypeId, SemLinkDecomposer.Source, TC.AcademicCurated);
                    if (vnRoleId is { } vr && feId is { } fe
                        && seen.Add((vr, fe)))
                        batch.AddAttestation(NativeAttestation.Categorical(
                            vr, "ROLE_CORRESPONDS_TO", fe, SemLinkDecomposer.Source, TC.AcademicCurated,
                            contextId: CategoryAnchor.Id(vnClass)));
                }
            }

            if (++count >= batchSize)
            {
                yield return batch.SetInputUnitsConsumed(count).Build();
                batch = NewBuilder($"semlink/predicate-matrix/{++batchNum}", batchSize);
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
        return parsed is null
            ? null
            : ConceptAnchor.SynsetId(parsed.Value.Offset, parsed.Value.SsType,
                                      parsed.Value.WnVersion ?? "pwn30");
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
