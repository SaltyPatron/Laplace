namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// P/Invoke surface for the native <c>Voronoi4DService</c> (CGAL backend).
/// 4D Voronoi tessellation primitives. Used by <c>IVoronoiConsensus</c> to
/// compute consensus cells over per-token firefly clouds across N ingested
/// models.
/// </summary>
public interface IVoronoi4D
{
    /// <summary>Compute the Voronoi cell vertices for a single seed point given a set of neighbor sites.</summary>
    Point4D[] CellFor(Point4D seed, ReadOnlySpan<Point4D> neighbors);

    /// <summary>Volume of the Voronoi cell (4-volume in R^4) — tight = high consensus.</summary>
    double CellVolume(ReadOnlySpan<Point4D> cellVertices);
}
