using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OMW;

internal static class OMWGrammarIngest
{
    private const int DefaultBatch = 2048;

    public static async IAsyncEnumerable<SubstrateChange> IngestFilesAsync(
        string wnsDir,
        LanguageFilter? langs,
        int batchSize,
        long maxInputUnits,
        ISubstrateReader? containmentReader = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int batch = batchSize > 1 ? batchSize : DefaultBatch;
        int fileWorkers = maxInputUnits > 0 ? 1 : IngestParallelism.ResolveFileWorkers();

        var tabFiles = OMWTabFiles.EnumerateTabFiles(wnsDir, langs)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (fileWorkers <= 1 || tabFiles.Count <= 1)
        {
            await foreach (var change in IngestFilesSerialAsync(
                tabFiles, batch, maxInputUnits, containmentReader, ct))
                yield return change;
            yield break;
        }

        await foreach (var change in IngestFilesParallelAsync(
            tabFiles, batch, maxInputUnits, fileWorkers, containmentReader, ct))
            yield return change;
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestFilesSerialAsync(
        IReadOnlyList<string> tabFiles,
        int batch,
        long maxInputUnits,
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long rowsParsed = 0;
        int fileBn = 0;

        foreach (string tabFile in tabFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                yield break;

            long fileCap = maxInputUnits > 0 ? maxInputUnits - rowsParsed : 0;
            if (fileCap == 0 && maxInputUnits > 0) yield break;

            await foreach (var change in IngestOneFileAsync(
                tabFile, batch, fileBn++, fileCap, containmentReader, ct))
            {
                rowsParsed += change.Metadata.InputUnitsConsumed;
                yield return change;
                if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                    yield break;
            }
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestFilesParallelAsync(
        IReadOnlyList<string> tabFiles,
        int batch,
        long maxInputUnits,
        int fileWorkers,
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Bounded: file workers are faster than the downstream commit consumer. An unbounded
        // channel lets them race ahead by hundreds of files during any DB stall (e.g. speculative-
        // insert blocking), accumulating gigabytes of SubstrateChange batches in managed heap and
        // exhausting native memory for subsequent grammar_compose calls (returns -3).
        // Back-pressure via FullMode.Wait serialises production to the commit rate.
        var outChannel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(fileWorkers * 4)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var workers = new Task[Math.Min(fileWorkers, tabFiles.Count)];
        int nextFile = -1;

        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (true)
                {
                    int fileIdx = Interlocked.Increment(ref nextFile);
                    if (fileIdx >= tabFiles.Count) break;

                    string tabFile = tabFiles[fileIdx];
                    await foreach (var change in IngestOneFileAsync(
                        tabFile, batch, fileIdx, maxInputUnits, containmentReader, ct))
                    {
                        await outChannel.Writer.WriteAsync(change, ct);
                    }
                }
            }, ct);
        }

        // Must always complete the channel, even on worker fault -- otherwise an exception in any one
        // of N parallel file-workers is swallowed into this unobserved closer task, the channel never
        // signals done, and the consumer's WaitToReadAsync below hangs forever with zero CPU and no
        // error surfaced anywhere. Propagate the fault into the channel so the consumer rethrows it.
        var closer = Task.Run(() => Task.WhenAll(workers), ct)
            .ContinueWith(t => outChannel.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        long rowsParsed = 0;
        while (await outChannel.Reader.WaitToReadAsync(ct))
        {
            while (outChannel.Reader.TryRead(out var change))
            {
                rowsParsed += change.Metadata.InputUnitsConsumed;
                yield return change;
                if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                {
                    await closer;
                    yield break;
                }
            }
        }

        await closer;
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestOneFileAsync(
        string tabFile,
        int batch,
        int fileBn,
        long maxInputUnits,
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string fileLang = OMWTabFiles.FileLang(tabFile);
        var witness = new OMWGrammarWitness(fileLang);

        await foreach (var change in StructuredGrammarIngest.IngestFileAsync(
            tabFile,
            modalityId: "tsv",
            sourceId: OMWDecomposer.Source,
            witness: witness,
            batchSize: batch,
            witnessWeight: 1.0,
            batchLabelPrefix: $"omw/{fileBn}",
            reportUnits: null,
            contextId: null,
            commitEpoch: 0,
            acceptRow: static line => line.Length > 0 && line[0] != (byte)'#',
            maxInputUnits: maxInputUnits,
            containmentReader: containmentReader,
            ct: ct))
        {
            yield return change;
        }
    }
}
