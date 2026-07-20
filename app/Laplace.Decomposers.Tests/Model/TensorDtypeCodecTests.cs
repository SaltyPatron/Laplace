using Xunit;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Boundary tests for the native tensor dtype codec (engine/synthesis/src/tensor_dtype_codec.c).
/// The managed decode switch that used to live in WeightTensorETL is gone; these pin the
/// replacement against a managed ORACLE so a drift in the native lane is caught here rather
/// than silently changing what gets attested. The oracle is test-only — production decode is
/// native, per the layer law (C/C++ does the math).
/// </summary>
public sealed class TensorDtypeCodecTests
{
    [Theory]
    [InlineData("F64", 8)]
    [InlineData("F32", 4)]
    [InlineData("F16", 2)]
    [InlineData("BF16", 2)]
    [InlineData("F8_E5M2", 1)]
    [InlineData("F8_E4M3", 1)]
    [InlineData("I64", 8)]
    [InlineData("I32", 4)]
    [InlineData("I16", 2)]
    [InlineData("I8", 1)]
    [InlineData("U8", 1)]
    [InlineData("BOOL", 1)]
    public void DtypeNamesResolveWithTheirElementSize(string name, int size)
    {
        int code = SynInterop.TensorDtypeFromName(name);
        Assert.True(code >= 0, $"{name} should resolve");
        Assert.Equal((nuint)size, SynInterop.TensorDtypeSize(code));
    }

    // Block-quant containers must NOT resolve — ingesting them as zeros would attest garbage.
    [Theory]
    [InlineData("Q4_K")]
    [InlineData("Q6_K")]
    [InlineData("Q8_0")]
    [InlineData("GPTQ")]
    [InlineData("f32")]
    [InlineData("")]
    public void QuantizedAndUnknownDtypesAreRefused(string name)
    {
        Assert.True(SynInterop.TensorDtypeFromName(name) < 0);
    }

    [Fact]
    public unsafe void F16MatchesTheManagedOracleAcrossEveryBitPattern()
    {
        // All 65,536 half bit patterns, so SIMD body and scalar tail are both covered
        // and no encoding corner is left to chance.
        const int n = 65536;
        var raw = new ushort[n];
        for (int i = 0; i < n; i++) raw[i] = (ushort)i;

        var got = new float[n];
        fixed (ushort* rp = raw)
        fixed (float* op = got)
        {
            int rc = SynInterop.TensorDecodeF32(rp, n, SynInterop.TensorDtypeFromName("F16"), op);
            Assert.Equal(0, rc);
        }

        for (int i = 0; i < n; i++)
        {
            float want = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            if (float.IsNaN(want))
            {
                Assert.True(float.IsNaN(got[i]), $"bit pattern 0x{i:X4} should be NaN");
                continue;
            }
            Assert.True(BitConverter.SingleToInt32Bits(want) == BitConverter.SingleToInt32Bits(got[i]),
                $"bit pattern 0x{i:X4}: native {got[i]} != oracle {want}");
        }
    }

    [Fact]
    public unsafe void Bf16IsTheHighHalfOfTheFloat()
    {
        float[] vals = { 0f, 1f, -1f, 3.5f, -2.25f, 1e30f, -1e-30f, 123456f, 7f, -7f, 0.5f };
        var raw = new ushort[vals.Length];
        for (int i = 0; i < vals.Length; i++)
            raw[i] = (ushort)(BitConverter.SingleToUInt32Bits(vals[i]) >> 16);

        var got = new float[vals.Length];
        fixed (ushort* rp = raw)
        fixed (float* op = got)
        {
            Assert.Equal(0, SynInterop.TensorDecodeF32(rp, (nuint)vals.Length,
                SynInterop.TensorDtypeFromName("BF16"), op));
        }

        for (int i = 0; i < vals.Length; i++)
        {
            uint want = BitConverter.SingleToUInt32Bits(vals[i]) & 0xFFFF0000u;
            Assert.Equal(want, BitConverter.SingleToUInt32Bits(got[i]));
        }
    }

    [Fact]
    public unsafe void DecodeIsRepeatableAndLengthIndependent()
    {
        const int n = 37;  // not a multiple of the 8-wide SIMD body
        var raw = new ushort[n];
        for (int i = 0; i < n; i++) raw[i] = (ushort)(0x3C00 + i);

        var full = new float[n];
        var again = new float[n];
        var part = new float[5];
        int f16 = SynInterop.TensorDtypeFromName("F16");
        fixed (ushort* rp = raw)
        {
            fixed (float* op = full) Assert.Equal(0, SynInterop.TensorDecodeF32(rp, n, f16, op));
            fixed (float* op = again) Assert.Equal(0, SynInterop.TensorDecodeF32(rp, n, f16, op));
            fixed (float* op = part) Assert.Equal(0, SynInterop.TensorDecodeF32(rp, 5, f16, op));
        }

        Assert.Equal(full, again);
        Assert.Equal(full.Take(5), part);
    }

    [Fact]
    public unsafe void UnknownDtypeCodeIsRejectedNotZeroFilled()
    {
        var raw = new byte[8];
        var outv = new float[4];
        fixed (byte* rp = raw)
        fixed (float* op = outv)
        {
            Assert.Equal(-2, SynInterop.TensorDecodeF32(rp, 4, -1, op));
        }
    }
}
