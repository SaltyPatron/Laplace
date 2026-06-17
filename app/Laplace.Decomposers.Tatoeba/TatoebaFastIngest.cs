using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Tatoeba;


internal static class TatoebaFastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestSentencesAsync(
        string filePath,
        int batchSize,
        LanguageFilter? langs,
        HashSet<long>? allowedIds,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) yield break;

        var b = NewBuilder("tatoeba/sent", 0, batchSize, commitEpoch: 0);
        int inBatch = 0, bn = 0;
        long rowsInBatch = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            ReadOnlySpan<byte> span = line.Span;
            if (langs?.IsActive == true && !TatoebaRowFilter.MatchesSentenceLanguageFilter(span, langs))
                continue;
            if (!TatoebaSentenceRow.TryParse(span, out var row)) continue;

            TatoebaWitness.WalkSentence(row, b);
            allowedIds?.Add(row.Id);
            rowsInBatch++;
            if (++inBatch >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                bn++;
                b = NewBuilder("tatoeba/sent", bn, batchSize, commitEpoch: 0);
                inBatch = 0;
                rowsInBatch = 0;
            }
        }

        if (inBatch > 0)
            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
    }

    public static async IAsyncEnumerable<SubstrateChange> IngestLinksAsync(
        string filePath,
        int batchSize,
        HashSet<long>? allowedIds,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) yield break;

        var b = NewBuilder("tatoeba/link", 0, batchSize, commitEpoch: 1);
        int inBatch = 0, bn = 0;
        long rowsInBatch = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            ReadOnlySpan<byte> span = line.Span;
            if (!TatoebaRowFilter.MatchesLinkFilter(span, allowedIds)) continue;
            if (!TatoebaLinkRow.TryParse(span, out var row)) continue;

            TatoebaWitness.WalkLink(row, b);
            rowsInBatch++;
            if (++inBatch >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                bn++;
                b = NewBuilder("tatoeba/link", bn, batchSize, commitEpoch: 1);
                inBatch = 0;
                rowsInBatch = 0;
            }
        }

        if (inBatch > 0)
            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
    }

    private static SubstrateChangeBuilder NewBuilder(
        string prefix, int bn, int batchSize, int commitEpoch) =>
        new SubstrateChangeBuilder(TatoebaDecomposer.Source, $"{prefix}/{bn}", null,
            entityCapacity: batchSize * 8,
            physicalityCapacity: batchSize * 16,
            attestationCapacity: batchSize * 4)
            .SetCommitEpoch(commitEpoch);
}
