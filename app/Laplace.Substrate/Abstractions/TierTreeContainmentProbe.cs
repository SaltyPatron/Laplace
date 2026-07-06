using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Thin alias — all tier existence orchestration lives in <see cref="ContentTierSpine"/>.</summary>
public static class TierTreeContainmentProbe
{
    public static Task<byte[]?> ProbeNodeEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct = default) =>
        ContentTierSpine.ExistenceEmitBitmapAsync(tree, reader, ct);

    public static Task<byte[]?[]> ProbeBatchNodeEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default) =>
        ContentTierSpine.BatchExistenceEmitBitmapsAsync(trees, reader, ct);

    public static Task<byte[]?[]> ProbeBatchNodeEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader,
        ISet<Hash128>? probedAbsent, CancellationToken ct = default) =>
        ContentTierSpine.BatchExistenceEmitBitmapsAsync(trees, reader, probedAbsent, ct);
}
