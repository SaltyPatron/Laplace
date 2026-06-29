using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Shared O(tier) Merkle descent probe for any tier tree (grammar compose, content witness, etc.).
/// </summary>
public static class TierTreeContainmentProbe
{
    public static async Task<byte[]?> ProbeNodeEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct = default)
    {
        var results = await ProbeBatchNodeEmitBitmapsAsync([tree], reader, ct).ConfigureAwait(false);
        return results[0];
    }

    /// <summary>
    /// One <c>content_descent_bitmap</c> (+ optional tier01 flat) round-trip for many trees.
    /// </summary>
    public static Task<byte[]?[]> ProbeBatchNodeEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default)
        => TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, ct);
}
