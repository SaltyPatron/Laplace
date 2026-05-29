using Xunit;
using Laplace.Engine.Synthesis;

namespace Laplace.Engine.Synthesis.Tests;

/// <summary>
/// Cross-language parity for the exact per-token L2 magnitude kernel
/// (<see cref="NativeInterop.ComputePerTokenL2Magnitude"/>). Redundancy check per the
/// build methodology: an independent C# re-implementation of the identical
/// Neumaier-compensated, fixed-column-order algorithm must produce **bit-identical**
/// doubles to the native engine kernel. If the two ever diverge, one side regressed.
/// </summary>
public class PerTokenMagnitudeParityTests
{
    /// <summary>Independent managed reference — same decode + same Neumaier compensated
    /// sum + same fixed column order as the C++ kernel.</summary>
    private static double RowL2(ushort[] tensor, int r, int cols)
    {
        double sum = 0.0, c = 0.0;
        for (int j = 0; j < cols; j++)
        {
            uint u = (uint)tensor[r * cols + j] << 16;
            double v = BitConverter.UInt32BitsToSingle(u);
            double term = v * v;
            double t = sum + term;
            if (Math.Abs(sum) >= Math.Abs(term)) c += (sum - t) + term;
            else                                 c += (term - t) + sum;
            sum = t;
        }
        return Math.Sqrt(sum + c);
    }

    [Fact]
    public unsafe void MatchesManagedReference_Bitwise()
    {
        const int rows = 257, cols = 193;   // deliberately non-grain-aligned
        var rng = new Random(12345);
        var tensor = new ushort[rows * cols];
        for (int i = 0; i < tensor.Length; i++)
        {
            ushort b;
            // exclude exponent-all-ones (NaN/Inf) so bitwise equality is well-defined
            do { b = (ushort)rng.Next(0, 65536); } while (((b >> 7) & 0xFF) == 0xFF);
            tensor[i] = b;
        }

        var got = new double[rows];
        fixed (ushort* tp = tensor)
        fixed (double* op = got)
            Assert.Equal(0, NativeInterop.ComputePerTokenL2Magnitude(tp, (nuint)rows, (nuint)cols, op));

        for (int r = 0; r < rows; r++)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(RowL2(tensor, r, cols)),
                BitConverter.DoubleToInt64Bits(got[r]));
    }

    [Fact]
    public unsafe void KnownValues()
    {
        // bf16: 1.0=0x3F80 2.0=0x4000 3.0=0x4040 4.0=0x4080 0.0=0x0000
        // row0 [1,2,2] → √9 = 3 ; row1 [3,0,4] → √25 = 5
        ushort[] tensor = { 0x3F80, 0x4000, 0x4000, 0x4040, 0x0000, 0x4080 };
        var got = new double[2];
        fixed (ushort* tp = tensor)
        fixed (double* op = got)
            Assert.Equal(0, NativeInterop.ComputePerTokenL2Magnitude(tp, 2, 3, op));
        Assert.Equal(3.0, got[0]);
        Assert.Equal(5.0, got[1]);
    }
}
