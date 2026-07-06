using System.IO.Compression;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

internal readonly struct OpenSubtitlesLinePair
{
    public required byte[] LineA { get; init; }
    public required byte[] LineB { get; init; }
    public required Hash128 LangA { get; init; }
    public required Hash128 LangB { get; init; }
    public required string PairStem { get; init; }
}

internal static class OpenSubtitlesZipIngest
{
    public static async IAsyncEnumerable<OpenSubtitlesLinePair> ReadZipPairsAsync(
        string zipPath,
        string pairStem,
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
        VocabularyNames.TrackLanguage(OpenSubtitlesDecomposer.LanguageNames, LangSuffix(entA.FullName));
        VocabularyNames.TrackLanguage(OpenSubtitlesDecomposer.LanguageNames, LangSuffix(entB.FullName));

        await foreach (var (lineA, lineB) in ReadPairedLinesAsync(entA, entB, ct))
        {
            if (lineA.IsEmpty || lineB.IsEmpty) continue;
            int lenA = TrimCr(lineA);
            int lenB = TrimCr(lineB);
            yield return new OpenSubtitlesLinePair
            {
                LineA = lineA.Span[..lenA].ToArray(),
                LineB = lineB.Span[..lenB].ToArray(),
                LangA = langA,
                LangB = langB,
                PairStem = pairStem,
            };
        }
    }

    private static int TrimCr(ReadOnlyMemory<byte> line)
    {
        int len = line.Length;
        if (len > 0 && line.Span[^1] == (byte)'\r') len--;
        return len;
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
}
