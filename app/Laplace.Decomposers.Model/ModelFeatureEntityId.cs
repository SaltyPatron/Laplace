using Laplace.Engine.Core;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Deterministic entity ids for <c>Model_Feature</c> axis indices (ADR 0056 interior matchup space).
/// Must match ingest (<see cref="LlamaWeightExtractor"/>) and synthesis fill paths.
/// </summary>
public static class ModelFeatureEntityId
{
    public static Hash128 For(string axis, int dim) =>
        Hash128.OfCanonical($"substrate/feature/{axis}/{dim}/v1");

    /// <summary>Maps feature entity id → column index for a given axis.</summary>
    public static Dictionary<Hash128, int> BuildIndex(string axis, int count)
    {
        var map = new Dictionary<Hash128, int>(count);
        for (int d = 0; d < count; d++)
            map[For(axis, d)] = d;
        return map;
    }
}
