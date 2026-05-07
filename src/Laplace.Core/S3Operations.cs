namespace Laplace.Core;

using System;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native <c>S3DomainService</c> + <c>QuaternionService</c>.
/// Phase 2 / Track D / D2.
/// </summary>
public sealed class S3Operations : IS3Operations
{
    public bool IsOnS3(Point4D point, double tolerance = 1e-9)
    {
        var p = ToNative(point);
        return NativeS3.IsOnSphere(p, tolerance) != 0;
    }

    public double GeodesicDistance(Point4D a, Point4D b)
    {
        var na = ToNative(a);
        var nb = ToNative(b);
        return NativeS3.GeodesicDistance(na, nb);
    }

    public Point4D Slerp(Point4D a, Point4D b, double t)
    {
        var na = ToNative(a);
        var nb = ToNative(b);
        NativeS3.Slerp(na, nb, t, out var result);
        return ToManaged(result);
    }

    public Point4D QuaternionMultiply(Point4D a, Point4D b)
    {
        var na = ToNative(a);
        var nb = ToNative(b);
        NativeQuaternion.Multiply(na, nb, out var result);
        return ToManaged(result);
    }

    public Point4D QuaternionConjugate(Point4D q)
    {
        var nq = ToNative(q);
        NativeQuaternion.Conjugate(nq, out var result);
        return ToManaged(result);
    }

    public Point4D EigenvalueCentroid(ReadOnlySpan<Point4D> quaternions)
    {
        var n = quaternions.Length;
        Span<NativeS3.Point4D> buf = n <= 64
            ? stackalloc NativeS3.Point4D[n]
            : new NativeS3.Point4D[n];
        for (int i = 0; i < n; ++i)
        {
            buf[i] = ToNative(quaternions[i]);
        }
        NativeS3.Point4D result;
        unsafe
        {
            fixed (NativeS3.Point4D* p = buf)
            {
                NativeS3.EigenvalueCentroid(p, null, (nuint)n, out result);
            }
        }
        return ToManaged(result);
    }

    public Point4D NormalizeToS3(Point4D point)
    {
        var p = ToNative(point);
        NativeS3.Normalize(p, out var result);
        return ToManaged(result);
    }

    private static NativeS3.Point4D ToNative(Point4D p) => new()
    {
        X = p.X, Y = p.Y, Z = p.Z, W = p.W,
    };

    private static Point4D ToManaged(NativeS3.Point4D p) => new(p.X, p.Y, p.Z, p.W);
}
