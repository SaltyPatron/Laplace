using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

/// <summary>
/// A test that drains a decomposer without a writer must release each apply barrier
/// itself: ArtifactDecomposerMultiPhase waits on one between dependency levels, and no
/// writer is there to complete it.
/// </summary>
internal static class WriterlessDrain
{
    public static async IAsyncEnumerable<SubstrateChange> WithoutWriter(
        this IAsyncEnumerable<SubstrateChange> changes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (SubstrateChange change in changes.WithCancellation(ct))
        {
            change.ApplyBarrier?.Complete();
            yield return change;
        }
    }
}
