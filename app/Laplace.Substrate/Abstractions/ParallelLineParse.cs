using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Fans a line-framed file's per-line parse across N workers — the spine-owned
/// generalization of <see cref="ParallelGrammarFileRecordStream"/> for managed
/// parsers (Utf8JsonReader lanes and kin). A feeder copies each line off
/// <see cref="StreamingUtf8LineReader"/>'s REUSED buffer (handing that memory
/// across threads without the copy is a data race — the copy is the price the
/// grammar pool already pays), N workers run the supplied parse, and a bounded
/// channel yields records with backpressure both ways. Record order is NOT
/// preserved; callers whose downstream reads order have no business here.
/// Decomposers must not hand-roll this shape —
/// DecomposerArchitectureGateTests.DecomposerProjects_NoHandRolledParallelIngest
/// bans Channel.CreateBounded in decomposer projects; this helper is the
/// sanctioned home. Lives under <c>Laplace.Substrate/Abstractions/</c> (spine
/// ownership) while keeping namespace <c>Laplace.Decomposers.Abstractions</c>
/// so decomposer call sites stay on one import surface (Copilot #895).
/// </summary>
public static class ParallelLineParse
{
    public static async IAsyncEnumerable<T> RecordsAsync<T>(
        string filePath,
        Func<byte[], T?> parseLine,
        int workerCount,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class
    {
        ct.ThrowIfCancellationRequested();
        int workers = Math.Max(1, workerCount);
        var rawLines = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(workers)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var parsed = Channel.CreateBounded<T>(new BoundedChannelOptions(workers)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = linked.Token;
        ExceptionDispatchInfo? failure = null;
        void Failed(Exception error)
        {
            if (error is OperationCanceledException)
            {
                if (runCt.IsCancellationRequested) return;
                error = new InvalidOperationException("Line parser cancelled without caller cancellation.", error);
            }
            if (Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null) is null)
            {
                parsed.Writer.TryComplete(error);
                linked.Cancel();
            }
            else if (error is not ChannelClosedException
                && !ReferenceEquals(error, Volatile.Read(ref failure)?.SourceException))
                System.Diagnostics.Trace.TraceWarning("Additional line parser failure for {0}: {1}", filePath, error);
        }

        var feeder = Task.Run(async () =>
        {
            try
            {
                await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(filePath, runCt))
                {
                    if (lineMem.IsEmpty) continue;
                    await rawLines.Writer.WriteAsync(lineMem.ToArray(), runCt);
                }
            }
            catch (Exception ex) { Failed(ex); }
            finally { rawLines.Writer.TryComplete(); }
        }, CancellationToken.None);

        var workerTasks = new Task[workers];
        int workersLeft = workers;
        for (int w = 0; w < workers; w++)
        {
            workerTasks[w] = Task.Run(async () =>
            {
                try
                {
                    var reader = rawLines.Reader;
                    while (await reader.WaitToReadAsync(runCt))
                    {
                        while (reader.TryRead(out byte[]? lineUtf8))
                        {
                            if (lineUtf8 is null || lineUtf8.Length == 0) continue;
                            if (parseLine(lineUtf8) is { } record)
                                await parsed.Writer.WriteAsync(record, runCt);
                        }
                    }
                }
                catch (Exception ex) { Failed(ex); }
                finally
                {
                    if (Interlocked.Decrement(ref workersLeft) == 0)
                        parsed.Writer.TryComplete(Volatile.Read(ref failure)?.SourceException);
                }
            }, CancellationToken.None);
        }

        try
        {
            // Internal sibling cancellation must not replace the original failure.
            await foreach (var record in parsed.Reader.ReadAllAsync(ct))
            {
                Volatile.Read(ref failure)?.Throw();
                yield return record;
            }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // The cancellation source outlives every worker using it, including when
            // the consumer stops early. Observe all owned tasks before returning.
            linked.Cancel();
            await feeder.ConfigureAwait(false);
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            Volatile.Read(ref failure)?.Throw();
        }
    }
}
