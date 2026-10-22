using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class CodepointPerfcacheTests
{
    [Fact]
    public void Record_Is_Exactly_64_Bytes() => Assert.Equal(64, Marshal.SizeOf<CodepointRecord>());

    [Fact]
    public void Fields_Read_At_The_Native_Struct_Offsets()
    {
        Span<byte> bytes = stackalloc byte[64];
        BitConverter.TryWriteBytes(bytes[..8], 0x1122334455667788ul);
        BitConverter.TryWriteBytes(bytes.Slice(8, 8), 0x99aabbccddeeff00ul);
        BitConverter.TryWriteBytes(bytes.Slice(16, 8), 1L << 52);
        BitConverter.TryWriteBytes(bytes.Slice(24, 8), -(1L << 51));
        BitConverter.TryWriteBytes(bytes.Slice(32, 8), 1L << 50);
        BitConverter.TryWriteBytes(bytes.Slice(40, 8), -(1L << 49));
        BitConverter.TryWriteBytes(bytes.Slice(48, 8), 0x0102030405060708ul);
        BitConverter.TryWriteBytes(bytes.Slice(56, 4), 12345u);
        BitConverter.TryWriteBytes(bytes.Slice(60, 4), 0u);
        var record = MemoryMarshal.Read<CodepointRecord>(bytes);
        Assert.Equal(0x1122334455667788ul, record.Hash.Hi);
        Assert.Equal(0x99aabbccddeeff00ul, record.Hash.Lo);
        Assert.Equal(0.5, record.CoordX);
        Assert.Equal(-0.25, record.CoordY);
        Assert.Equal(0.125, record.CoordZ);
        Assert.Equal(-0.0625, record.CoordM);
        Assert.Equal(0x0102030405060708ul, record.NativeHilbert);
        Assert.Equal(12345u, record.UcaOrder);
        Assert.Equal(0u, record.Pad);
    }
}
