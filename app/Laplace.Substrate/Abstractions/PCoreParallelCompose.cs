using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class PCoreParallelCompose
{
    public static IAsyncEnumerable<SubstrateChange> RunAsync<TRecord>(
        IAsyncEnumerable<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        IngestBatchConfig config,
        int workerCount,
        CancellationToken ct = default)
    {
        int batchNum = -1;
        return RunAsync(
            records,
            workerCount,
            config.BatchSize,
            () => config.NewBuilder(Interlocked.Increment(ref batchNum)),
            (builder, record) =>
            {
                using var unit = handler.CreateDeferredUnit(record);
                var root = unit.DrainInto(builder, config.WitnessWeight, null);
                handler.WalkWitness(record, root, builder, unit);
            },
            ct);
    }

    public static async IAsyncEnumerable<SubstrateChange> RunAsync<TRecord>(
        IAsyncEnumerable<TRecord> records,
        int workerCount,
        int recordsPerChange,
        Func<SubstrateChangeBuilder> newBuilder,
        Action<SubstrateChangeBuilder, TRecord> compose,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(newBuilder);
        ArgumentNullException.ThrowIfNull(compose);
        workerCount = Math.Max(1, workerCount);
        recordsPerChange = Math.Max(1, recordsPerChange);

        var input = Channel.CreateBounded<TRecord>(new BoundedChannelOptions(workerCount * 4 + 4)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Bounded, and it matters: with an unbounded output channel, workers
        // that outpace the single consumer (the runner's per-attestation
        // consensus accumulate) pile fully-staged changes into RAM without
        // limit — measured live as multi-GB growth on the chess lane. Bounded,
        // the producers block and the pipeline self-limits to the consumer's
        // real pace instead of turning the imbalance into memory.
        var output = Channel.CreateBounded<SubstrateChange>(new BoundedChannelOptions(workerCount * 2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = stop.Token;
        var errors = new ConcurrentQueue<Exception>();

        var feeder = Task.Run(async () =>
        {
            try
            {
                await foreach (var r in records.WithCancellation(runCt))
                    await input.Writer.WriteAsync(r, runCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Enqueue(ex);
                stop.Cancel();
            }
            finally { input.Writer.TryComplete(); }
        }, runCt);

        var workers = new Thread[workerCount];
        var workerDone = new CountdownEvent(workerCount);
        for (int w = 0; w < workerCount; w++)
        {
            int wId = w;
            workers[w] = new Thread(() =>
            {
                CpuTopology.RequirePerformanceCorePin();
                try
                {
                    var builder = newBuilder();
                    int n = 0;
                    // Yield on STAGED BYTES, not record count: records vary
                    // ~1000x in substrate weight (a wiktionary entry is
                    // ~1,100 rows), so a count quota is minutes-to-hours of
                    // silence on fat corpora while a byte quota is a steady
                    // cadence and a per-worker memory bound. The count quota
                    // remains as the upper bound for feather-weight records.
                    const long yieldBytes = 256L << 20;
                    int sinceCheck = 0;
                    var reader = input.Reader;
                    while (true)
                    {
                        if (runCt.IsCancellationRequested) break;
                        if (reader.TryRead(out var rec))
                        {
                            compose(builder, rec);
                            n++;
                            bool quota = n >= recordsPerChange;
                            if (!quota && ++sinceCheck >= 256)
                            {
                                sinceCheck = 0;
                                quota = builder.StagedBytesEstimate >= yieldBytes;
                            }
                            if (quota)
                            {
                                output.Writer.TryWrite(builder.SetInputUnitsConsumed(n).BuildAsync(runCt).GetAwaiter().GetResult());
                                builder = newBuilder();
                                n = 0;
                                sinceCheck = 0;
                            }
                            continue;
                        }
                        var wait = reader.WaitToReadAsync(runCt).AsTask();
                        if (!wait.GetAwaiter().GetResult()) break;
                    }
                    if (n > 0) output.Writer.TryWrite(builder.SetInputUnitsConsumed(n).BuildAsync(runCt).GetAwaiter().GetResult());
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    stop.Cancel();
                }
                finally
                {
                    if (workerDone.Signal())
                        output.Writer.TryComplete(errors.TryPeek(out var first) ? first : null);
                }
            })
            { IsBackground = true, Name = $"ingest-pcore-{wId}" };
            workers[w].Start();
        }

        try
        {
            await foreach (var change in output.Reader.ReadAllAsync(ct))
                yield return change;
        }
        finally
        {
            try { await feeder; } catch { }
            foreach (var t in workers) t.Join();
        }
    }
}
