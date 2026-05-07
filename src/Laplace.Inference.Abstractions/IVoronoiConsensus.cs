namespace Laplace.Inference.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Compute Voronoi consensus cells over per-substrate-entity firefly clouds.
/// One firefly per ingested model that has the entity in its vocab; cell over
/// those positions = consensus area where models collectively agree the
/// entity lives in S³. Tight = strong agreement; fragmented = ambiguity;
/// empty = no model has an opinion (frayed-edge signal).
/// </summary>
public interface IVoronoiConsensus
{
    Task<VoronoiCell> CellForAsync(AtomId substrateEntity, CancellationToken cancellationToken);
}

public record VoronoiCell(
    AtomId SubstrateEntity,
    int FireflyCount,
    Point4D[] CellVertices,
    double CellVolume,
    double HausdorffDiameter);
