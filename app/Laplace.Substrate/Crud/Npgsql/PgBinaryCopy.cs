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


    // PostgreSQL frontend CopyData uses a 32-bit message length including the
    // four-byte length word itself. The backend's PQ_LARGE_MESSAGE_LIMIT is
    // MaxAllocSize - 1 (0x3ffffffe), so one CopyData payload may contain at most
    // 0x3ffffffa bytes. NpgsqlRawCopyStream turns one sufficiently-large
    // Stream.WriteAsync call into one CopyData message; every raw body write must
    // therefore stay at or below this payload ceiling.
    internal const int MaxCopyDataPayloadBytes = 0x3FFF_FFFA;


    // All concurrently active COPY connections together receive one flush envelope
    // of unmanaged-to-managed streaming windows. No independent 8 MiB window remains.
    public static readonly long StreamWindowBytes = Math.Max(1,
        IngestSizing.ResolveWorkingSetFlushEnvelopeBytes()
        / Math.Max(1, IngestTopology.Current.ApplyPartitions));

    // Machine-derived throughput sizing remains authoritative, but the wire protocol
    // is a hard upper bound regardless of how large a future machine envelope becomes.
    internal static readonly int WriteWindowBytes = checked((int)Math.Max(1L,
        Math.Min(Math.Min(StreamWindowBytes, (long)MaxCopyDataPayloadBytes),
            (long)Array.MaxLength)));






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
            ? new byte[(int)Math.Min((long)WriteWindowBytes, maxLen)]
            : null;
        foreach (var (ptr, len) in blobs)
            await WriteBlobBodyAsync(stream, ptr, len, window, ct);
        await stream.WriteAsync(Trailer, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task WriteBlobBodyAsync(
        Stream stream, IntPtr ptr, long len, byte[]? reuse, CancellationToken ct)
    {
        if (len < 0) throw new ArgumentOutOfRangeException(nameof(len));
        if (len == 0) return;

        int windowLength = (int)Math.Min((long)WriteWindowBytes, len);
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

    /// <summary>
    /// Write an already-managed PGCOPY body as a sequence of protocol-safe raw stream
    /// writes. CopyData message boundaries have no COPY tuple semantics, so slicing the
    /// body here preserves the byte stream while preventing Npgsql from constructing an
    /// oversized single frontend message.
    /// </summary>
    internal static Task WriteManagedBodyAsync(
        Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        => WriteManagedBodyAsync(stream, body, WriteWindowBytes, ct);

    /// <summary>Testable overload with an explicit raw-write ceiling.</summary>
    internal static async Task WriteManagedBodyAsync(
        Stream stream, ReadOnlyMemory<byte> body, int maxChunkBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxChunkBytes is <= 0 or > MaxCopyDataPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxChunkBytes), maxChunkBytes,
                $"COPY raw-write ceiling must be in [1,{MaxCopyDataPayloadBytes}]");

        for (int off = 0; off < body.Length;)
        {
            int n = Math.Min(maxChunkBytes, body.Length - off);
            await stream.WriteAsync(body.Slice(off, n), ct).ConfigureAwait(false);
            off += n;
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
