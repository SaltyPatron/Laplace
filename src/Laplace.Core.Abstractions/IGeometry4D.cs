namespace Laplace.Core.Abstractions;

using System;

/// <summary>
/// P/Invoke surface for the native <c>Geometry4DService</c> +
/// <c>Geometry4DOperatorsService</c>. The substrate's GEOMETRY4D type family
/// is an INDEPENDENT custom PostgreSQL type — separate type OIDs, separate
/// WKB-equivalent serialization, separate ST_4D_* operator surface — NOT
/// PostGIS GEOMETRYZM with M repurposed. Existing PostGIS infrastructure
/// remains additively available for naturally low-dim modalities.
/// </summary>
public interface IGeometry4D
{
    /// <summary>4D Euclidean distance between two POINT4D entities.</summary>
    double Distance(Point4D a, Point4D b);

    /// <summary>Vertex-mean centroid (NOT length-weighted) — what compositions use.</summary>
    Point4D VertexCentroid(ReadOnlySpan<Point4D> vertices);

    /// <summary>Length-weighted centroid (OGC-compatible) — for analytic queries.</summary>
    Point4D LengthWeightedCentroid(ReadOnlySpan<Point4D> linestringVertices);

    /// <summary>
    /// 4D Fréchet distance between two LINESTRING4D entities. Used for shape-
    /// based similarity at every tier (the "frayed edge detection" primitive
    /// when comparing edge geometries against archetypes).
    /// </summary>
    double FrechetDistance(ReadOnlySpan<Point4D> a, ReadOnlySpan<Point4D> b);

    /// <summary>4D Hausdorff distance between two LINESTRING4D entities.</summary>
    double HausdorffDistance(ReadOnlySpan<Point4D> a, ReadOnlySpan<Point4D> b);

    /// <summary>Arc length of a LINESTRING4D.</summary>
    double Length(ReadOnlySpan<Point4D> linestringVertices);

    /// <summary>
    /// Axis-aligned 4D bounding box for a vertex set. Returned as a packed
    /// (minX, minY, minZ, minW, maxX, maxY, maxZ, maxW) tuple via two Point4Ds.
    /// </summary>
    (Point4D Min, Point4D Max) BoundingBox(ReadOnlySpan<Point4D> vertices);
}
