using System.IO.Compression;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

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

    public static bool TryAppendPair(
        SubstrateChangeBuilder b,
        OpenSubtitlesLinePair pair,
        out Hash128 rootA)
    {
        rootA = default;
        if (!ContentWitnessBatch.TryAppendToBuilder(
                b, pair.LineA, OpenSubtitlesDecomposer.Source, out var idA))
            return false;
        if (!ContentWitnessBatch.TryAppendToBuilder(
                b, pair.LineB, OpenSubtitlesDecomposer.Source, out var idB))
            return false;

        b.AddAttestation(NativeAttestation.Categorical(
            idA, "IS_TRANSLATION_OF", idB, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
        b.AddAttestation(NativeAttestation.Categorical(
            idA, "HAS_LANGUAGE", pair.LangA, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
        b.AddAttestation(NativeAttestation.Categorical(
            idB, "HAS_LANGUAGE", pair.LangB, OpenSubtitlesDecomposer.Source, TC.StructuredCorpus));
        rootA = idA;
        return true;
    }

    public static void Absorb(SubstrateChangeBuilder acc, SubstrateChange micro)
    {
        foreach (var e in micro.Entities) acc.AddEntity(e);
        foreach (var p in micro.Physicalities) acc.AddPhysicality(p);
        foreach (var a in micro.Attestations) acc.AddAttestation(a);
        foreach (var s in micro.IntentStages)
            if (s is { IsInvalid: false })
                acc.AddIntentStage(s);
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

    internal static SubstrateChangeBuilder NewBuilder(
        string unit, int batch, Hash128 langA, Hash128 langB, ISubstrateReader? reader = null)
    {
        var b = new SubstrateChangeBuilder(OpenSubtitlesDecomposer.Source, unit, null,
            entityCapacity: batch * 4,
            physicalityCapacity: batch * 8,
            attestationCapacity: batch * 4);
        b.AddEntity(new EntityRow(langA, EntityTier.Word, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
        b.AddEntity(new EntityRow(langB, EntityTier.Word, EntityTypeRegistry.Language, OpenSubtitlesDecomposer.Source));
        return b;
    }
}
