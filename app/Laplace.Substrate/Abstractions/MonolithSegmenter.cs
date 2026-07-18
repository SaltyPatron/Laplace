using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The generic "ingest ONE file maximally fast" unit — record-aligned intra-file
/// parallelism for monolithic sources.
///
/// A monolith (ConceptNet's single <c>assertions.csv</c>, one JSONL, one CSV) has no
/// intra-file parallelism: the whole stream feeds ONE working-set builder whose serial
/// <c>DrainInto</c> stage bounds throughput (compose fans <c>CreateDeferredUnit</c> across
/// P-cores, but every unit then stages into a single non-thread-safe builder one at a time).
/// Measured: ConceptNet at 0.57 cores with 18 threads idle. OMW hit 5.4 cores only because
/// its 1,226 per-language files each became an INDEPENDENT working-set pipeline on the
/// file-worker pool.
///
/// This segmenter gives a monolith that same structure: the already-FRAMED record stream
/// (each element is one complete logical record — a JSONL line, a CSV row, a ConceptNet
/// assertion) is cut on RECORD boundaries into N segments, each an independent producer
/// running the identical <see cref="IngestBatchPipeline.RunAsync"/> working-set spine with
/// its own handler, builder, and commit. A segmented monolith IS a multi-file and rides the
/// same pool OMW spreads its files across — ConceptNet-the-monolith and one OMW .tab end up
/// on the identical fast path.
///
/// Cross-segment conflicts need NO coordination: two segments that emit the same content
/// (e.g. <c>/c/en/dog</c>) produce the SAME content-addressed id, and the consumer's
/// dedup-by-construction merges them. Content-addressing IS the conflict resolution — the
/// final substrate is order-independent and bit-identical to the serial run.
///
/// A chunk boundary NEVER splits a record: a dispatch chunk is a list of WHOLE records taken
/// from the framed stream, so there is no byte-offset guillotine of a half-written record.
/// </summary>
public static class MonolithSegmenter
{
    /// <summary>
    /// Independent segment producers to run for a single-file working-set source. ONE (no
    /// segmentation, straight serial spine) when the source is capped — a bounded validate
    /// run needs the exact cross-record stop point that only the sequential path provides —
    /// or the source is not in working-set mode, or the box has a single compose worker.
    /// Otherwise the compose-worker count: the same P-core-bound pool size the multi-file
    /// lane spreads its files across.
    /// </summary>
    public static int ResolveSegments(IngestBatchConfig config)
    {
        if (!config.WorkingSet) return 1;
        if (config.MaxInputUnits > 0) return 1;
        return Math.Max(1, IngestTopology.Current.ComposeWorkers);
    }

    /// <summary>
    /// Records per dispatch chunk — the round-robin unit handed to a segment. A chunk is a
    /// list of WHOLE records, so cutting the stream here is record-aligned. Kept small (a
    /// fraction of the working-set probe interval) so records spread evenly across segments
    /// and the in-flight raw-record buffers stay bounded: at most segments x depth x chunk
    /// RECORDS are buffered — the cheap parsed records, never their composed tier trees,
    /// which remain bounded by each segment's own working-set flush envelope.
    /// </summary>
    public static int ResolveChunkRecords(IngestBatchConfig config)
    {
        int probe = config.WorkingSetProbeInterval ?? 4096;
        return Math.Clamp(probe / 4, 256, 8192);
    }

