using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

        var feeder = Task.Run(async () =>
        {
            try
            {
                await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(filePath, runCt))
                {
                    if (lineMem.IsEmpty) continue;
                    await rawLines.Writer.WriteAsync(lineMem.ToArray(), runCt);
                }
                rawLines.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                rawLines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // An IO fault must ride the channel chain: completing rawLines WITH the
                // fault makes every worker's WaitToReadAsync throw it, which completes
                // `parsed` faulted and fails the consumer. Completing clean here (the
                // old `finally`) made a truncated or unreadable file look like a
                // successful ingest of its readable prefix.
                rawLines.Writer.TryComplete(ex);
            }
        }, runCt);

        var errors = new ConcurrentQueue<Exception>();
        int workersLeft = workers;
        for (int w = 0; w < workers; w++)
        {
            _ = Task.Run(async () =>
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
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    linked.Cancel();
                }
                finally
                {
                    if (Interlocked.Decrement(ref workersLeft) == 0)
                        parsed.Writer.TryComplete(errors.TryPeek(out var first) ? first : null);
                }
            }, runCt);
        }

        try
        {
            await foreach (var record in parsed.Reader.ReadAllAsync(runCt))
                yield return record;
        }
        finally
        {
            // A consumer that stops enumerating early leaves the feeder blocked on a
            // full channel no one drains; awaiting it without cancelling first is a
            // deadlock. Cancel unblocks feeder and workers; faults on the normal path
            // have already propagated through the channel completions above.
            linked.Cancel();
            try { await feeder; } catch { }
        }
    }
}
