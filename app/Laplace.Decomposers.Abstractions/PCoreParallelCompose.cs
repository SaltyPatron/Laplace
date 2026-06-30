using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// P-CORE-PINNED PARALLEL COMPOSE for single-enumerator decomposers.
///
/// A serial DecomposeAsync is compute-bound on ONE thread; the per-record compose (content-id
/// derivation = BLAKE3 over canonical bytes + attestation assembly) is a pure function over the
/// record, so it parallelizes. But worker COUNT alone is not enough — on a hybrid CPU the OS
/// scatters threads onto the weak E-cores, so each worker thread PINS itself to the P-core set
/// (<see cref="CpuTopology.PinCurrentThreadToPerformanceCores"/>, verified) before composing.
///
/// SubstrateChangeBuilder is single-threaded by design (HashSets + a native content stage), so each
/// worker owns its OWN builder over a DISJOINT slice of records and emits independent
/// SubstrateChanges. Identity is content-addressed, so duplicate ids across workers are folded by
/// the server-side set-based merge — no cross-builder coordination is required. Yields each
/// completed change back to the caller in arrival order.
///
/// Cross-platform: pinning is abstracted behind CpuTopology (Windows SetThreadAffinityMask to the
/// P-core mask; Linux sched_setaffinity to /sys/devices/cpu_core/cpus). On a non-hybrid box pinning
/// is a verified no-op and the workers run unpinned — correctness is unaffected.
/// </summary>
public static class PCoreParallelCompose
{
    /// <summary>
    /// Grammar spine overload: drives <paramref name="handler"/> on each record using a fresh-bypass
    /// compose (no descent probes). Owns CreateDeferredUnit / DrainInto / WalkWitness lifecycle.
    /// </summary>
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

    /// <summary>
    /// Stream <paramref name="records"/> through <paramref name="workerCount"/> P-core-pinned
    /// compose workers. <paramref name="compose"/> writes one record's rows into a worker-local
    /// builder; <paramref name="newBuilder"/> mints a fresh builder when the previous one reaches
    /// <paramref name="recordsPerChange"/> records (so emitted changes stay batch-sized). Each
    /// completed builder is built and yielded.
    /// </summary>
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

        // Bound the in-flight work so a stall doesn't accumulate the whole corpus in memory.
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

        // Feeder: one writer pumps records into the bounded input channel.
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

        // Workers: each is a dedicated OS thread (so the affinity pin sticks), pinned to P-cores,
        // draining the input channel into its own builder and emitting batch-sized changes.
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
                    // Synchronous drain on this dedicated thread; the channel read blocks via the
                    // synchronous TryRead + WaitToRead loop driven off the thread's own sync wait.
                    while (true)
                    {
                        if (runCt.IsCancellationRequested) break;
                        if (reader.TryRead(out var rec))
                        {
                            compose(builder, rec);
                            if (++n >= recordsPerChange)
                            {
                                // BuildAsync flushes the deferred-content containment (the O(tier)
                                // skip) when enabled; it is exactly Build() when it is not. Blocking
                                // on this dedicated, sync-context-free worker thread is deadlock-safe.
                                output.Writer.TryWrite(builder.SetInputUnitsConsumed(n).BuildAsync(runCt).GetAwaiter().GetResult());
                                builder = newBuilder();
                                n = 0;
                            }
                            continue;
                        }
                        // No item ready: block until more arrive or the channel completes.
                        var wait = reader.WaitToReadAsync(runCt).AsTask();
                        if (!wait.GetAwaiter().GetResult()) break; // completed + drained
                    }
                    if (n > 0) output.Writer.TryWrite(builder.SetInputUnitsConsumed(n).BuildAsync(runCt).GetAwaiter().GetResult());
                }
                catch (OperationCanceledException) { /* shutting down */ }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    stop.Cancel();
                }
                finally
                {
                    // The LAST worker to finish completes the output — carrying the first compose
                    // error if any, so the consumer's ReadAllAsync rethrows that error deterministically
                    // (rather than an incidental OperationCanceledException from the shared stop token).
                    if (workerDone.Signal())
                        output.Writer.TryComplete(errors.TryPeek(out var first) ? first : null);
                }
            }) { IsBackground = true, Name = $"ingest-pcore-{wId}" };
            workers[w].Start();
        }

        try
        {
            await foreach (var change in output.Reader.ReadAllAsync(ct))
                yield return change;
        }
        finally
        {
            try { await feeder; } catch { /* feeder error already surfaced via errors/stop */ }
            foreach (var t in workers) t.Join();
        }
    }
}
