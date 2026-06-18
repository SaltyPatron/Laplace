using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Atomic2020;


internal static class Atomic2020FastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestFileAsync(
        string filePath,
        string splitLabel,
        int batchSize,
        Hash128 splitId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) yield break;

        var b = NewBuilder($"atomic/{splitLabel}/0", batchSize);
        int inBatch = 0, bn = 0;
        long rowsInBatch = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            ReadOnlySpan<byte> span = line.Span;
            if (!Atomic2020TsvRow.TryParse(span, out var row)) continue;

            Atomic2020Witness.WalkRow(row, b);
            rowsInBatch++;
            if (++inBatch >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                bn++;
                b = NewBuilder($"atomic/{splitLabel}/{bn}", batchSize);
                inBatch = 0;
                rowsInBatch = 0;
            }
        }

        if (inBatch > 0)
            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batchSize) =>
        new SubstrateChangeBuilder(Atomic2020Decomposer.Source, unit, null,
            entityCapacity: batchSize * 4,
            physicalityCapacity: batchSize * 8,
            attestationCapacity: batchSize * 2)
            .SetCommitEpoch(0);
}
