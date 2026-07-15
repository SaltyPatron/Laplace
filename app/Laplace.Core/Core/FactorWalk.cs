using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;




public static class FactorWalk
{
    public const int ValuesPerVertex = 6;

    public static int VertexCount(int valueCount)
        => (valueCount + ValuesPerVertex - 1) / ValuesPerVertex;


    public static unsafe byte[] Pack(ReadOnlySpan<float> values)
    {
        if (values.Length == 0) throw new ArgumentException("empty factor payload");

        var bytes = new byte[VertexCount(values.Length) * 4 * sizeof(double)];
        fixed (float* pv = values)
        fixed (byte* pb = bytes)
        {
            nuint vertices = 0;
            int rc = NativeInterop.LaplaceFactorPackValues(
                pv, (nuint)values.Length, (double*)pb, &vertices);
            if (rc != 0)
                throw new InvalidOperationException($"factor pack failed: {rc}");
        }
        return bytes;
    }


    public static unsafe float[] Unpack(ReadOnlySpan<double> xyzm)
    {
        if (xyzm.Length == 0 || xyzm.Length % 4 != 0)
            throw new ArgumentException("xyzm length must be a positive multiple of 4");
        int nVertices = xyzm.Length / 4;

        var values = new float[nVertices * ValuesPerVertex];
        int total = 0;
        fixed (double* pv = xyzm)
        fixed (float* po = values)
        {
            for (int v = 0; v < nVertices; v++)
            {
                byte count = 0;
                int rc = NativeInterop.LaplaceFactorUnpackVertex(pv + v * 4, po + total, &count);
                if (rc != 0)
                    throw new InvalidOperationException($"factor unpack failed at vertex {v}: {rc}");
                total += count;
            }
        }
        if (total == values.Length) return values;
        var trimmed = new float[total];
        Array.Copy(values, trimmed, total);
        return trimmed;
    }
}
