using System.Buffers.Binary;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;








internal static class PgBinaryCopy
{

    public static readonly byte[] Header =
    {
        0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };


    public static readonly byte[] Trailer = { 0xFF, 0xFF };



    // All concurrently active COPY connections together receive one flush envelope
    // of unmanaged-to-managed streaming windows. No independent 8 MiB window remains.
    public static readonly long StreamWindowBytes = Math.Max(1,
        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes()
        / Math.Max(1, IngestTopology.Current.ApplyPartitions));






    public static async Task WriteNativeBlobAsync(
        Stream stream, IntPtr ptr, long len, CancellationToken ct = default)
    {
        await stream.WriteAsync(Header, ct);
        await WriteBlobBodyAsync(stream, ptr, len, null, ct);
        await stream.WriteAsync(Trailer, ct);
        await stream.FlushAsync(ct);
    }






    public static async Task WriteNativeBlobsAsync(
        Stream stream, IReadOnlyList<(IntPtr Ptr, long Len)> blobs, CancellationToken ct = default)
    {
        await stream.WriteAsync(Header, ct);
        long maxLen = 0;
        foreach (var (_, len) in blobs) if (len > maxLen) maxLen = len;
        byte[]? window = maxLen > 0
            ? new byte[(int)Math.Min(Math.Min(StreamWindowBytes, Array.MaxLength), maxLen)]
            : null;
        foreach (var (ptr, len) in blobs)
            await WriteBlobBodyAsync(stream, ptr, len, window, ct);
        await stream.WriteAsync(Trailer, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task WriteBlobBodyAsync(
        Stream stream, IntPtr ptr, long len, byte[]? reuse, CancellationToken ct)
    {
        int windowLength = (int)Math.Min(
            Math.Min(StreamWindowBytes, Array.MaxLength), len);
        if (len <= windowLength)
        {
            int n = (int)len;
            byte[] buf = reuse is not null && reuse.Length >= n ? reuse : new byte[n];
            unsafe
            {
                new ReadOnlySpan<byte>((void*)ptr, n).CopyTo(buf);
            }
            await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            return;
        }
        byte[] window = reuse ?? new byte[windowLength];
        for (long off = 0; off < len; off += window.Length)
        {
            int n = (int)Math.Min(window.Length, len - off);
            unsafe
            {
                new ReadOnlySpan<byte>((void*)(ptr + (nint)off), n).CopyTo(window);
            }
            await stream.WriteAsync(window.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }


    public static int WriteHash(Span<byte> dst, int o, in Hash128 h)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 16);
        h.WriteBytes(dst[(o + 4)..(o + 20)]);
        return o + 20;
    }


    public static int WriteInt64Field(Span<byte> dst, int o, long v)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 8);
        BinaryPrimitives.WriteInt64BigEndian(dst[(o + 4)..], v);
        return o + 12;
    }
}
