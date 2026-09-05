using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using SynInterop = Laplace.Engine.Synthesis.NativeInterop;

namespace Laplace.Decomposers.Model;





public static class WeightTensorETL
{

    public static byte[] LoadRawBytes(
        Dictionary<string, SafetensorsContainerParser.TensorReference> refMap, string name,
        SourceEntityIdConventions.ModelContentSnapshot? snapshot = null)
    {
        var tref = refMap[name];
        if (snapshot is not null)
            return snapshot.ReadRange(tref.FilePath, tref.AbsoluteDataStart, tref.DataLength);
        byte[] rawBytes = new byte[tref.DataLength];
        using var fs = IngestIo.OpenSequentialRead(tref.FilePath);
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
        string name, long expectedElements,
        SourceEntityIdConventions.ModelContentSnapshot? snapshot = null)
    {
        var tref = refMap[name];
        int dtype = SynInterop.TensorDtypeFromName(tref.Dtype);
        if (dtype < 0)
        {
            // O10 — record-don't-interpret. Unknown / block-quant dtypes are witnessed as
            // undecodable (empty payload); inventing zeros was the prior defect, refusing
            // the whole ingest pushed operators onto GGUF. Callers that need floats must
            // treat Length == 0 as "no numeric interpretation available".
            return Array.Empty<float>();
        }

        long bytesPer = (long)SynInterop.TensorDtypeSize(dtype);
        if (expectedElements < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedElements), "negative element count");
        if (expectedElements > tref.DataLength / bytesPer)
            throw new InvalidDataException(
                $"safetensors: tensor '{name}' dtype {tref.Dtype} holds {tref.DataLength} bytes, too few " +
                $"for {expectedElements} elements x {bytesPer}B — config.json shape disagrees with the " +
                "tensor; refusing to read past the buffer.");

        float[] result = new float[expectedElements];
        if (snapshot is not null)
        {
            // The selected artifact is already open and content-addressed.
            // Decode bounded sequential tiles from that held handle so a large
            // tensor never exists simultaneously as raw bytes and decoded f32.
            snapshot.Read(tref.FilePath, stream =>
            {
                stream.Position = tref.AbsoluteDataStart;
                int tileBytes = Math.Max((int)bytesPer,
                    MemoryTopology.CopyStartupBytesPerConnection);
                tileBytes -= tileBytes % (int)bytesPer;
                var rawTile = new byte[tileBytes];
                long elementAt = 0;
                while (elementAt < expectedElements)
                {
                    int elements = (int)Math.Min(
                        rawTile.Length / bytesPer, expectedElements - elementAt);
                    int byteCount = checked(elements * (int)bytesPer);
                    int read = 0;
                    while (read < byteCount)
                    {
                        int count = stream.Read(rawTile, read, byteCount - read);
                        if (count == 0)
                            throw new IOException($"safetensors: truncated data for {name}");
                        read += count;
                    }
                    unsafe
                    {
                        fixed (byte* input = rawTile)
                        fixed (float* output = result)
                        {
                            int rc = SynInterop.TensorDecodeF32(
                                input, (nuint)elements, dtype, output + elementAt);
                            if (rc != 0)
                                throw new InvalidOperationException(
                                    $"laplace_tensor_decode_f32('{name}', dtype={tref.Dtype}) returned {rc}");
                        }
                    }
                    elementAt += elements;
                }
                return 0;
            });
            return result;
        }

        byte[] raw = LoadRawBytes(refMap, name);
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
