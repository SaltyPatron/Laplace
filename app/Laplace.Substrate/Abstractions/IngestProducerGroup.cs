using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Owns the producers, cancellation and unconsumed leases of one ingest stream.</summary>
internal sealed class IngestProducerGroup(CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly List<Task> _tasks = [];
    private Exception? _failure;

    internal CancellationToken Token => _stop.Token;

    internal void Run(Func<Task> producer)
    {
        // Always enter the delegate, even if cancellation raced scheduling: its
        // finally blocks own channel completion and source-reader disposal.
        _tasks.Add(Task.Run(async () =>
        {
            try { await producer().ConfigureAwait(false); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _failure, error, null);
                await StopAsync().ConfigureAwait(false);
            }
        }));
    }

    internal static async ValueTask WriteAsync(
        ChannelWriter<SubstrateChange> writer, SubstrateChange change, CancellationToken ct)
    {
        try { await writer.WriteAsync(change, ct).ConfigureAwait(false); }
        catch
        {
            // Ownership transfers only when the bounded channel accepts the change.
            change.ApplyEnvelope?.Dispose();
            throw;
        }
    }

    internal async IAsyncEnumerable<SubstrateChange> ConsumeAsync(
        Channel<SubstrateChange> output,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Task completion = CompleteAsync(output.Writer);
        try
        {
            // Use the consumer token here. A failed producer cancels its siblings,
            // but the consumer must receive that original error, not sibling cancellation.
            await foreach (var change in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return change;
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
            try { await completion.ConfigureAwait(false); }
            finally
            {
                while (output.Reader.TryRead(out var abandoned))
                    abandoned.ApplyEnvelope?.Dispose();
            }
        }
    }

    private async Task StopAsync()
    {
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            // Cancellation callbacks belong to producers too. Their failures
            // must not bypass joining the tasks or draining unconsumed leases.
            Interlocked.CompareExchange(ref _failure, error, null);
        }
    }

    private async Task CompleteAsync(ChannelWriter<SubstrateChange> output)
    {
        try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
        finally { output.TryComplete(_failure); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        finally { _stop.Dispose(); }
    }
}
