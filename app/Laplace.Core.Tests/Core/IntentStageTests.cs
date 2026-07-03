using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class IntentStageTests
{
    private static byte[] kSig =
    {
        (byte)'P', (byte)'G', (byte)'C', (byte)'O', (byte)'P', (byte)'Y',
        (byte)'\n', 0xff, (byte)'\r', (byte)'\n', 0x00
    };

    private static uint ReadBe32(ReadOnlySpan<byte> p) =>
        ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];

    private static short ReadBe16(ReadOnlySpan<byte> p) =>
        (short)(((ushort)p[0] << 8) | p[1]);

    [Fact]
    public void EmptyStream_HeaderAndTrailerOnly()
    {
        using var s = IntentStage.New(0);
        var bytes = s.EmitCopyBinary(IntentStageTable.Entities);
        Assert.Equal(21, bytes.Length);
        for (int i = 0; i < 11; i++) Assert.Equal(kSig[i], bytes[i]);
        Assert.Equal(0u, ReadBe32(bytes.AsSpan(11, 4)));
        Assert.Equal(0u, ReadBe32(bytes.AsSpan(15, 4)));
        Assert.Equal(0xff, bytes[19]);
        Assert.Equal(0xff, bytes[20]);
    }

    [Fact]
    public void AddEntity_RowEncodedCorrectly()
    {
        using var s = IntentStage.New(1);
        var id = new Hash128(0x1111_1111_1111_1111ul, 0x1111_1111_1111_1111ul);
        var typeId = new Hash128(0x2222_2222_2222_2222ul, 0x2222_2222_2222_2222ul);
        s.AddEntity(id, 5, typeId, firstObservedBy: null);
        Assert.Equal(1, s.EntityCount);

        var bytes = s.EmitCopyBinary(IntentStageTable.Entities);
        Assert.Equal(73, bytes.Length);
        Assert.Equal(4, ReadBe16(bytes.AsSpan(19, 2)));
        Assert.Equal(16u, ReadBe32(bytes.AsSpan(21, 4)));
        Assert.Equal(2u, ReadBe32(bytes.AsSpan(41, 4)));
        Assert.Equal(5, ReadBe16(bytes.AsSpan(45, 2)));
        Assert.Equal(16u, ReadBe32(bytes.AsSpan(47, 4)));
        Assert.Equal(unchecked((uint)-1), ReadBe32(bytes.AsSpan(67, 4)));
    }

    [Fact]
    public void AddPhysicality_NullTrajectoryAccepted()
    {
        using var s = IntentStage.New(1);
        var h = Hash128.Zero;
        var hb = new Hilbert128();
        s.AddPhysicality(
            h, h, physicalityType: 1,
            coord: stackalloc double[] { 0, 0, 0, 0 },
            hilbertIndex: hb,
            trajectoryXyzm: ReadOnlySpan<double>.Empty,
            nConstituents: 0,
            alignmentResidual: null,
            sourceDim: null,
            observedAtUnixUs: 0);
        Assert.Equal(1, s.PhysicalityCount);
    }

    [Fact]
    public void AddPhysicality_TrajectoryLengthMustBeMultipleOf4()
    {
        using var s = IntentStage.New(1);
        var h = Hash128.Zero;
        var hb = new Hilbert128();
        Assert.Throws<ArgumentException>(() =>
            s.AddPhysicality(h, h, 1,
                stackalloc double[] { 0, 0, 0, 0 }, hb,
                stackalloc double[5],
                nConstituents: 0,
                alignmentResidual: null,
                sourceDim: null,
                observedAtUnixUs: 0));
    }

    [Fact]
    public void AddAttestation_NullObjectAndContextEmitsNullFields()
    {
        using var s = IntentStage.New(1);
        var h = Hash128.Zero;
        s.AddAttestation(h, h, h, null, h, null,
            outcome: 2, lastObservedAtUnixUs: 0, observationCount: 1);
        Assert.Equal(1, s.AttestationCount);
        var bytes = s.EmitCopyBinary(IntentStageTable.Attestations);
        Assert.Equal(unchecked((uint)-1), ReadBe32(bytes.AsSpan(81, 4)));
    }

    [Fact]
    public void AddAttestation_DefaultHighwayMaskEmits32ZeroBytesNotNull()
    {
        // Regression test for the IsZero-vs-unset bug: a caller that doesn't pass
        // highwayMask (or passes an explicitly all-zero Mask256) gets Mask256.Zero,
        // which is a legitimately meaningful 32-byte mask (no relation bands
        // matched), not "no mask was computed." Before the fix, AddAttestation's
        // `mask.IsZero ? null : ...` collapsed both cases to a NULL column write.
        using var s = IntentStage.New(1);
        var h = Hash128.Zero;
        s.AddAttestation(h, h, h, null, h, null,
            outcome: 2, lastObservedAtUnixUs: 0, observationCount: 1);
        Assert.Equal(1, s.AttestationCount);

        string[] columns = IntentStage.CopyColumnList(IntentStageTable.Attestations)
            .Split(", ");
        int highwayMaskIndex = Array.IndexOf(columns, "highway_mask");
        Assert.True(highwayMaskIndex >= 0, "highway_mask column not found in CopyColumnList");

        var bytes = s.EmitCopyBinary(IntentStageTable.Attestations);
        int pos = 11 + 8; // signature + flags(4) + extension(4)
        short fieldCount = ReadBe16(bytes.AsSpan(pos, 2));
        Assert.Equal(columns.Length, fieldCount);
        pos += 2;
        for (int i = 0; i < fieldCount; i++)
        {
            uint len = ReadBe32(bytes.AsSpan(pos, 4));
            pos += 4;
            if (i == highwayMaskIndex)
            {
                Assert.Equal(32u, len); // NOT unchecked((uint)-1) -- must not be NULL
                for (int b = 0; b < 32; b++)
                    Assert.Equal(0, bytes[pos + b]);
                return;
            }
            if (len != unchecked((uint)-1))
                pos += (int)len;
        }
        Assert.Fail("did not reach highway_mask field");
    }

    [Fact]
    public void CopyColumnList_ReturnsKnownStringForEntities()
    {
        Assert.Equal("id, tier, type_id, first_observed_by",
            IntentStage.CopyColumnList(IntentStageTable.Entities));
    }

    [Fact]
    public void DisposeFreesHandle()
    {
        var s = IntentStage.New(1);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.EntityCount);
    }
}
