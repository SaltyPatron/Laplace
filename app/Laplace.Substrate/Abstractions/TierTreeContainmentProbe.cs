using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class TierTreeContainmentProbe
{
    public static async Task<byte[]?> ProbeNodeEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct = default)
    {
        var results = await ProbeBatchNodeEmitBitmapsAsync([tree], reader, ct).ConfigureAwait(false);
        return results[0];
    }

    public static Task<byte[]?[]> ProbeBatchNodeEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default)
        => TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, ct);

    public static Task<byte[]?[]> ProbeBatchNodeEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader,
        ISet<Hash128>? probedAbsent, CancellationToken ct = default)
        => TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, probedAbsent, ct);
}
