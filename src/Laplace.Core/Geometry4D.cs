namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native <c>Geometry4DService</c> +
/// <c>Geometry4DOperatorsService</c>. Phase 2 / Track D / D2.
///
/// LengthWeighted centroid is computed managed-side as a polyline length
/// integral until the native operator surface is fleshed out.
/// </summary>
public sealed class Geometry4D : IGeometry4D
{
    public double Distance(Point4D a, Point4D b)
    {
        var na = ToNative(a);
        var nb = ToNative(b);
        return NativeGeometry4D.Distance(na, nb);
    }

    public Point4D VertexCentroid(ReadOnlySpan<Point4D> vertices)
    {
        var n = vertices.Length;
        Span<NativeS3.Point4D> buf = n <= 128
            ? stackalloc NativeS3.Point4D[n]
            : new NativeS3.Point4D[n];
        for (int i = 0; i < n; ++i)
        {
            buf[i] = ToNative(vertices[i]);
        }
        NativeS3.Point4D outP;
        unsafe
        {
            fixed (NativeS3.Point4D* p = buf)
            {
                NativeGeometry4D.VertexCentroid(p, (nuint)n, out outP);
            }
        }
        return ToManaged(outP);
    }

    public Point4D LengthWeightedCentroid(ReadOnlySpan<Point4D> linestringVertices)
    {
        if (linestringVertices.Length < 2)
        {
            return linestringVertices.Length == 0
                ? new Point4D(0, 0, 0, 0)
                : linestringVertices[0];
        }
        double cx = 0, cy = 0, cz = 0, cw = 0, total = 0;
        for (int i = 1; i < linestringVertices.Length; ++i)
        {
            var a   = linestringVertices[i - 1];
            var b   = linestringVertices[i];
            var len = Distance(a, b);
            var mx  = (a.X + b.X) * 0.5;
            var my  = (a.Y + b.Y) * 0.5;
            var mz  = (a.Z + b.Z) * 0.5;
            var mw  = (a.W + b.W) * 0.5;
            cx += mx * len;
            cy += my * len;
            cz += mz * len;
            cw += mw * len;
            total += len;
        }
        if (total == 0.0)
        {
            return linestringVertices[0];
        }
        var inv = 1.0 / total;
        return new Point4D(cx * inv, cy * inv, cz * inv, cw * inv);
    }

    public double FrechetDistance(ReadOnlySpan<Point4D> a, ReadOnlySpan<Point4D> b)
    {
        var np = a.Length;
        var nq = b.Length;
        Span<NativeS3.Point4D> bufA = np <= 64 ? stackalloc NativeS3.Point4D[np] : new NativeS3.Point4D[np];
        Span<NativeS3.Point4D> bufB = nq <= 64 ? stackalloc NativeS3.Point4D[nq] : new NativeS3.Point4D[nq];
        for (int i = 0; i < np; ++i) { bufA[i] = ToNative(a[i]); }
        for (int i = 0; i < nq; ++i) { bufB[i] = ToNative(b[i]); }
        unsafe
        {
            fixed (NativeS3.Point4D* pa = bufA)
            fixed (NativeS3.Point4D* pb = bufB)
            {
                return NativeGeometry4D.FrechetDistance(pa, (nuint)np, pb, (nuint)nq);
            }
        }
    }

    public double HausdorffDistance(ReadOnlySpan<Point4D> a, ReadOnlySpan<Point4D> b)
    {
        var np = a.Length;
        var nq = b.Length;
        Span<NativeS3.Point4D> bufA = np <= 64 ? stackalloc NativeS3.Point4D[np] : new NativeS3.Point4D[np];
        Span<NativeS3.Point4D> bufB = nq <= 64 ? stackalloc NativeS3.Point4D[nq] : new NativeS3.Point4D[nq];
        for (int i = 0; i < np; ++i) { bufA[i] = ToNative(a[i]); }
        for (int i = 0; i < nq; ++i) { bufB[i] = ToNative(b[i]); }
        unsafe
        {
            fixed (NativeS3.Point4D* pa = bufA)
            fixed (NativeS3.Point4D* pb = bufB)
            {
                return NativeGeometry4D.HausdorffDistance(pa, (nuint)np, pb, (nuint)nq);
            }
        }
    }

    public double Length(ReadOnlySpan<Point4D> linestringVertices)
    {
        if (linestringVertices.Length < 2)
        {
            return 0.0;
        }
        double total = 0.0;
        for (int i = 1; i < linestringVertices.Length; ++i)
        {
            total += Distance(linestringVertices[i - 1], linestringVertices[i]);
        }
        return total;
    }

    public (Point4D Min, Point4D Max) BoundingBox(ReadOnlySpan<Point4D> vertices)
    {
        if (vertices.Length == 0)
        {
            return (new Point4D(0, 0, 0, 0), new Point4D(0, 0, 0, 0));
        }
        double minX = vertices[0].X, minY = vertices[0].Y, minZ = vertices[0].Z, minW = vertices[0].W;
        double maxX = minX, maxY = minY, maxZ = minZ, maxW = minW;
        for (int i = 1; i < vertices.Length; ++i)
        {
            var v = vertices[i];
            if (v.X < minX) { minX = v.X; } else if (v.X > maxX) { maxX = v.X; }
            if (v.Y < minY) { minY = v.Y; } else if (v.Y > maxY) { maxY = v.Y; }
            if (v.Z < minZ) { minZ = v.Z; } else if (v.Z > maxZ) { maxZ = v.Z; }
            if (v.W < minW) { minW = v.W; } else if (v.W > maxW) { maxW = v.W; }
        }
        return (new Point4D(minX, minY, minZ, minW), new Point4D(maxX, maxY, maxZ, maxW));
    }

    private static NativeS3.Point4D ToNative(Point4D p) => new()
    {
        X = p.X, Y = p.Y, Z = p.Z, W = p.W,
    };

    private static Point4D ToManaged(NativeS3.Point4D p) => new(p.X, p.Y, p.Z, p.W);
}
