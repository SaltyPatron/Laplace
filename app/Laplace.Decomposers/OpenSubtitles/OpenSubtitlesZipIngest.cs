using System.IO.Compression;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

internal static class OpenSubtitlesZipIngest
{
    public const int BlockPairs = 512;

    /// <summary>
    /// Streams one language-pair zip into bounded, contiguous alignment blocks. Blank
    /// alignments terminate a block so its source ordinal range remains exact.
    /// </summary>
    public static async IAsyncEnumerable<AlignedSubtitleBlock> ReadZipBlocksAsync(
        string zipPath,
        string pairStem,
        int blockPairs = BlockPairs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockPairs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockPairs, BlockPairs);
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

        await using var streamA = entA.Open();
        await using var streamB = entB.Open();
        await using var eA = StreamingUtf8LineReader.ReadLinesAsync(streamA, ct).GetAsyncEnumerator(ct);
        await using var eB = StreamingUtf8LineReader.ReadLinesAsync(streamB, ct).GetAsyncEnumerator(ct);
        var left = new List<byte[]>(blockPairs);
        var right = new List<byte[]>(blockPairs);
        long sourceOrdinal = 0;
        long blockStart = 0;
        while (await eA.MoveNextAsync() && await eB.MoveNextAsync())
        {
            sourceOrdinal++;
            var lineA = eA.Current;
            var lineB = eB.Current;
            int lenA = TrimCr(lineA);
            int lenB = TrimCr(lineB);
            if (lenA == 0 || lenB == 0)
            {
                if (left.Count > 0)
                {
                    yield return BuildBlock(pairStem, blockStart, left, right, langA, langB);
                    left = new List<byte[]>(blockPairs);
                    right = new List<byte[]>(blockPairs);
                }
                continue;
            }
            if (left.Count == 0) blockStart = sourceOrdinal;
            left.Add(lineA.Span[..lenA].ToArray());
            right.Add(lineB.Span[..lenB].ToArray());
            if (left.Count == blockPairs)
            {
                yield return BuildBlock(pairStem, blockStart, left, right, langA, langB);
                left = new List<byte[]>(blockPairs);
                right = new List<byte[]>(blockPairs);
            }
        }
        if (left.Count > 0)
            yield return BuildBlock(pairStem, blockStart, left, right, langA, langB);
    }

    private static AlignedSubtitleBlock BuildBlock(
        string pairStem, long startOrdinal,
        List<byte[]> left, List<byte[]> right, Hash128 leftLanguage, Hash128 rightLanguage) =>
        new(pairStem, startOrdinal, left.ToArray(), right.ToArray(), leftLanguage, rightLanguage);

    private static int TrimCr(ReadOnlyMemory<byte> line)
    {
        int len = line.Length;
        if (len > 0 && line.Span[^1] == (byte)'\r') len--;
        return len;
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
