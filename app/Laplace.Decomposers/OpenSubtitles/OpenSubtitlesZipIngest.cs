using System.IO.Compression;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

internal static class OpenSubtitlesZipIngest
{
    /// <summary>
    /// One zip → relation-triple records. Single copy from the streaming line buffer
    /// into the record's owned byte[] — no intermediate LinePair.ToArray().
    /// </summary>
    public static async IAsyncEnumerable<RelationTripleRecord> ReadZipTripleAsync(
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

        await using var streamA = entA.Open();
        await using var streamB = entB.Open();
        await using var eA = StreamingUtf8LineReader.ReadLinesAsync(streamA, ct).GetAsyncEnumerator(ct);
        await using var eB = StreamingUtf8LineReader.ReadLinesAsync(streamB, ct).GetAsyncEnumerator(ct);
        while (await eA.MoveNextAsync() && await eB.MoveNextAsync())
        {
            var lineA = eA.Current;
            var lineB = eB.Current;
            if (lineA.IsEmpty || lineB.IsEmpty) continue;
            int lenA = TrimCr(lineA);
            int lenB = TrimCr(lineB);
            if (lenA == 0 || lenB == 0) continue;
            yield return new RelationTripleRecord(
                lineA.Span[..lenA].ToArray(),
                "IS_TRANSLATION_OF",
                lineB.Span[..lenB].ToArray(),
                SubjectLangId: langA,
                ObjectLangId: langB);
        }
    }

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
