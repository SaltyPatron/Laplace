using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;

/// <summary>
/// Streaming TSV ingest for assertions.csv — no grammar compose, one file pass, bounded memory.
/// </summary>
internal static class ConceptNetFastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestAssertionsAsync(
        string filePath,
        int batchSize,
        LanguageFilter? langs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) yield break;

        var arena = new ArenaRmsTracker();
        var witness = new ConceptNetWitness(arena, langs);
        var b = NewBuilder(0, batchSize);
        int inBatch = 0, bn = 0;
        long rowsInBatch = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            ReadOnlySpan<byte> span = line.Span;
            if (langs?.IsActive == true && !ConceptNetRowFilter.MatchesLanguageFilter(span, langs))
                continue;
            if (!ConceptNetTsvRow.TryParse(span, out var row)) continue;

            witness.WalkAssertion(row, b);
            rowsInBatch++;
            if (++inBatch >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                bn++;
                b = NewBuilder(bn, batchSize);
                inBatch = 0;
                rowsInBatch = 0;
            }
        }

        if (inBatch > 0)
            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
    }

    private static SubstrateChangeBuilder NewBuilder(int bn, int batchSize) =>
        new SubstrateChangeBuilder(ConceptNetDecomposer.Source, $"conceptnet/{bn}", null,
            entityCapacity: batchSize * 32,
            physicalityCapacity: batchSize * 32,
            attestationCapacity: batchSize * 8);

}
