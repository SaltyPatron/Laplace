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

        OmwIngestPhase phase,

        [EnumeratorCancellation] CancellationToken ct = default)

    {

        int batch = batchSize > 1 ? batchSize : DefaultBatch;

        string phaseTag = phase switch

        {

            OmwIngestPhase.Content => "content",

            OmwIngestPhase.Attestations => "att",

            _ => "legacy",

        };

        int commitEpoch = phase == OmwIngestPhase.Attestations ? 1 : 0;

        int fileWorkers = ResolveFileWorkers();



        var tabFiles = OMWTabFiles.EnumerateTabFiles(wnsDir, langs)

            .OrderBy(p => p, StringComparer.Ordinal)

            .ToList();



        if (fileWorkers <= 1 || tabFiles.Count <= 1)

        {

            await foreach (var change in IngestFilesSerialAsync(

                tabFiles, batch, phaseTag, commitEpoch, maxInputUnits, phase, ct))

                yield return change;

            yield break;

        }



        await foreach (var change in IngestFilesParallelAsync(

            tabFiles, batch, phaseTag, commitEpoch, maxInputUnits, phase, fileWorkers, ct))

            yield return change;

    }



    private static int ResolveFileWorkers()

    {

        string? env = Environment.GetEnvironmentVariable("LAPLACE_INGEST_WORKERS");

        if (int.TryParse(env, out int w) && w > 1) return w;

        return 1;

    }



    private static async IAsyncEnumerable<SubstrateChange> IngestFilesSerialAsync(

        IReadOnlyList<string> tabFiles,

        int batch,

        string phaseTag,

        int commitEpoch,

        long maxInputUnits,

        OmwIngestPhase phase,

        [EnumeratorCancellation] CancellationToken ct)

    {

        long rowsParsed = 0;

        int fileBn = 0;



        foreach (string tabFile in tabFiles)

        {

            ct.ThrowIfCancellationRequested();

            if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)

                yield break;



            await foreach (var change in IngestOneFileAsync(

                tabFile, batch, phaseTag, commitEpoch, fileBn++, phase, ct))

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

        string phaseTag,

        int commitEpoch,

        long maxInputUnits,

        OmwIngestPhase phase,

        int fileWorkers,

        [EnumeratorCancellation] CancellationToken ct)

    {

        var outChannel = Channel.CreateUnbounded<SubstrateChange>(

            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });



        var workers = new Task[Math.Min(fileWorkers, tabFiles.Count)];

        int nextFile = -1;



        for (int w = 0; w < workers.Length; w++)

        {

            int workerId = w;

            workers[w] = Task.Run(async () =>

            {

                while (true)

                {

                    int fileIdx = Interlocked.Increment(ref nextFile);

                    if (fileIdx >= tabFiles.Count) break;



                    string tabFile = tabFiles[fileIdx];

                    await foreach (var change in IngestOneFileAsync(

                        tabFile, batch, phaseTag, commitEpoch, fileIdx, phase, ct))

                    {

                        await outChannel.Writer.WriteAsync(change, ct);

                    }

                }

            }, ct);

        }



        var closer = Task.Run(async () =>

        {

            await Task.WhenAll(workers);

            outChannel.Writer.TryComplete();

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

        string phaseTag,

        int commitEpoch,

        int fileBn,

        OmwIngestPhase phase,

        [EnumeratorCancellation] CancellationToken ct)

    {

        string fileLang = OMWTabFiles.FileLang(tabFile);

        var witness = new OMWGrammarWitness(fileLang, phase);



        await foreach (var change in StructuredGrammarIngest.IngestFileAsync(

            tabFile,

            modalityId: "tsv",

            sourceId: OMWDecomposer.Source,

            witness: witness,

            batchSize: batch,

            witnessWeight: 1.0,

            batchLabelPrefix: $"omw/{phaseTag}/{fileBn}",

            reportUnits: null,

            contextId: null,

            commitEpoch: commitEpoch,

            acceptRow: static line => line.Length > 0 && line[0] != (byte)'#',

            ct: ct))

        {

            yield return change;

        }

    }

}

