using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public sealed class ChessPositionFloorTests
{
    [DllImport("laplace_core", EntryPoint = "chess_position_table_unload")]
    private static extern void NativeUnload();

    // A format fixture exercises the real native mmap/CRC/lookup path. Its geometry
    // is deliberately fixed test data, not a claim about a generated chess catalog.
    private static string Blob(bool empty = false)
    {
        var body = new byte[128 + (empty ? 0 : 80)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x5048434C);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), empty ? 0UL : 1UL);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), 80);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(24), 128);
        if (!empty)
        {
            new Hash128(1, 2).WriteBytes(body.AsSpan(128));
            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(144 + 8 * i), (i + 1) / 10.0);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(192), 4);
            body[196] = 2;
        }
        string path = Path.Combine(Path.GetTempPath(), $"chess-position-observation-{Guid.NewGuid():N}.bin");
        using var file = File.Create(path);
        file.Write(body);
        file.Write(Hash128.Blake3(body).ToBytes());
        return path;
    }

    [Fact]
    public void InstalledFileIsNotLoadedAndEmptyMapIsAnExplicitState()
    {
        ChessPositionFloor.Unload();
        string path = Blob(empty: true);
        try
        {
            Assert.False(ChessPositionFloor.Observe().IsLoaded);
            ChessPositionFloor.Load(path);
            var loaded = ChessPositionFloor.Observe();
            Assert.True(loaded.IsLoaded);
            Assert.Equal(0, loaded.RecordCount);
            Assert.False(ChessPositionFloor.TryLookup(new(1, 2), out _, out _, out _, out _, out _, out _, out _));
            Assert.Equal(loaded.LookupMisses + 1, ChessPositionFloor.Observe().LookupMisses);
        }
        finally { ChessPositionFloor.Unload(); File.Delete(path); }
    }

    [Fact]
    public void NativeHitsAndMissesAreCountedAcrossParallelReadersAndUnload()
    {
        string path = Blob();
        try
        {
            ChessPositionFloor.Load(path);
            var before = ChessPositionFloor.Observe();
            Assert.Equal(1, before.RecordCount);
            Parallel.For(0, 64, iteration =>
            {
                Assert.True(ChessPositionFloor.TryLookup(new(1, 2), out var x, out var y,
                    out var z, out var m, out _, out var n, out var tier));
                Assert.Equal(0.1, x); Assert.Equal(0.2, y); Assert.Equal(0.3, z); Assert.Equal(0.4, m);
                Assert.Equal(4u, n); Assert.Equal((byte)2, tier);
                Assert.False(ChessPositionFloor.TryLookup(new(9, 9), out _, out _, out _, out _, out _, out _, out _));
            });
            var after = ChessPositionFloor.Observe();
            Assert.Equal(before.LookupHits + 64, after.LookupHits);
            Assert.Equal(before.LookupMisses + 64, after.LookupMisses);
            ChessPositionFloor.Unload();
            var unloaded = ChessPositionFloor.Observe();
            Assert.False(unloaded.IsLoaded);
            Assert.Equal(0, unloaded.RecordCount);
            Assert.Equal(after.LookupHits, unloaded.LookupHits);
            Assert.Equal(after.LookupMisses, unloaded.LookupMisses);
        }
        finally { ChessPositionFloor.Unload(); File.Delete(path); }
    }

    [Fact]
    public void ObservationReadsNativeMapEvenWhenManagedReadyFlagIsStale()
    {
        string path = Blob();
        try
        {
            ChessPositionFloor.Load(path);
            NativeUnload();
            var observed = ChessPositionFloor.Observe();
            Assert.False(observed.IsLoaded);
            Assert.Equal(0, observed.RecordCount);
        }
        finally { ChessPositionFloor.Unload(); File.Delete(path); }
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 63)]
    [InlineData((1UL << 60) + 1)]
    public void OverflowingCountIsRejectedBeforeLookupAndKeepsThePriorMap(ulong count)
    {
        string valid = Blob(), invalid = Blob();
        try
        {
            ChessPositionFloor.Load(valid);
            var bytes = File.ReadAllBytes(invalid);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), count);
            SaveWithChecksum(invalid, bytes);
            var error = Assert.Throws<InvalidOperationException>(() => ChessPositionFloor.Load(invalid));
            Assert.Contains("layout mismatch", error.Message);
            Assert.Equal(1, ChessPositionFloor.Observe().RecordCount);
            Assert.True(ChessPositionFloor.TryLookup(new(1, 2), out _, out _, out _, out _, out _, out _, out _));
        }
        finally { ChessPositionFloor.Unload(); File.Delete(valid); File.Delete(invalid); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ChecksummedExtraBytesDuplicateAndUnsortedRecordsAreRejected(int kind)
    {
        string path = Blob();
        ChessPositionFloor.Unload();
        try
        {
            var original = File.ReadAllBytes(path);
            int extra = kind == 0 ? 1 : 80;
            var bytes = new byte[original.Length + extra];
            original.AsSpan(0, original.Length - 16).CopyTo(bytes);
            if (kind != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 2);
                original.AsSpan(128, 80).CopyTo(bytes.AsSpan(208));
                if (kind == 2) new Hash128(0, 0).WriteBytes(bytes.AsSpan(208));
            }
            SaveWithChecksum(path, bytes);
            Assert.Throws<InvalidOperationException>(() => ChessPositionFloor.Load(path));
            Assert.False(ChessPositionFloor.Observe().IsLoaded);
        }
        finally { ChessPositionFloor.Unload(); File.Delete(path); }
    }

    private static void SaveWithChecksum(string path, byte[] bytes)
    {
        Hash128.Blake3(bytes.AsSpan(0, bytes.Length - 16)).WriteBytes(bytes.AsSpan(bytes.Length - 16));
        File.WriteAllBytes(path, bytes);
    }
}
