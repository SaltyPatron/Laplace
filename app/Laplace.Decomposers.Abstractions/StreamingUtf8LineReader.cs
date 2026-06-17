using System.Buffers;
using System.Runtime.CompilerServices;

namespace Laplace.Decomposers.Abstractions;


public static class StreamingUtf8LineReader
{
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(
        string filePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        await foreach (var line in ReadLinesAsync(fs, ct))
            yield return line;
    }

    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var carry = ArrayPool<byte>.Shared.Rent(256);
        int carryLen = 0;
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        var lineBuf = ArrayPool<byte>.Shared.Rent(4096);
        int lineCap = lineBuf.Length;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] != (byte)'\n') continue;

                    int rawLen = carryLen + (i - start);
                    if (rawLen == 0)
                    {
                        carryLen = 0;
                        start = i + 1;
                        yield return ReadOnlyMemory<byte>.Empty;
                        continue;
                    }

                    int contentLen = rawLen;
                    if (i > start && buf[i - 1] == (byte)'\r')
                        contentLen--;
                    else if (carryLen > 0 && carry[carryLen - 1] == (byte)'\r')
                        contentLen--;

                    if (contentLen > 0)
                    {
                        EnsureLineCap(ref lineBuf, ref lineCap, contentLen);
                        int dst = 0;
                        if (carryLen > 0)
                        {
                            int cl = Math.Min(carryLen, contentLen);
                            if (carry[carryLen - 1] == (byte)'\r' && cl == carryLen && cl > 0)
                                cl--;
                            carry.AsSpan(0, cl).CopyTo(lineBuf.AsSpan(0, cl));
                            dst = cl;
                        }
                        int srcLen = Math.Min(i - start, contentLen - dst);
                        if (srcLen > 0)
                            buf.AsSpan(start, srcLen).CopyTo(lineBuf.AsSpan(dst, srcLen));
                        yield return lineBuf.AsMemory(0, contentLen);
                    }

                    carryLen = 0;
                    start = i + 1;
                }

                int tail = read - start;
                if (tail <= 0) continue;
                if (carryLen + tail > carry.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Max(carry.Length * 2, carryLen + tail));
                    carry.AsSpan(0, carryLen).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(carry);
                    carry = grown;
                }
                buf.AsSpan(start, tail).CopyTo(carry.AsSpan(carryLen, tail));
                carryLen += tail;
            }

            if (carryLen > 0)
            {
                int contentLen = carryLen;
                if (carry[carryLen - 1] == (byte)'\r') contentLen--;
                if (contentLen > 0)
                {
                    EnsureLineCap(ref lineBuf, ref lineCap, contentLen);
                    carry.AsSpan(0, contentLen).CopyTo(lineBuf);
                    yield return lineBuf.AsMemory(0, contentLen);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            ArrayPool<byte>.Shared.Return(carry);
            ArrayPool<byte>.Shared.Return(lineBuf);
        }
    }

    private static void EnsureLineCap(ref byte[] lineBuf, ref int lineCap, int need)
    {
        if (need <= lineCap) return;
        ArrayPool<byte>.Shared.Return(lineBuf);
        lineCap = Math.Max(need, lineCap * 2);
        lineBuf = ArrayPool<byte>.Shared.Rent(lineCap);
    }
}
