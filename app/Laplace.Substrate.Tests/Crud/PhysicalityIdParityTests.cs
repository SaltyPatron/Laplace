using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// GH #904. The physicality id pre-image exists twice — <see cref="PhysicalityId.Compute"/>
/// and <c>laplace_physicality_id_compute</c> in <c>engine/core/src/content_witness_batch.c</c>
/// — on a substrate whose identity axiom is that ids are content hashes. Prose in both
/// files said "keep these bit-identical"; nothing enforced it, so a layout edit on either
/// side would silently mint divergent physicality ids and forge duplicate rows (the same
/// failure class as the 319 chess-move duplicates both headers record).
///
/// These tests pin the LAYOUT independently: each rebuilds the documented 18 bytes by
/// hand — entity id (16, as stored) then the physicality type as a little-endian int16 —
/// and hashes that. They never call the production helper to build the expectation, so a
/// change to <c>PhysicalityId.Compute</c> fails here instead of propagating.
///
/// C side is exported as <c>laplace_physicality_id_compute</c> through
/// <see cref="NativeInterop.PhysicalityIdCompute"/>; <see cref="Compute_MatchesNativeC"/>
/// is the real cross-language gate.
/// </summary>
public class PhysicalityIdParityTests
{
    private static Hash128 H(byte first, int step = 1)
    {
        Span<byte> b = stackalloc byte[16];
        for (int i = 0; i < 16; i++) b[i] = (byte)(first + i * step);
        return Hash128.FromBytes(b);
    }

    /// <summary>The spec, spelled out byte by byte — deliberately not sharing code
    /// with the implementation under test.</summary>
    private static Hash128 SpecPreImage(Hash128 entityId, short type)
    {
        Span<byte> buf = stackalloc byte[18];
        entityId.WriteBytes(buf.Slice(0, 16));
        buf[16] = (byte)(type & 0xFF);
        buf[17] = (byte)((type >> 8) & 0xFF);
        return Hash128.Blake3(buf);
    }

    [Theory]
    [InlineData(PhysicalityType.Content)]
    [InlineData(PhysicalityType.Projection)]
    public void Compute_MatchesDocumented18BytePreImage(PhysicalityType type)
    {
        var entityId = H(0x00);
        Assert.Equal(SpecPreImage(entityId, (short)type), PhysicalityId.Compute(entityId, type));
    }

    [Fact]
    public void Compute_TypeBytesAreLittleEndian_NotHostOrder()
    {
        // 0x0102 has distinguishable bytes, so a big-endian write produces a different
        // hash. This is the assertion that would have caught the BitConverter form on a
        // big-endian host, where it silently changed identity for identical content.
        var entityId = H(0x40);
        const short type = 0x0102;

        Span<byte> le = stackalloc byte[18];
        entityId.WriteBytes(le.Slice(0, 16));
        le[16] = 0x02; le[17] = 0x01;

        Span<byte> be = stackalloc byte[18];
        entityId.WriteBytes(be.Slice(0, 16));
        be[16] = 0x01; be[17] = 0x02;

        Assert.NotEqual(Hash128.Blake3(be), Hash128.Blake3(le));
        Assert.Equal(Hash128.Blake3(le), PhysicalityId.Compute(entityId, (PhysicalityType)type));
    }

    [Fact]
    public void Compute_IdentityIsEntityAndTypeOnly_NotGeometry()
    {
        // The axiom both headers state: geometry is payload and never enters the id, so
        // two physicalities of the same (entity, type) are ONE row however their floats
        // came out. This is what the 319 duplicate chess-move entities violated.
        var entityId = H(0x11, 3);
        Assert.Equal(
            PhysicalityId.Compute(entityId, PhysicalityType.Content),
            PhysicalityId.Compute(entityId, PhysicalityType.Content));
    }

    [Fact]
    public void Compute_DistinctTypesGiveDistinctIds()
    {
        var entityId = H(0x77, 5);
        Assert.NotEqual(
            PhysicalityId.Compute(entityId, PhysicalityType.Content),
            PhysicalityId.Compute(entityId, PhysicalityType.Projection));
    }

    [Fact]
    public void Compute_DistinctEntitiesGiveDistinctIds()
    {
        Assert.NotEqual(
            PhysicalityId.Compute(H(0x01), PhysicalityType.Content),
            PhysicalityId.Compute(H(0x02), PhysicalityType.Content));
    }

    [Theory]
    [InlineData(PhysicalityType.Content)]
    [InlineData(PhysicalityType.Projection)]
    public unsafe void Compute_MatchesNativeC(PhysicalityType type)
    {
        var entityId = H(0xA5, 7);
        Hash128 native;
        NativeInterop.PhysicalityIdCompute(entityId, (short)type, &native);
        Assert.Equal(PhysicalityId.Compute(entityId, type), native);
        Assert.Equal(SpecPreImage(entityId, (short)type), native);
    }
}
