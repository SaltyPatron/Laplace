using System.IO.Compression;
using System.Runtime.CompilerServices;
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
        var b = NewBuilder($"opensubtitles/{unitStem}/0", batchSize, langA, langB);
        int n = 0, bn = 0;

        await foreach (var (lineA, lineB) in ReadPairedLinesAsync(entA, entB, ct))
        {
            if (lineA.IsEmpty || lineB.IsEmpty) continue;

            var idA = EmitLine(b, lineA);
            var idB = EmitLine(b, lineB);
            if (idA is null || idB is null) continue;

            b.AddAttestation(NativeAttestation.Categorical(
                idA.Value, "IS_TRANSLATION_OF", idB.Value, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                idA.Value, "HAS_LANGUAGE", langA, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
            b.AddAttestation(NativeAttestation.Categorical(
                idB.Value, "HAS_LANGUAGE", langB, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));

            if (++n >= batchSize)
            {
                yield return b.Build();
                b = NewBuilder($"opensubtitles/{unitStem}/{++bn}", batchSize, langA, langB);
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
        await using var streamA = entA.Open();
        await using var streamB = entB.Open();
        await using var eA = StreamingUtf8LineReader.ReadLinesAsync(streamA, ct).GetAsyncEnumerator(ct);
        await using var eB = StreamingUtf8LineReader.ReadLinesAsync(streamB, ct).GetAsyncEnumerator(ct);
        while (await eA.MoveNextAsync() && await eB.MoveNextAsync())
            yield return (eA.Current, eB.Current);
    }

    private static bool IsTextEntry(string name)
    {
        string leaf = Path.GetFileName(name);
        if (leaf.Equals("README", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            return true;

        return leaf.StartsWith("OpenSubtitles.", StringComparison.OrdinalIgnoreCase);
    }

    private static string LangSuffix(string entryName)
    {
        string leaf = Path.GetFileName(entryName);
        if (leaf.StartsWith("OpenSubtitles.", StringComparison.OrdinalIgnoreCase))
        {
            int lastDot = leaf.LastIndexOf('.');
            if (lastDot > 0 && lastDot + 1 < leaf.Length)
                return leaf[(lastDot + 1)..];
        }
        string baseName = Path.GetFileNameWithoutExtension(entryName);
        int dot = baseName.LastIndexOf('.');
        return dot >= 0 ? baseName[(dot + 1)..] : baseName;
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch, Hash128 langA, Hash128 langB)
    {
        var b = new SubstrateChangeBuilder(OpenSubtitlesDecomposer.Source, unit, null,
            entityCapacity: batch * 4,
            physicalityCapacity: batch * 8,
            attestationCapacity: batch * 4);
        b.AddEntity(new EntityRow(langA, EntityTier.Vocabulary, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
        b.AddEntity(new EntityRow(langB, EntityTier.Vocabulary, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
        return b;
    }
}
