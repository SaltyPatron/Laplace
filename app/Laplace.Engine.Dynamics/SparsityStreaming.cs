namespace Laplace.Engine.Dynamics;

/// <summary>
/// Managed wrappers over the engine streaming sparsity primitives
/// (engine/dynamics/include/laplace/dynamics/sparsity.h). Used by
/// <c>WeightTensorETL</c> (ADR 0056) per Story B.1/B.2 of Framework Epic
/// #232.
///
/// Both functions are deterministic by construction: same input → byte-
/// identical mask across runs / thread counts (RULES R7), because the
/// inner kernels avoid floating-point reductions and the MKL_CBWR mode
/// is locked at static-init time.
/// </summary>
public static class SparsityStreaming
{
    /// <summary>Per-tensor relative top-k% over <paramref name="values"/>;
    /// writes 1 to <paramref name="outMask"/>[i] iff <c>|values[i]|</c> is in
    /// the top <paramref name="topkPct"/> fraction by absolute magnitude.
    /// Ties at the threshold are retained.</summary>
    /// <param name="topkPct">Must be in (0, 1].</param>
    public static void PerTensor(ReadOnlySpan<double> values, double topkPct, Span<byte> outMask)
    {
        if (values.IsEmpty) throw new ArgumentException("values must be non-empty", nameof(values));
        if (outMask.Length < values.Length)
            throw new ArgumentException("outMask must be at least values.Length", nameof(outMask));
        if (!(topkPct > 0.0 && topkPct <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(topkPct));

        unsafe
        {
            fixed (double* pv = values)
            fixed (byte* pm = outMask)
            {
                int rc = NativeInterop.SparsityPerTensorTopkStreaming(pv, (nuint)values.Length, topkPct, pm);
                if (rc != 0) throw new InvalidOperationException("sparsity_per_tensor_topk_streaming failed");
            }
        }
    }

    /// <summary>Per-row top-k over a row-major <paramref name="rows"/> buffer
    /// of size <paramref name="rowCount"/> × <paramref name="rowSize"/>. For
    /// each row, writes 1 to the columns whose absolute value is among the
    /// row's top <paramref name="k"/> (ties retained). If k ≥ row_size every
    /// column is retained; if k == 0 every column is pruned.</summary>
    public static void PerRow(
        ReadOnlySpan<double> rows,
        int rowCount, int rowSize, int k,
        Span<byte> outMasks)
    {
        if (rowCount < 0 || rowSize < 0 || k < 0) throw new ArgumentOutOfRangeException();
        if (rowCount == 0 || rowSize == 0) throw new ArgumentException("rowCount and rowSize must be positive");
        long total = (long)rowCount * rowSize;
        if (rows.Length < total) throw new ArgumentException("rows buffer too small", nameof(rows));
        if (outMasks.Length < total) throw new ArgumentException("outMasks buffer too small", nameof(outMasks));

        unsafe
        {
            fixed (double* pr = rows)
            fixed (byte* pm = outMasks)
            {
                int rc = NativeInterop.SparsityPerRowTopkStreaming(
                    pr, (nuint)rowCount, (nuint)rowSize, (nuint)k, pm);
                if (rc != 0) throw new InvalidOperationException("sparsity_per_row_topk_streaming failed");
            }
        }
    }
}
