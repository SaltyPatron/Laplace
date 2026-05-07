namespace Laplace.Inference.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Glicko-2-cost-weighted A* over typed edges. Edge cost = 1 / μ from
/// significance_edge in the requested context. Edge-batch fetch (one SPI per
/// popped node, NOT per neighbor — the inner-SPI-per-neighbor pattern is the
/// known anti-pattern from prior iterations).
///
/// Wrapped at every scale by <c>IMicroOoda</c> / <c>IMesoOoda</c> /
/// <c>IMacroOoda</c> inside the Gödel Engine (the behavioral engine).
/// </summary>
public interface ITraversal
{
    /// <summary>
    /// Traverse from one or more seed entities, returning ranked paths up to
    /// <paramref name="maxDepth"/> edge hops or until <paramref name="costBudget"/>
    /// is exhausted.
    /// </summary>
    IAsyncEnumerable<TraversalPath> AStarAsync(
        IReadOnlyList<AtomId> seedEntities,
        AtomId contextEntity,
        int maxDepth,
        double costBudget,
        CancellationToken cancellationToken);
}

/// <summary>
/// One ranked traversal result. Carries provenance (the chain of edges +
/// participants) and the accumulated cost (sum of 1/μ along the path). The
/// path itself IS the explanation — there is no separate "explanation"
/// generation step.
/// </summary>
public record TraversalPath(
    IReadOnlyList<AtomId> NodeChain,
    IReadOnlyList<EdgeSegment> EdgeChain,
    double TotalCost);

public record EdgeSegment(
    AtomId EdgeTypeHash,
    AtomId EdgeHash,
    double Mu,
    double SigmaDisp);
