namespace Laplace.Core;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over the native HilbertCurveService. Phase 2 / Track D / D2.
/// </summary>
public sealed class HilbertCurve : IHilbertCurve
{
    public ulong Index(Point4D point)
    {
        var native = new NativeS3.Point4D
        {
            X = point.X, Y = point.Y, Z = point.Z, W = point.W,
        };
        return NativeHilbert.PointToIndex(in native);
    }

    public Point4D Decode(ulong index)
    {
        NativeHilbert.IndexToPoint(index, out var p);
        return new Point4D(p.X, p.Y, p.Z, p.W);
    }
}
