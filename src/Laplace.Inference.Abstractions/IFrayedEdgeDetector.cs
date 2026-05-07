namespace Laplace.Inference.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Mendeleev's "predict missing elements" generalized. For each known edge
/// type T with archetype geometry, find pairs of substrate entities (A, B)
/// whose 4D centroids are within Fréchet threshold of T's archetype but no
/// T-typed edge exists between them. Surfaces gaps in the substrate's
/// knowledge that the geometry says should be filled.
///
/// Frayed-edge signals also flow into the Gödel Engine's macro-OODA as
/// triggers for hypothesis-driven exploration and source-ingestion proposals.
/// </summary>
public interface IFrayedEdgeDetector
{
    Task<IReadOnlyList<FrayedEdgeCandidate>> DetectAsync(
        AtomId edgeTypeHash,
        double frechetThreshold,
        int maxResults,
        CancellationToken cancellationToken);
}

public record FrayedEdgeCandidate(
    AtomId SourceEntity,
    AtomId TargetEntity,
    double FrechetDistanceFromArchetype);
