namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Completion adapter for the inline consensus writer.
///
/// ConsensusAccumulatingWriter no longer owns deferred fold work: each accepted
/// working set completes its consensus fold before ApplyAsync returns. Older
/// concrete call sites can therefore await the historical drain boundary without
/// reintroducing a queue, background worker, or terminal fold pass.
/// </summary>
public static class ConsensusAccumulatingWriterCompletionExtensions
{
    public static Task DrainFoldsAsync(this ConsensusAccumulatingWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return Task.CompletedTask;
    }
}
