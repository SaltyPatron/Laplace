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

        await foreach (var line in ReadLinesAsync(filePath, ct))
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

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        var carry = new byte[256];
        int carryLen = 0;
        var buf = new byte[1 << 20];

        int read;
        while ((read = await fs.ReadAsync(buf, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buf[i] != (byte)'\n') continue;

                int lineLen = carryLen + (i - start);
                var line = new byte[lineLen];
                if (carryLen > 0)
                {
                    carry.AsSpan(0, carryLen).CopyTo(line);
                    Buffer.BlockCopy(buf, start, line, carryLen, i - start);
                    carryLen = 0;
                }
                else
                {
                    Buffer.BlockCopy(buf, start, line, 0, i - start);
                }

                if (lineLen > 0 && line[^1] == (byte)'\r') lineLen--;
                if (lineLen > 0) yield return line.AsMemory(0, lineLen);
                start = i + 1;
            }

            int tail = read - start;
            if (tail <= 0) continue;
            if (carryLen + tail > carry.Length)
            {
                var grown = new byte[Math.Max(carry.Length * 2, carryLen + tail)];
                Buffer.BlockCopy(carry, 0, grown, 0, carryLen);
                carry = grown;
            }
            Buffer.BlockCopy(buf, start, carry, carryLen, tail);
            carryLen += tail;
        }

        if (carryLen > 0)
        {
            int lineLen = carryLen;
            if (lineLen > 0 && carry[lineLen - 1] == (byte)'\r') lineLen--;
            if (lineLen > 0) yield return carry.AsMemory(0, lineLen);
        }
    }
}
