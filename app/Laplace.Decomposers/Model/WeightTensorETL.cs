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



        int dtype = SynInterop.TensorDtypeFromName(tref.Dtype);
        if (dtype < 0)
            throw new NotSupportedException(
                $"tensor '{name}' dtype '{tref.Dtype}' has no decoder. safetensors numeric/bool " +
                "are covered; GGUF block-quant (Q4_K/Q6_K/…) is a separate container needing its " +
                "own dequantizer. Refusing to ingest zeros.");

        long bytesPer = (long)SynInterop.TensorDtypeSize(dtype);
        if (expectedElements < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedElements), "negative element count");
        if (expectedElements > raw.LongLength / bytesPer)
            throw new InvalidDataException(
                $"safetensors: tensor '{name}' dtype {tref.Dtype} holds {raw.LongLength} bytes, too few " +
                $"for {expectedElements} elements x {bytesPer}B — config.json shape disagrees with the " +
                "tensor; refusing to read past the buffer.");

        float[] result = new float[expectedElements];
        unsafe
        {
            fixed (byte* rp = raw)
            fixed (float* op = result)
            {
                int rc = SynInterop.TensorDecodeF32(rp, (nuint)expectedElements, dtype, op);
                if (rc != 0)
                    throw new InvalidOperationException(
                        $"laplace_tensor_decode_f32('{name}', dtype={tref.Dtype}) returned {rc}");
            }
        }
        return result;
    }

    internal static long BytesPerElement(string dtype) =>
        (long)SynInterop.TensorDtypeSize(SynInterop.TensorDtypeFromName(dtype));
}
