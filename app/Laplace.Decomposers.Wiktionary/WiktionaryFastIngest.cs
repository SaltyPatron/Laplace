using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

internal static class WiktionaryFastIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestJsonlAsync(
        string filePath,
        int batchSize,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) yield break;

        bool preFilter = WiktionaryJsonFilter.NeedsLanguagePreFilter(filePath, options.Languages);
        var b = NewBuilder("wiktionary/batch-0", batchSize);
        int inBatch = 0, bn = 0;
        long rowsInBatch = 0;

        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(filePath, ct))
        {
            if (line.Length == 0 || line.Span[0] != (byte)'{') continue;
            if (preFilter && options.Languages is { IsActive: true } langs
                && !WiktionaryJsonFilter.MatchesLanguageFilter(line.Span, langs))
                continue;

            if (!WiktionaryWitness.TryWalkRecord(b, line, options)) continue;

            rowsInBatch++;
            if (++inBatch >= batchSize)
            {
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                bn++;
                b = NewBuilder($"wiktionary/batch-{bn}", batchSize);
                inBatch = 0;
                rowsInBatch = 0;
            }
        }

        if (inBatch > 0)
            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(WiktionaryDecomposer.Source, unit, null,
            entityCapacity: batch * 30,
            physicalityCapacity: batch * 30,
            attestationCapacity: batch * 20);
}
