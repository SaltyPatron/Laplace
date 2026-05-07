namespace Laplace.Core;

using System;
using System.Buffers;

using Laplace.Core.Abstractions;
using Laplace.Core.Native;

/// <summary>
/// Managed wrapper over <c>RleService</c>. Encodes/decodes byte runs and
/// hash runs. Used by composition emission so entities are referenced as
/// FEW times as physically possible.
/// Phase 2 / Track D / D2.
/// </summary>
public sealed class RleEncoder : IRleEncoder
{
    public (AtomId Hash, int Count)[] EncodeHashes(ReadOnlySpan<AtomId> orderedChildren)
    {
        var n = orderedChildren.Length;
        if (n == 0)
        {
            return Array.Empty<(AtomId, int)>();
        }
        var packed     = ArrayPool<byte>.Shared.Rent(n * NativeHash.HashBytes);
        var outHashes  = ArrayPool<byte>.Shared.Rent(n * NativeHash.HashBytes);
        var outCounts  = ArrayPool<int>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; ++i)
            {
                orderedChildren[i].AsSpan()
                    .CopyTo(packed.AsSpan(i * NativeHash.HashBytes, NativeHash.HashBytes));
            }
            nuint nRuns;
            unsafe
            {
                fixed (byte* inPtr   = packed)
                fixed (byte* outHPtr = outHashes)
                fixed (int*  outCPtr = outCounts)
                {
                    nRuns = NativeRle.EncodeHashes(inPtr, (nuint)n, outHPtr, outCPtr);
                }
            }
            var result = new (AtomId Hash, int Count)[(int)nRuns];
            for (int r = 0; r < (int)nRuns; ++r)
            {
                var hashCopy = new byte[NativeHash.HashBytes];
                Buffer.BlockCopy(outHashes, r * NativeHash.HashBytes, hashCopy, 0, NativeHash.HashBytes);
                result[r] = (new AtomId(hashCopy), outCounts[r]);
            }
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(outHashes);
            ArrayPool<int>.Shared.Return(outCounts);
        }
    }

    public AtomId[] DecodeHashes(ReadOnlySpan<(AtomId Hash, int Count)> runs)
    {
        var nRuns = runs.Length;
        if (nRuns == 0)
        {
            return Array.Empty<AtomId>();
        }
        long totalLong = 0;
        for (int i = 0; i < nRuns; ++i)
        {
            totalLong += runs[i].Count;
        }
        if (totalLong > int.MaxValue)
        {
            throw new InvalidOperationException("RLE expansion exceeds int.MaxValue elements.");
        }
        var total = (int)totalLong;

        var packed = ArrayPool<byte>.Shared.Rent(nRuns * NativeHash.HashBytes);
        var counts = ArrayPool<int>.Shared.Rent(nRuns);
        var output = ArrayPool<byte>.Shared.Rent(total * NativeHash.HashBytes);
        try
        {
            for (int i = 0; i < nRuns; ++i)
            {
                runs[i].Hash.AsSpan().CopyTo(packed.AsSpan(i * NativeHash.HashBytes, NativeHash.HashBytes));
                counts[i] = runs[i].Count;
            }
            unsafe
            {
                fixed (byte* hPtr = packed)
                fixed (int*  cPtr = counts)
                fixed (byte* oPtr = output)
                {
                    NativeRle.DecodeHashes(hPtr, cPtr, (nuint)nRuns, oPtr, (nuint)total);
                }
            }
            var result = new AtomId[total];
            for (int i = 0; i < total; ++i)
            {
                var hashCopy = new byte[NativeHash.HashBytes];
                Buffer.BlockCopy(output, i * NativeHash.HashBytes, hashCopy, 0, NativeHash.HashBytes);
                result[i] = new AtomId(hashCopy);
            }
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<int>.Shared.Return(counts);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    public (byte Value, int Count)[] EncodeBytes(ReadOnlySpan<byte> bytes)
    {
        var n = bytes.Length;
        if (n == 0)
        {
            return Array.Empty<(byte, int)>();
        }
        var values = ArrayPool<byte>.Shared.Rent(n);
        var counts = ArrayPool<int>.Shared.Rent(n);
        try
        {
            nuint nRuns;
            unsafe
            {
                fixed (byte* inPtr  = bytes)
                fixed (byte* valPtr = values)
                fixed (int*  cntPtr = counts)
                {
                    nRuns = NativeRle.EncodeBytes(inPtr, (nuint)n, valPtr, cntPtr);
                }
            }
            var result = new (byte, int)[(int)nRuns];
            for (int i = 0; i < (int)nRuns; ++i)
            {
                result[i] = (values[i], counts[i]);
            }
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(values);
            ArrayPool<int>.Shared.Return(counts);
        }
    }
}
