using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Raw COPY is a PostgreSQL protocol stream, not one unbounded frontend message.
/// A large Stream.WriteAsync on NpgsqlRawCopyStream becomes one CopyData message,
/// so body chunking is a correctness requirement rather than a throughput hint.
/// </summary>
public sealed class PgBinaryCopyProtocolTests
{
    [Fact]
    public void ProductionWriteWindowFitsPostgresCopyDataEnvelope()
    {
        Assert.Equal(0x3FFF_FFFA, PgBinaryCopy.MaxCopyDataPayloadBytes);
        Assert.InRange(PgBinaryCopy.WriteWindowBytes,
            1, PgBinaryCopy.MaxCopyDataPayloadBytes);
    }

    [Fact]
    public async Task ManagedBodyChunksWithoutChangingBytes()
    {
        byte[] body = Enumerable.Range(0, 17).Select(static i => (byte)i).ToArray();
        using var stream = new RecordingStream();

        await PgBinaryCopy.WriteManagedBodyAsync(stream, body, maxChunkBytes: 5);

        Assert.Equal(new[] { 5, 5, 5, 2 }, stream.Writes);
        Assert.Equal(body, stream.ToArray());
    }

    [Fact]
    public async Task EmptyManagedBodyWritesNoCopyDataPayload()
    {
        using var stream = new RecordingStream();

        await PgBinaryCopy.WriteManagedBodyAsync(stream, ReadOnlyMemory<byte>.Empty, maxChunkBytes: 5);

        Assert.Empty(stream.Writes);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task ManagedBodyRejectsWriteAbovePostgresProtocolCeiling()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PgBinaryCopy.WriteManagedBodyAsync(
                stream, new byte[1], PgBinaryCopy.MaxCopyDataPayloadBytes + 1));
    }

    private sealed class RecordingStream : MemoryStream
    {
        public List<int> Writes { get; } = new();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
