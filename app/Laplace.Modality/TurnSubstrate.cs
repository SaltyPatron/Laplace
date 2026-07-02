using Laplace.Engine.Core;

namespace Laplace.Modality;

public readonly record struct RecordedEdge(
    string SubjectKey,
    string ObjectKey,
    string? MoveKey,
    PlyOutcome MoverOutcome);

public interface IContentAddresser
{
    Hash128 Address(string canonicalSurface);
}

public static class ConsensusKeys
{
    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
        => Laplace.Engine.Core.ConsensusKeys.EdgeId(subject, type, obj);

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128? obj)
        => Laplace.Engine.Core.ConsensusKeys.EdgeId(subject, type, obj);
}

public interface IEdgeRatings
{
    Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default);
}

public interface IStateValuer
{
    Task<double[]> ValueStatesAsync(IReadOnlyList<string> stateSurfaces, CancellationToken ct = default);
}

public interface ITurnLearner
{
    Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default);
}

public static class GlickoPriors
{
    public const double NeutralMu = 1_500_000_000_000d;
    public const double InitialRd = 350_000_000_000d;
    public const double UnratedEffMu = NeutralMu - 2d * InitialRd;
}
