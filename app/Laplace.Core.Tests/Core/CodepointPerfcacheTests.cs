using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class CodepointPerfcacheTests
{
    [Fact]
    public void Record_Is_Exactly_80_Bytes()
    {
        Assert.Equal(80, Marshal.SizeOf<CodepointRecord>());
    }

    [Fact]
    public void Fields_Read_At_The_C_Struct_Offsets()
    {
        Span<byte> buf = stackalloc byte[80];

        BitConverter.TryWriteBytes(buf.Slice(0, 4), 0x0041u);
        BitConverter.TryWriteBytes(buf.Slice(4, 4), 12345u);
        BitConverter.TryWriteBytes(buf.Slice(8, 8), 0.5);
        BitConverter.TryWriteBytes(buf.Slice(16, 8), -0.25);
        BitConverter.TryWriteBytes(buf.Slice(24, 8), 0.125);
        BitConverter.TryWriteBytes(buf.Slice(32, 8), -0.0625);
        buf[40] = 0xAB;
        buf[55] = 0xCD;
        BitConverter.TryWriteBytes(buf.Slice(56, 8), 0x1122334455667788ul);
        BitConverter.TryWriteBytes(buf.Slice(64, 8), 0x99AABBCCDDEEFF00ul);
        uint flags = (1u << 0) | (10u << 4) | (8u << 9) | (0u << 13) | (230u << 15);
        BitConverter.TryWriteBytes(buf.Slice(72, 4), flags);

        CodepointRecord r = MemoryMarshal.Read<CodepointRecord>(buf);

        Assert.Equal(0x0041u, r.Codepoint);
        Assert.Equal(12345u, r.UcaOrder);
        Assert.Equal(0.5, r.CoordX);
        Assert.Equal(-0.25, r.CoordY);
        Assert.Equal(0.125, r.CoordZ);
        Assert.Equal(-0.0625, r.CoordM);
        Assert.Equal(0x1122334455667788ul, r.Hash.Hi);
        Assert.Equal(0x99AABBCCDDEEFF00ul, r.Hash.Lo);
        Assert.Equal(1, r.GraphemeBreak);
        Assert.Equal(10, r.WordBreak);
        Assert.Equal(8, r.SentenceBreak);
        Assert.Equal(0, r.IndicConjunctBreak);
        Assert.Equal(230, r.CombiningClass);
    }
}
