using System.Buffers;
using System.Buffers.Binary;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

public class StagedClaimIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeCopyWriterUsesTheSameActualVerticesAsTheClient(bool hasObject, bool zeroObject)
    {
        byte[] subject = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] relation = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        byte[] obj = zeroObject ? new byte[16] : Enumerable.Range(32, 16).Select(i => (byte)i).ToArray();
        using var input = new MemoryStream();
        input.Write([(byte)'P', (byte)'G', (byte)'C', (byte)'O', (byte)'P', (byte)'Y', 10, 255, 13, 10, 0]);
        WriteInt(0, 4); // COPY flags
        WriteInt(0, 4); // header extension length
        WriteInt(12, 2);
        Field(subject); Field(relation);
        if (hasObject) Field(obj); else Null();
        Field(subject); // witness
        Number(1_500_000_000_000); Number(30_000_000_000);
        Number(1); Number(1_000_000_000); Number(50);
        Null(); Null(); Null(); // this cell has no previous standing
        WriteInt(-1, 2);

        using var operation = NativeStagedLoad.Create(StagedLoadOperation.Score);
        byte[] bytes = input.ToArray();
        for (int i = 0; i < bytes.Length; i++) operation.Feed(bytes.AsSpan(i, 1), i + 1 == bytes.Length);
        var output = new ArrayBufferWriter<byte>();
        operation.TakeOutput(0, output);
        Assert.Equal((1UL, 1UL), operation.Counts);
        Assert.Equal(9, BinaryPrimitives.ReadInt16BigEndian(output.WrittenSpan[19..]));
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(output.WrittenSpan[21..]));
        Hash128 actual = Hash128.FromBytes(output.WrittenSpan.Slice(25, 16));
        Hash128 s = Hash128.FromBytes(subject), r = Hash128.FromBytes(relation);
        Hash128? o = hasObject ? Hash128.FromBytes(obj) : null;
        Assert.Equal(ConsensusKeys.EdgeId(s, r, o), actual);
        if (!hasObject) Assert.NotEqual(ConsensusKeys.EdgeId(s, r, Hash128.Zero), actual);

        void WriteInt(long value, int width)
        {
            Span<byte> encoded = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(encoded, value);
            input.Write(encoded[(8 - width)..]);
        }
        void Null() => WriteInt(-1, 4);
        void Field(byte[] value) { WriteInt(value.Length, 4); input.Write(value); }
        void Number(long value) { WriteInt(8, 4); WriteInt(value, 8); }
    }
}
