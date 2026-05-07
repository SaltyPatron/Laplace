namespace Laplace.Core.Abstractions;

using System.Collections.Generic;
using System.IO;

/// <summary>
/// P/Invoke surface for the native <c>TensorDecodeService</c>. Lossless
/// safetensors header parse + dtype decode (BF16 / F16 / F32 / F64 / FP8 /
/// I8 / I16 / I32 / I64 / U8). Used by the AI model decomposer family for
/// firefly extraction (read embedding tensor) and for weight-as-edge
/// extraction (read attention / FFN / LM-head / etc. tensors).
/// </summary>
public interface ITensorReader
{
    /// <summary>Parse a safetensors file's header into per-tensor metadata.</summary>
    IReadOnlyList<SafetensorEntry> ReadHeader(Stream safetensorsFile);

    /// <summary>Stream a tensor's data losslessly decoded to float32.</summary>
    void StreamFloat32(Stream safetensorsFile, SafetensorEntry entry, float[] destination);

    /// <summary>Stream a tensor's data losslessly decoded to float64.</summary>
    void StreamFloat64(Stream safetensorsFile, SafetensorEntry entry, double[] destination);
}

public record SafetensorEntry(
    string Name,
    string Dtype,
    long[] Shape,
    long DataOffsetStart,
    long DataOffsetEnd);
