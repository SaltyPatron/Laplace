using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// ConsensusKeys.EdgeId is byte-identical to SQL laplace.consensus_id (BLAKE3 over
/// subject(16)‖type(16)‖object-or-zero(16), FOLD_IDENT_LEN = 48), so one typed triple's
/// consensus cell has one id in C# and in the extension. Expected values are
/// laplace.consensus_id outputs; a failure is a divergence to fix in code, not in the
/// fixture.
/// </summary>
public class ConsensusKeysParityTests
{
    private static Hash128 H(byte first, int step = 1)
    {
        Span<byte> b = stackalloc byte[16];
        for (int i = 0; i < 16; i++) b[i] = (byte)(first + i * step);
        return Hash128.FromBytes(b);
    }

    private static string Hex(Hash128 h)
    {
        Span<byte> b = stackalloc byte[16];
        h.WriteBytes(b);
        return Convert.ToHexStringLower(b);
    }

    [Fact]
    public void EdgeId_WithObject_MatchesServerConsensusId()
    {
        var subject = H(0x00);
        var type = H(0x10);
        var obj = H(0x20);
        Assert.Equal("5e9902524b30f4f4cfd5d2180fd357bb",
            Hex(ConsensusKeys.EdgeId(subject, type, obj)));
    }

    [Fact]
    public void EdgeId_NullObject_MatchesServerConsensusId_ZeroPadded()
    {
        var subject = H(0x00);
        var type = H(0x10);
        Assert.Equal("bb7fea3858e878658da23d1ed4bd8236",
            Hex(ConsensusKeys.EdgeId(subject, type, (Hash128?)null)));
    }

    [Fact]
    public void EdgeId_ExtremeBytes_MatchesServerConsensusId()
    {
        var subject = H(0xff, 0);
        var type = H(0x00, 0);
        var obj = H(0x01);
        Assert.Equal("1ee31d8116b5473376dbff6b7950602d",
            Hex(ConsensusKeys.EdgeId(subject, type, obj)));
    }

    [Fact]
    public void EdgeId_NullableOverload_AgreesWithConcreteOverload()
    {
        var subject = H(0x30);
        var type = H(0x40);
        var obj = H(0x50);
        Assert.Equal(
            ConsensusKeys.EdgeId(subject, type, obj),
            ConsensusKeys.EdgeId(subject, type, (Hash128?)obj));
    }

}
