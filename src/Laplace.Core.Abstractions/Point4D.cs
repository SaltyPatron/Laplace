namespace Laplace.Core.Abstractions;

/// <summary>
/// Single 4D point — the substrate's geometric atom. The S³ unit-3-sphere
/// domain is a CHECK-constrained subtype (ensures ‖q‖ = 1). All four
/// coordinates are full IEEE 754 doubles (53-bit mantissa each ⇒ 212 bits of
/// usable precision per point).
/// </summary>
public readonly record struct Point4D(double X, double Y, double Z, double W)
{
    public double NormSquared => X * X + Y * Y + Z * Z + W * W;

    public double Norm => System.Math.Sqrt(NormSquared);

    public Point4D Normalized()
    {
        var n = Norm;
        if (n <= 1e-15)
        {
            return this;
        }
        return new Point4D(X / n, Y / n, Z / n, W / n);
    }
}
