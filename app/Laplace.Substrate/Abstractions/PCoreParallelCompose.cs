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
        var output = Channel.CreateUnbounded<SubstrateChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
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
                    var reader = input.Reader;
                    while (true)
                    {
                        if (runCt.IsCancellationRequested) break;
                        if (reader.TryRead(out var rec))
                        {
                            compose(builder, rec);
                            if (++n >= recordsPerChange)
                            {
                                output.Writer.TryWrite(builder.SetInputUnitsConsumed(n).BuildAsync(runCt).GetAwaiter().GetResult());
                                builder = newBuilder();
                                n = 0;
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
