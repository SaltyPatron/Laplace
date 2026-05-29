using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

/// <summary>
/// Cross-language parity for the exact E·|W| per-token projection kernel
/// (<see cref="NativeInterop.ComputeProjectionPerToken"/>). An independent C#
/// re-implementation of the identical Neumaier-compensated, fixed-order algorithm must
/// produce bit-identical doubles to the native engine kernel — for both the projection
/// case (in_dim == d_model) and the uniform fallback (in_dim != d_model).
/// </summary>
public class ProjectionPerTokenParityTests
{
    private static float Bf16(ushort b) => BitConverter.UInt32BitsToSingle((uint)b << 16);

    private static void Neumaier(ref double sum, ref double c, double term)
    {
        double t = sum + term;
        if (Math.Abs(sum) >= Math.Abs(term)) c += (sum - t) + term;
        else                                 c += (term - t) + sum;
        sum = t;
    }

    /// <summary>Independent managed reference — identical to the C++ kernel + to
    /// WeightTensorETL.AggregateLayerThroughEmbed.</summary>
    private static double[] Reference(ushort[] e, int vocab, int dModel,
                                      ushort[] w, int outDim, int inDim)
    {
        var perInDim = new double[inDim];
        for (int i = 0; i < inDim; i++)
        {
            double s = 0, c = 0;
            for (int o = 0; o < outDim; o++) Neumaier(ref s, ref c, Math.Abs((double)Bf16(w[o * inDim + i])));
            perInDim[i] = s + c;
        }
        var outv = new double[vocab];
        if (inDim == dModel)
        {
            for (int t = 0; t < vocab; t++)
            {
                double s = 0, c = 0;
                for (int i = 0; i < inDim; i++) Neumaier(ref s, ref c, (double)Bf16(e[t * dModel + i]) * perInDim[i]);
                outv[t] = Math.Abs(s + c);
            }
        }
        else
        {
            double s = 0, c = 0;
            for (int i = 0; i < inDim; i++) Neumaier(ref s, ref c, perInDim[i]);
            double per = (s + c) / vocab;
            for (int t = 0; t < vocab; t++) outv[t] = per;
        }
        return outv;
    }

    private static ushort[] RandBf16(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            ushort b;
            do { b = (ushort)rng.Next(0, 65536); } while (((b >> 7) & 0xFF) == 0xFF); // no NaN/Inf
            a[i] = b;
        }
        return a;
    }

    [Theory]
    [InlineData(131, 96, 257, 96)]   // projection case: in_dim == d_model
    [InlineData(131, 96, 257, 211)]  // fallback case:  in_dim != d_model
    public unsafe void MatchesManagedReference_Bitwise(int vocab, int dModel, int outDim, int inDim)
    {
        var e = RandBf16(vocab * dModel, 7);
        var w = RandBf16(outDim * inDim, 99);
        var got = new double[vocab];
        fixed (ushort* ep = e)
        fixed (ushort* wp = w)
        fixed (double* op = got)
            Assert.Equal(0, NativeInterop.ComputeProjectionPerToken(
                ep, (nuint)vocab, (nuint)dModel, wp, (nuint)outDim, (nuint)inDim, op));

        var exp = Reference(e, vocab, dModel, w, outDim, inDim);
        for (int t = 0; t < vocab; t++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(exp[t]), BitConverter.DoubleToInt64Bits(got[t]));
    }
}
