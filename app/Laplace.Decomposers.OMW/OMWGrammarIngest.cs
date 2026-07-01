using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
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
        int fileWorkers = IngestParallelism.ResolveFileWorkers();

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
        CpuTopology.PinWorkerThread(0);
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





        var outChannel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(IngestTopology.Current.Sizing.FileWorkerChannelDepth)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        int workerCount = Math.Min(fileWorkers, tabFiles.Count);
        var threads = new Thread[workerCount];
        var errors = new ConcurrentQueue<Exception>();
        int nextFile = -1;

        for (int w = 0; w < workerCount; w++)
        {
            int workerId = w;
            threads[w] = new Thread(() =>
            {
                try
                {
                    CpuTopology.PinWorkerThread(workerId);
                    while (true)
                    {
                        int fileIdx = Interlocked.Increment(ref nextFile);
                        if (fileIdx >= tabFiles.Count) break;

                        string tabFile = tabFiles[fileIdx];
                        var fileChanges = IngestOneFileAsync(
                            tabFile, batch, fileIdx, maxInputUnits, containmentReader, ct);
                        var enumerator = fileChanges.GetAsyncEnumerator(ct);
                        try
                        {
                            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                            {
                                var change = enumerator.Current;
                                outChannel.Writer.WriteAsync(change, ct).AsTask().GetAwaiter().GetResult();
                            }
                        }
                        finally
                        {
                            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Enqueue(ex);
                }
            })
            { IsBackground = true, Name = $"omw-file-pcore-{workerId}" };
            threads[w].Start();
        }

        var closer = Task.Run(() =>
        {
            foreach (var t in threads) t.Join();
            outChannel.Writer.TryComplete(errors.TryPeek(out var first) ? first : null);
        }, ct);

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
            EtlManifest.Get("omw"),
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
