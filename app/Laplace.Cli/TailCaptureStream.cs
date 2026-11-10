using System.Text;

namespace Laplace.Cli;

/// <summary>
/// Passes a child's stream through unchanged and keeps its last <see cref="Capacity"/> bytes,
/// so a failure can be recorded with what the child said rather than its exit status alone.
/// </summary>
internal sealed class TailCaptureStream(Stream inner, int capacity = 16 * 1024) : Stream
{
    private readonly byte[] _ring = new byte[capacity];
    private readonly object _gate = new();
    private long _written;

    public int Capacity => _ring.Length;

    public string Tail()
    {
        lock (_gate)
        {
            int count = (int)Math.Min(_written, _ring.Length);
            var bytes = new byte[count];
            int start = (int)((_written - count) % _ring.Length);
            int first = Math.Min(count, _ring.Length - start);
            Array.Copy(_ring, start, bytes, 0, first);
            Array.Copy(_ring, 0, bytes, first, count - first);
            // A cut may land inside a UTF-8 sequence; the decoder replaces only that fragment.
            return Encoding.UTF8.GetString(bytes).Trim();
        }
    }

    private void Keep(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length >= _ring.Length) data = data[^_ring.Length..];
            int at = (int)(_written % _ring.Length);
            int first = Math.Min(data.Length, _ring.Length - at);
            data[..first].CopyTo(_ring.AsSpan(at));
            data[first..].CopyTo(_ring);
            _written += data.Length;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Keep(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Keep(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
