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

    
    
    public const long DefaultChunkBytes = 1L << 23;

    
    
    
    
    
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
            ? new byte[(int)Math.Min(DefaultChunkBytes, maxLen)]
            : null;
        foreach (var (ptr, len) in blobs)
            await WriteBlobBodyAsync(stream, ptr, len, window, ct);
        await stream.WriteAsync(Trailer, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task WriteBlobBodyAsync(
        Stream stream, IntPtr ptr, long len, byte[]? reuse, CancellationToken ct)
    {
        if (len <= 0) return;
        byte[] window = reuse ?? new byte[(int)Math.Min(DefaultChunkBytes, len)];
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

    
    public byte[] Array => _buffer;

    
    public int Filled => _filled;

    
    
    
    
    
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

    
    public void Commit(int newFilled) => _filled = newFilled;

    
    public async Task FinalizeAsync(CancellationToken ct)
    {
        await EnsureRoomAsync(2, ct);
        BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_filled), -1);
        _filled += 2;
        await _stream.WriteAsync(_buffer.AsMemory(0, _filled), ct);
    }
}
