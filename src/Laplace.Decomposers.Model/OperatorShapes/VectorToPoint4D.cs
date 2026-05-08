namespace Laplace.Decomposers.Model.OperatorShapes;

using System;

using Laplace.Core.Abstractions;

/// <summary>
/// Project a vector-shaped tensor (bias, layer norm scale/shift, RMSNorm
/// weight, etc.) to a POINT4D for storage in the model_weights_4d
/// physicality partition. Vectors carry per-output-feature additive shifts
/// or scale factors — they're typically near-zero or near-one and contribute
/// less circuit information than W matrices, but they MUST be preserved
/// for deterministic re-export (Phase 6 cell formula sees their content).
///
/// Projection: deterministic 4-stripe sum + S³ normalization. The N-element
/// vector is partitioned into 4 stripes (every 4th element starting at
/// offset 0/1/2/3); each stripe's mean becomes one coordinate (X, Y, Z, W).
/// The resulting (X, Y, Z, W) is then normalized to S³. Pure function of
/// content; identical vectors always produce identical positions.
///
/// Phase 4 / Track F5 / supports model_weights_4d emission per #38
/// per-tensor extractors.
/// </summary>
public static class VectorToPoint4D
{
    public static Point4D Project(ReadOnlySpan<double> vector)
    {
        if (vector.IsEmpty)
        {
            return new Point4D(0d, 0d, 0d, 1d);
        }

        var sumX = 0d; var countX = 0;
        var sumY = 0d; var countY = 0;
        var sumZ = 0d; var countZ = 0;
        var sumW = 0d; var countW = 0;

        for (var i = 0; i < vector.Length; i++)
        {
            switch (i & 3)
            {
                case 0: sumX += vector[i]; countX++; break;
                case 1: sumY += vector[i]; countY++; break;
                case 2: sumZ += vector[i]; countZ++; break;
                case 3: sumW += vector[i]; countW++; break;
            }
        }

        var x = countX > 0 ? sumX / countX : 0d;
        var y = countY > 0 ? sumY / countY : 0d;
        var z = countZ > 0 ? sumZ / countZ : 0d;
        var w = countW > 0 ? sumW / countW : 0d;

        var raw  = new Point4D(x, y, z, w);
        var norm = raw.Norm;
        if (norm <= 1e-15)
        {
            return new Point4D(0d, 0d, 0d, 1d);
        }
        return raw.Normalized();
    }
}