    /// <summary>
    /// Run <paramref name="stream"/> as <paramref name="segments"/> independent working-set
    /// pipelines and merge their changes into one stream the caller drains serially. With
    /// <paramref name="segments"/> <= 1 this is exactly <see cref="IngestBatchPipeline.RunAsync"/>.
    /// </summary>
    public static async IAsyncEnumerable<SubstrateChange> RunSegmentedAsync<TRecord>(
        IRecordStream<TRecord> stream,
        Func<int, IIngestRecordHandler<TRecord>> handlerFactory,
        Func<int, IngestBatchConfig> configFactory,
        int segments,
        int chunkRecords,
        string progressLabel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (segments <= 1)
        {
            await foreach (var change in IngestBatchPipeline.RunAsync(
                               stream, handlerFactory(0), configFactory(0), ct))
                yield return change;
            yield break;
        }

        chunkRecords = Math.Max(1, chunkRecords);

        // One bounded input channel per segment carries record-aligned chunks (lists of
        // whole records). The bounded depth backpressures the dispatcher to the real compose
        // rate, so the dispatched-record counter below is a faithful live compose gauge.
        var inputs = new Channel<List<TRecord>>[segments];
        for (int s = 0; s < segments; s++)
            inputs[s] = Channel.CreateBounded<List<TRecord>>(
                new BoundedChannelOptions(2)
                { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });

        var outCh = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(segments * 4)
            { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = true });

        long dispatched = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var dispatcher = Task.Run(async () =>
        {
            int rr = 0;
            long sinceReport = 0;
            var buf = new List<TRecord>(chunkRecords);
            await foreach (var rec in stream.RecordsAsync(ct))
            {
                buf.Add(rec);
                if (buf.Count >= chunkRecords)
                {
                    long n = Interlocked.Add(ref dispatched, buf.Count);
                    await inputs[rr].Writer.WriteAsync(buf, ct).ConfigureAwait(false);
                    rr = rr + 1 == segments ? 0 : rr + 1;
                    // Live compose telemetry DURING the run (dispatch is backpressured to the
                    // compose rate by the bounded input channels), with the source's name —
                    // not silence until the commit.
                    if ((sinceReport += buf.Count) >= 262_144)
                    {
                        sinceReport = 0;
                        Console.WriteLine(
                            $"SEGMENTED_COMPOSE {progressLabel}: {n:N0} records across "
                            + $"{segments} segments "
                            + $"({n / Math.Max(1e-3, sw.Elapsed.TotalSeconds):N0} rec/s)");
                    }
                    buf = new List<TRecord>(chunkRecords);
                }
            }
            if (buf.Count > 0)
            {
                Interlocked.Add(ref dispatched, buf.Count);
                await inputs[rr].Writer.WriteAsync(buf, ct).ConfigureAwait(false);
            }
            for (int s = 0; s < segments; s++) inputs[s].Writer.Complete();
        }, ct);

        var workerTasks = new Task[segments];
        for (int s = 0; s < segments; s++)
        {
            int seg = s;
            workerTasks[s] = Task.Run(async () =>
            {
                var recStream = new ChannelChunkRecordStream<TRecord>(inputs[seg].Reader);
                var handler = handlerFactory(seg);
                var config = configFactory(seg);
                await foreach (var change in IngestBatchPipeline.RunAsync(recStream, handler, config, ct))
                    await outCh.Writer.WriteAsync(change, ct).ConfigureAwait(false);
                // Per-segment progress marker: the runner counts completed segments as
                // files_done. Carries no fold semantics (empty change, writer skips it).
                await outCh.Writer.WriteAsync(
                    IngestBatchPipeline.BuildPeriodBoundary(config.SourceId, config.BatchLabelPrefix), ct)
                    .ConfigureAwait(false);
            }, ct);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.ConfigureAwait(false);
                await Task.WhenAll(workerTasks).ConfigureAwait(false);
                outCh.Writer.Complete();
            }
            catch (Exception ex)
            {
                outCh.Writer.Complete(ex);
            }
        }, ct);

        await foreach (var change in outCh.Reader.ReadAllAsync(ct))
            yield return change;
    }

    /// <summary>Flattens a segment's channel of record-aligned chunks back into a record stream.</summary>
    private sealed class ChannelChunkRecordStream<TRecord>(ChannelReader<List<TRecord>> reader)
        : IRecordStream<TRecord>
    {
        public async IAsyncEnumerable<TRecord> RecordsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in reader.ReadAllAsync(ct).ConfigureAwait(false))
                for (int i = 0; i < chunk.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return chunk[i];
                }
        }
    }
}
