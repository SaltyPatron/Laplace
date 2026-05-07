namespace Laplace.Inference.Ranking;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Inference.Abstractions;

/// <summary>
/// H2 — multi-criteria ranker for traversal candidates. Applies the per-
/// criterion weights from <see cref="RankingCriteria"/> and returns paths
/// in descending blended-score order.
///
/// At v0.1 the four geometric criteria (distance, geodesic-derived Glicko
/// profile, Fréchet shape, Voronoi tightness) collapse to placeholders
/// because the centroid lookup + IVoronoiConsensus + IGeometry4D wiring
/// gates on env-installed PG with the GEOMETRY4D type registered. The
/// ranker scaffold is structured so each criterion plugs in independently
/// when its dependency lands.
///
/// Phase 6 / Track H2.
/// </summary>
public sealed class Ranking : IRanking
{
    public Task<IReadOnlyList<TraversalPath>> RankAsync(
        IReadOnlyList<TraversalPath> candidates,
        RankingCriteria              criteria,
        CancellationToken            cancellationToken)
    {
        var scored = new List<(TraversalPath Path, double Score)>(candidates.Count);
        foreach (var path in candidates)
        {
            var score = ComputeScore(path, criteria);
            scored.Add((path, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var ranked = new TraversalPath[scored.Count];
        for (int i = 0; i < scored.Count; ++i) { ranked[i] = scored[i].Path; }
        return Task.FromResult<IReadOnlyList<TraversalPath>>(ranked);
    }

    private static double ComputeScore(TraversalPath path, RankingCriteria criteria)
    {
        // Glicko profile component: average mu along the edge chain — high
        // mu means well-attested edges. Inverse cost is roughly proportional
        // to mu so this is partially redundant with the cost score, but the
        // ranker's job is to expose the criterion knob to the caller.
        double avgMu = 0.0;
        if (path.EdgeChain.Count > 0)
        {
            foreach (var seg in path.EdgeChain) { avgMu += seg.Mu; }
            avgMu /= path.EdgeChain.Count;
        }

        // Distance component: shorter (lower TotalCost) is better. Convert
        // cost to a positive score component via 1 / (1 + cost).
        var distanceScore = 1.0 / (1.0 + path.TotalCost);

        var glickoScore = avgMu / 3000.0; // normalize ~[0, 1] for typical Glicko range
        var frechetScore = 0.0;          // gates on IGeometry4D wired against substrate centroids
        var voronoiScore = 0.0;          // gates on IVoronoiConsensus
        var provScore    = 0.0;          // gates on Provenance significance lookups in this hot path

        return criteria.DistanceWeight        * distanceScore
             + criteria.GlickoProfileWeight   * glickoScore
             + criteria.FrechetShapeWeight    * frechetScore
             + criteria.VoronoiTightnessWeight * voronoiScore
             + criteria.ProvenanceTrustWeight * provScore;
    }
}
