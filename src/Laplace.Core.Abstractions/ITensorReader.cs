namespace Laplace.Core.Abstractions;

using System;
using System.Collections.Generic;

/// <summary>
/// Managed wrapper over the native B19 TensorDecodeService. Opens a
/// HuggingFace .safetensors file via the native parser and exposes a
/// handle from which per-tensor data can be streamed losslessly into
/// f32 / f64 destinations (decoding F16 / BF16 / F8_E4M3 / F8_E5M2 /
/// I64 / I32 / I16 / I8 / U64 / U32 / U16 / U8 / BOOL inline).
///
/// Used by the F5 model decomposer family for embedding firefly extraction
/// (read embedding tensor) and for per-tensor weight-as-edge extraction
/// (read attention / FFN / LM-head / etc. tensors).
/// </summary>
public interface ITensorReader
{
    /// <summary>Open a .safetensors file and parse its header.
    /// Returned handle is disposable; close releases native resources.</summary>
    ISafetensorsHandle Open(string filePath);
}

/// <summary>
/// Disposable handle to an open .safetensors file. Header is parsed once
/// at open time; tensor data is streamed on demand from the file's data
/// section. Supports both single-file and (via shared accessor logic)
/// sharded model.safetensors.index.json layouts.
/// </summary>
public interface ISafetensorsHandle : IDisposable
{
    /// <summary>All tensors declared in the header, in source order.</summary>
    IReadOnlyList<SafetensorEntry> Entries { get; }

    /// <summary>Look up a tensor by name. Returns null if absent.</summary>
    SafetensorEntry? Find(string name);

    /// <summary>Stream a tensor's data into a managed Span&lt;float&gt;,
    /// decoding the dtype losslessly. Span length must equal entry.ElementCount.</summary>
    void ReadFloat32(SafetensorEntry entry, Span<float> destination);

    /// <summary>Stream a tensor's data into a managed Span&lt;double&gt;,
    /// decoding the dtype losslessly. Span length must equal entry.ElementCount.</summary>
    void ReadFloat64(SafetensorEntry entry, Span<double> destination);
}

/// <summary>
/// One tensor entry in a .safetensors header. Mirrors the native
/// laplace_tensor_entry_t struct with managed types.
/// </summary>
public sealed record SafetensorEntry(
    string Name,
    SafetensorDtype Dtype,
    long[] Shape,
    long DataOffset,            // relative to data section start
    long DataByteLength)
{
    /// <summary>Total scalar element count (product of shape dimensions).</summary>
    public long ElementCount
    {
        get
        {
            var n = 1L;
            foreach (var dim in Shape) { n *= dim; }
            return n;
        }
    }
}

/// <summary>
/// Tensor element dtype. Values match the native laplace_dtype_t enum
/// for direct cast across the P/Invoke boundary.
/// </summary>
public enum SafetensorDtype
{
    Unknown = 0,
    F64,
    F32,
    F16,
    BF16,
    F8E4M3,
    F8E5M2,
    I64,
    I32,
    I16,
    I8,
    U64,
    U32,
    U16,
    U8,
    Bool,
}
