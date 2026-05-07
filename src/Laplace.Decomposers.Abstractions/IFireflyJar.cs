namespace Laplace.Decomposers.Abstractions;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Per-substrate-entity collection of firefly positions across N ingested
/// models. Each (substrate token entity × model) pair contributes one
/// firefly Point4D in S³. The jar is backed by the <c>firefly_s3_extracted</c>
/// physicality partition (separate from the substrate atom partition).
///
/// Voronoi consensus over the jar emerges from cumulative model ingestion:
/// tight cells = strong cross-model agreement on where the entity lives in
/// S³; fragmented cells = disagreement; empty = no model has an opinion
/// (frayed-edge signal).
/// </summary>
public interface IFireflyJar
{
    /// <summary>Store one firefly position for a (substrate entity × model) pair.</summary>
    Task StoreAsync(AtomId substrateEntity, AtomId modelEntity, Point4D position, CancellationToken cancellationToken);

    /// <summary>Retrieve all fireflies (one per ingested model that contributed) for a substrate entity.</summary>
    Task<IReadOnlyList<(AtomId Model, Point4D Position)>> GetForAsync(AtomId substrateEntity, CancellationToken cancellationToken);
}
