using System.Buffers.Binary;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// PostgreSQL binary-COPY (<c>FORMAT BINARY</c>) framing shared by every substrate bulk-load path.
/// Two row sources feed it: a contiguous NATIVE blob already serialized in an <c>IntentStage</c>
/// tuple buffer (<see cref="WriteNativeBlobAsync"/>), and MANAGED rows built field-by-field via
/// <see cref="PgCopyRowBuffer"/>. The header/trailer constants and per-field encoders previously
/// existed in up to four copies across the writers.
/// </summary>
internal static class PgBinaryCopy
{
    /// <summary>The 19-byte COPY binary signature: <c>PGCOPY\n\xff\r\n\0</c> + int32 flags(0) + int32 header-extension(0).</summary>
    public static readonly byte[] Header =
    {
        0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>The COPY binary trailer: an int16 field-count of -1 (0xFFFF).</summary>
    public static readonly byte[] Trailer = { 0xFF, 0xFF };

    /// <summary>Window size for streaming a native blob (8 MiB) — large COMPLETES_TO flushes must
    /// not stall the per-write COPY timeout, so the socket and server-side heap write overlap.</summary>
    public const long DefaultChunkBytes = 1L << 23;

    /// <summary>
    /// Streams a contiguous, already-serialized binary-COPY row blob (native <c>IntentStage</c>
    /// tuple buffer at <paramref name="ptr"/>/<paramref name="len"/>) through a raw binary COPY
    /// stream: header, chunked body, trailer, flush.
    /// </summary>
    public static async Task WriteNativeBlobAsync(
        Stream stream, IntPtr ptr, long len, CancellationToken ct = default)
    {
        await stream.WriteAsync(Header, ct);
        if (len > 0)
        {
            var window = new byte[(int)Math.Min(DefaultChunkBytes, len)];
            for (long off = 0; off < len; off += window.Length)
            {
                int n = (int)Math.Min(window.Length, len - off);
                unsafe
                {
                    new ReadOnlySpan<byte>((void*)(ptr + (nint)off), n).CopyTo(window);
                }
                await stream.WriteAsync(window.AsMemory(0, n), ct);
            }
        }
        await stream.WriteAsync(Trailer, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Encodes a 16-byte bytea field (<c>int32 length=16</c> + 16 bytes). Returns the new offset.</summary>
    public static int WriteHash(Span<byte> dst, int o, in Hash128 h)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 16);
        h.WriteBytes(dst[(o + 4)..(o + 20)]);
        return o + 20;
    }

    /// <summary>Encodes an int8 field (<c>int32 length=8</c> + big-endian int64). Returns the new offset.</summary>
    public static int WriteInt64Field(Span<byte> dst, int o, long v)
    {
        BinaryPrimitives.WriteInt32BigEndian(dst[o..], 8);
        BinaryPrimitives.WriteInt64BigEndian(dst[(o + 4)..], v);
        return o + 12;
    }
}

/// <summary>
/// A growable managed buffer for building a PG binary-COPY body row-by-row over a COPY stream.
/// Initializes with the COPY header; the caller calls <see cref="EnsureRoomAsync"/> for the next
/// row's byte budget, writes the row's fields into <see cref="Array"/> starting at
/// <see cref="Filled"/>, then <see cref="Commit"/>s the returned offset. <see cref="FinalizeAsync"/>
/// appends the trailer and flushes the tail.
/// </summary>
internal sealed class PgCopyRowBuffer
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _filled;

    public PgCopyRowBuffer(Stream stream, int initialCapacity = 4 * 1024 * 1024)
    {
        _stream = stream;
        _buffer = new byte[initialCapacity];
        PgBinaryCopy.Header.CopyTo(_buffer, 0);
        _filled = PgBinaryCopy.Header.Length;
    }

    /// <summary>The backing buffer; may be replaced by <see cref="EnsureRoomAsync"/> for an oversized row.</summary>
    public byte[] Array => _buffer;

    /// <summary>Current fill position — where the next row should be written.</summary>
    public int Filled => _filled;

    /// <summary>
    /// Guarantees room for <paramref name="rowBytes"/> more bytes: flushes the current fill if it
    /// would overflow, and grows the buffer if a single row exceeds capacity (a high-degree
    /// COMPLETES_TO walk bytea can dwarf the initial 4 MiB).
    /// </summary>
    public async ValueTask EnsureRoomAsync(int rowBytes, CancellationToken ct)
    {
        if (_filled + rowBytes <= _buffer.Length) return;
        if (_filled > 0)
        {
            await _stream.WriteAsync(_buffer.AsMemory(0, _filled), ct);
            _filled = 0;
        }
        if (rowBytes > _buffer.Length) _buffer = new byte[rowBytes];
    }

    /// <summary>Records the new fill position returned by a row encoder.</summary>
    public void Commit(int newFilled) => _filled = newFilled;

    /// <summary>Appends the binary-COPY trailer and flushes the remaining buffered bytes.</summary>
    public async Task FinalizeAsync(CancellationToken ct)
    {
        await EnsureRoomAsync(2, ct);
        BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_filled), -1);
        _filled += 2;
        await _stream.WriteAsync(_buffer.AsMemory(0, _filled), ct);
    }
}
