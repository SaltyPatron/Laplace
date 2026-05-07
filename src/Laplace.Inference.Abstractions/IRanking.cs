namespace Laplace.Inference.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Multi-criteria ranker for traversal results: 4D Euclidean / geodesic
/// distance + Glicko-2 rating-profile correlation + Fréchet shape similarity
/// + Voronoi consensus tightness + provenance trust filter. Caller specifies
/// the per-criterion weights for the ranking blend.
/// </summary>
public interface IRanking
{
    Task<IReadOnlyList<TraversalPath>> RankAsync(
        IReadOnlyList<TraversalPath> candidates,
        RankingCriteria criteria,
        CancellationToken cancellationToken);
}

public record RankingCriteria(
    double DistanceWeight,
    double GlickoProfileWeight,
    double FrechetShapeWeight,
    double VoronoiTightnessWeight,
    double ProvenanceTrustWeight,
    AtomId? ContextEntity);
