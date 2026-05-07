namespace Laplace.Core.Abstractions;

/// <summary>
/// P/Invoke surface for the native <c>S3DomainService</c> +
/// <c>QuaternionService</c>. The substrate's tier-0 codepoint atoms live on
/// S³ (the unit 3-sphere parameterized as unit quaternions in 4D). This
/// interface exposes the canonical S³ operations.
/// </summary>
public interface IS3Operations
{
    /// <summary>True if the point lies on the unit 3-sphere within numerical tolerance.</summary>
    bool IsOnS3(Point4D point, double tolerance = 1e-9);

    /// <summary>Great-arc geodesic distance between two unit quaternions on S³ (radians).</summary>
    double GeodesicDistance(Point4D a, Point4D b);

    /// <summary>
    /// Spherical linear interpolation between two unit quaternions.
    /// <paramref name="t"/> is in [0, 1].
    /// </summary>
    Point4D Slerp(Point4D a, Point4D b, double t);

    /// <summary>Quaternion product a · b (NOT commutative).</summary>
    Point4D QuaternionMultiply(Point4D a, Point4D b);

    /// <summary>Quaternion conjugate (x, y, z, w) → (-x, -y, -z, w).</summary>
    Point4D QuaternionConjugate(Point4D q);

    /// <summary>
    /// Eigenvalue centroid — Markley's quaternion average that stays on S³
    /// (the rare case where you DO want centroid back on the sphere instead
    /// of in the 4-ball interior).
    /// </summary>
    Point4D EigenvalueCentroid(ReadOnlySpan<Point4D> quaternions);

    /// <summary>Project an arbitrary 4D point onto S³ by normalization.</summary>
    Point4D NormalizeToS3(Point4D point);
}
