namespace Laplace.SubstrateCRUD;

/// <summary>
/// In-process dependency barrier between decomposition and persistence. A producer emits a
/// zero-row barrier change and waits; the shared runner flushes every previously produced
/// working set, then releases the barrier. No source may implement this with direct writer
/// calls or polling.
/// </summary>
public sealed class IngestApplyBarrier
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken ct = default) =>
        _completion.Task.WaitAsync(ct);

    internal void Complete() => _completion.TrySetResult();

    internal void Fail(Exception error) => _completion.TrySetException(error);
}
