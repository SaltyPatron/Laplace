using Laplace.Engine.Core;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;





public static class WeightTensorETL
{

    public static byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name)
    {
        var tref = refMap[name];
        byte[] rawBytes = new byte[tref.DataLength];
        using var fs = new FileStream(tref.FilePath, FileMode.Open, FileAccess.Read,
                                      FileShare.Read, 1 << 16, useAsync: false);
        fs.Seek(tref.AbsoluteDataStart, SeekOrigin.Begin);
        int total = 0;
        while (total < rawBytes.Length)
        {
            int n = fs.Read(rawBytes, total, rawBytes.Length - total);
            if (n == 0) throw new IOException($"safetensors: truncated data for {name}");
            total += n;
        }
        return rawBytes;
    }

    public static float[] LoadTensorF32(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap,
        string name, long expectedElements)
    {
        var tref = refMap[name];
        byte[] raw = LoadRawBytes(refMap, name);



        long bytesPer = BytesPerElement(tref.Dtype);
        if (expectedElements < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedElements), "negative element count");
        if (bytesPer > 0 && expectedElements > raw.LongLength / bytesPer)
            throw new InvalidDataException(
                $"safetensors: tensor '{name}' dtype {tref.Dtype} holds {raw.LongLength} bytes, too few " +
                $"for {expectedElements} elements x {bytesPer}B — config.json shape disagrees with the " +
                "tensor; refusing to read past the buffer.");
        float[] result = new float[expectedElements];
        long n = expectedElements;
        unsafe
        {
            fixed (byte* rp = raw)
            fixed (float* op = result)
            {
                switch (tref.Dtype)
                {
                    case "F32": { float* s = (float*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "F64": { double* s = (double*)rp; for (long i = 0; i < n; i++) op[i] = (float)s[i]; break; }
                    case "F16": { ushort* s = (ushort*)rp; for (long i = 0; i < n; i++) op[i] = (float)BitConverter.UInt16BitsToHalf(s[i]); break; }
                    case "BF16":
                        {
                            var tmp = GC.AllocateUninitializedArray<double>((int)n);
                            fixed (double* td = tmp)
                            {
                                int rc = SynInterop.Bf16Decode(rp, (nuint)n, td);
                                if (rc != 0)
                                    throw new InvalidOperationException($"laplace_bf16_decode returned {rc}");
                                for (long i = 0; i < n; i++) op[i] = (float)tmp[i];
                            }
                            break;
                        }
                    case "F8_E5M2": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE5M2(s[i]); break; }
                    case "F8_E4M3": { byte* s = rp; for (long i = 0; i < n; i++) op[i] = DecodeE4M3(s[i]); break; }
                    case "I64": { long* s = (long*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I32": { int* s = (int*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I16": { short* s = (short*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "I8": { sbyte* s = (sbyte*)rp; for (long i = 0; i < n; i++) op[i] = s[i]; break; }
                    case "U8": for (long i = 0; i < n; i++) op[i] = rp[i]; break;
                    case "BOOL": for (long i = 0; i < n; i++) op[i] = rp[i] != 0 ? 1f : 0f; break;
                    default:
                        throw new NotSupportedException(
                            $"tensor '{name}' dtype '{tref.Dtype}' has no decoder. safetensors numeric/bool " +
                            "are covered; GGUF block-quant (Q4_K/Q6_K/…) is a separate container needing its " +
                            "own dequantizer. Refusing to ingest zeros.");
                }
            }
        }
        return result;
    }



    internal static long BytesPerElement(string dtype) => dtype switch
    {
        "F64" or "I64" => 8,
        "F32" or "I32" => 4,
        "F16" or "BF16" or "I16" => 2,
        "F8_E5M2" or "F8_E4M3" or "I8" or "U8" or "BOOL" => 1,
        _ => 0,
    };

    private static float DecodeE5M2(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 2) & 0x1F, mant = b & 0x3;
        ushort half = (ushort)((sign << 15) | (exp << 10) | (mant << 8));
        return (float)BitConverter.UInt16BitsToHalf(half);
    }

    private static float DecodeE4M3(byte b)
    {
        int sign = (b >> 7) & 1, exp = (b >> 3) & 0xF, mant = b & 0x7;
        float v;
        if (exp == 0) v = mant * MathF.ScaleB(1f, -9);
        else if (exp == 15 && mant == 7) v = float.NaN;
        else v = (1f + mant * 0.125f) * MathF.ScaleB(1f, exp - 7);
        return sign != 0 ? -v : v;
    }
}
