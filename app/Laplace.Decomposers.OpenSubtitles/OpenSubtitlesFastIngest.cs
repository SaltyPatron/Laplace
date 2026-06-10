using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.OpenSubtitles;

internal static class OpenSubtitlesFastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestZipAsync(
        string zipPath,
        string pairStem,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var textEntries = zip.Entries
            .Where(e => e.Length > 0 && IsTextEntry(e.FullName))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .Take(2)
            .ToList();
        if (textEntries.Count != 2) yield break;

        var entA = textEntries[0];
        var entB = textEntries[1];
        Hash128 langA = LanguageReference.Resolve(LangSuffix(entA.FullName));
        Hash128 langB = LanguageReference.Resolve(LangSuffix(entB.FullName));

        string unitStem = Path.GetFileNameWithoutExtension(zipPath);
        var b = NewBuilder($"opensubtitles/{unitStem}/0", batchSize);
        int n = 0, bn = 0;

        await foreach (var (lineA, lineB) in ReadPairedLinesAsync(entA, entB, ct))
        {
            if (lineA.IsEmpty || lineB.IsEmpty) continue;

            var idA = EmitLine(b, lineA);
            var idB = EmitLine(b, lineB);
            if (idA is null || idB is null) continue;

            b.AddEntity(new EntityRow(langA, EntityTier.Vocabulary, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
            b.AddEntity(new EntityRow(langB, EntityTier.Vocabulary, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
            b.AddAttestation(RelationTypeRegistry.Attest(
                idA.Value, "IS_TRANSLATION_OF", idB.Value, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
            b.AddAttestation(RelationTypeRegistry.Attest(
                idA.Value, "HAS_LANGUAGE", langA, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
            b.AddAttestation(RelationTypeRegistry.Attest(
                idB.Value, "HAS_LANGUAGE", langB, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));

            if (++n >= batchSize)
            {
                yield return b.Build();
                b = NewBuilder($"opensubtitles/{unitStem}/{++bn}", batchSize);
                n = 0;
            }
        }

        if (n > 0) yield return b.Build();
    }

    private static Hash128? EmitLine(SubstrateChangeBuilder b, ReadOnlyMemory<byte> line)
    {
        int len = line.Length;
        if (len > 0 && line.Span[^1] == (byte)'\r') len--;
        return ContentWitnessBatch.TryAppendToBuilder(
            b, line.Span[..len], OpenSubtitlesDecomposer.Source, out var root)
            ? root : null;
    }

    private static async IAsyncEnumerable<(ReadOnlyMemory<byte> A, ReadOnlyMemory<byte> B)> ReadPairedLinesAsync(
        ZipArchiveEntry entA, ZipArchiveEntry entB, [EnumeratorCancellation] CancellationToken ct)
    {
        string pathA = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        string pathB = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await using (var s = entA.Open())
            await using (var fs = File.Create(pathA))
                await s.CopyToAsync(fs, ct);
            await using (var s = entB.Open())
            await using (var fs = File.Create(pathB))
                await s.CopyToAsync(fs, ct);

            await using var eA = StreamingUtf8LineReader.ReadLinesAsync(pathA, ct).GetAsyncEnumerator(ct);
            await using var eB = StreamingUtf8LineReader.ReadLinesAsync(pathB, ct).GetAsyncEnumerator(ct);
            while (await eA.MoveNextAsync() && await eB.MoveNextAsync())
                yield return (eA.Current, eB.Current);
        }
        finally
        {
            if (File.Exists(pathA)) File.Delete(pathA);
            if (File.Exists(pathB)) File.Delete(pathB);
        }
    }

    private static bool IsTextEntry(string name) =>
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);

    private static string LangSuffix(string entryName)
    {
        string baseName = Path.GetFileNameWithoutExtension(entryName);
        int dot = baseName.LastIndexOf('.');
        return dot >= 0 ? baseName[(dot + 1)..] : baseName;
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(OpenSubtitlesDecomposer.Source, unit, null,
            entityCapacity: batch * 8, physicalityCapacity: batch * 8, attestationCapacity: batch * 4);
}
