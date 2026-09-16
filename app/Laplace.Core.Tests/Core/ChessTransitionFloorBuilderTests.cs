using System.Buffers.Binary;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public sealed class ChessTransitionFloorBuilderTests
{
    private static (Hash128 Key, Hash128 To) Pair(int i) =>
        (new Hash128((ulong)i * 19, (ulong)i), new Hash128((ulong)i, ulong.MaxValue - (ulong)i));

    private static (Hash128 Key, Hash128 To)[] Sorted(params int[] values) =>
        values.Select(Pair).OrderBy(x => x.Key,
            Comparer<Hash128>.Create((a, b) => a.CompareToBytewise(b))).ToArray();

    private static byte[] ExpectedV1((Hash128 Key, Hash128 To)[] records)
    {
        var bytes = new byte[64 + records.Length * 32 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x5448434C);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), (ulong)records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            records[i].Key.WriteBytes(bytes.AsSpan(64 + i * 32));
            records[i].To.WriteBytes(bytes.AsSpan(80 + i * 32));
        }
        Hash128.Blake3(bytes.AsSpan(0, bytes.Length - 16)).WriteBytes(bytes.AsSpan(bytes.Length - 16));
        return bytes;
    }

    [Fact]
    public void StreamingWriterConsumesOnceAndPinsEveryV1Byte()
    {
        using var files = new Files();
        var records = Sorted(1, 15, 257, 256);
        int enumerations = 0;
        IEnumerable<(Hash128 Key, Hash128 To)> Once()
        {
            Assert.Equal(1, ++enumerations);
            foreach (var row in records) yield return row;
        }
        string output = files.PathFor("stream.bin");
        ChessTransitionFloor.WriteBlob(output, Once(), (ulong)records.Length);
        Assert.Equal(1, enumerations);
        Assert.Equal(ExpectedV1(records), File.ReadAllBytes(output));
        ChessTransitionFloor.Load(output);
        foreach (var row in records)
        {
            Assert.True(ChessTransitionFloor.TryLookup(row.Key, out var actual, out var source));
            Assert.Equal(row.To, actual);
            Assert.Equal(ChessTransitionFloor.LookupSource.Persistent, source);
        }
        string adapter = files.PathFor("adapter.bin");
        ChessTransitionFloor.WriteBlob(adapter, records);
        Assert.Equal(File.ReadAllBytes(output), File.ReadAllBytes(adapter));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public void RefusedStreamPreservesFileServingMapAndNovelEntries(string mode)
    {
        using var files = new Files();
        using var cancelled = new CancellationTokenSource();
        var records = Sorted(1, 15);
        string output = files.PathFor("floor.bin");
        ChessTransitionFloor.WriteBlob(output, records);
        ChessTransitionFloor.Load(output);
        var novel = Pair(1000);
        ChessTransitionFloor.Remember(novel.Key, novel.To);
        byte[] original = File.ReadAllBytes(output);
        IEnumerable<(Hash128 Key, Hash128 To)> Invalid()
        {
            if (mode == "reversed")
            {
                yield return records[1];
                yield return records[0];
                yield break;
            }
            yield return records[0];
            if (mode == "short") yield break;
            if (mode == "throw") throw new IOException("controlled input failure");
            if (mode == "cancel") cancelled.Cancel();
            yield return mode == "duplicate" ? records[0] : records[1];
            if (mode == "long") yield return records[1];
        }

        var failure = Record.Exception(() =>
            ChessTransitionFloor.WriteBlob(output, Invalid(), 2UL, cancelled.Token));
        if (mode == "cancel") Assert.IsType<OperationCanceledException>(failure);
        else if (mode == "throw") Assert.IsType<IOException>(failure);
        else Assert.IsType<ArgumentException>(failure);
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.True(ChessTransitionFloor.TryLookup(records[0].Key, out var actual));
        Assert.Equal(records[0].To, actual);
        Assert.True(ChessTransitionFloor.TryLookup(novel.Key, out actual));
        Assert.Equal(novel.To, actual);
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 63)]
    public void ImpossibleUlongCountRefusesBeforeEnumerationOrFilesystemChange(ulong count)
    {
        using var files = new Files();
        bool enumerated = false;
        IEnumerable<(Hash128 Key, Hash128 To)> Unexpected()
        {
            enumerated = true;
            yield return Pair(1);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChessTransitionFloor.WriteBlob(files.PathFor("absent.bin"), Unexpected(), count));
        Assert.False(enumerated);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public void ManyDiskRunsMergeBytewiseCollapseIdenticalPairsAndKeepOnlyTheFinalRun()
    {
        using var files = new Files();
        string work = files.PathFor("runs"), output = files.PathFor("floor.bin");
        // 106 occurrences with a three-record buffer create 36 initial runs.
        // Fan-in two requires six merge passes, including single-input tail groups.
        using (var builder = new ChessTransitionFloorBuilder(work, 3, 2, 16_384))
        {
            for (int repetition = 0; repetition < 2; repetition++)
                for (int i = 53; i >= 1; i--)
                {
                    var row = Pair(i);
                    builder.Add(row.Key, row.To);
                }
            var result = builder.Complete(output);
            Assert.Equal(53UL, result.Records);
            Assert.Equal(106UL, result.InputOccurrences);
            Assert.InRange(result.PeakSpillBytes, 106L * 32, 16_384L);
            Assert.Equal(ExpectedV1(Sorted(Enumerable.Range(1, 53).ToArray())), File.ReadAllBytes(output));
            string retained = Assert.Single(Directory.EnumerateFiles(work));
            Assert.Equal(53L * 32 + 32, new FileInfo(retained).Length);
            Assert.Throws<InvalidOperationException>(() => builder.Add(Pair(1).Key, Pair(1).To));
        }
        Assert.False(Directory.Exists(work));
        Assert.True(File.Exists(output));
    }

    [Theory]
    [InlineData(1)] // Equal keys enter different runs and conflict in the merge.
    [InlineData(4)] // Equal keys conflict when the first in-memory run is sorted.
    public void ConflictingResultsRefusePublicationAndFaultTheBuilder(int bufferRecords)
    {
        using var files = new Files();
        string output = files.PathFor("floor.bin"), work = files.PathFor("runs");
        var original = Sorted(10);
        ChessTransitionFloor.WriteBlob(output, original);
        ChessTransitionFloor.Load(output);
        var before = File.ReadAllBytes(output);
        using (var builder = new ChessTransitionFloorBuilder(work, bufferRecords, 2, 4096))
        {
            var row = Pair(1);
            builder.Add(row.Key, row.To);
            builder.Add(row.Key, Pair(2).To);
            Assert.Throws<InvalidOperationException>(() => builder.Complete(output));
            Assert.Throws<InvalidOperationException>(() => builder.Complete(output));
            Assert.Equal(before, File.ReadAllBytes(output));
            Assert.True(ChessTransitionFloor.TryLookup(original[0].Key, out var actual));
            Assert.Equal(original[0].To, actual);
        }
        Assert.False(Directory.Exists(work));
    }

    [Theory]
    [InlineData(144L, 2)] // Two checksummed input runs fit; another trailer would exceed the grant.
    [InlineData(175L, 1)] // A 64-byte checked run plus the final 112-byte v1 temporary does not fit.
    public void SpillGrantCoversConcurrentInputsMergeOutputAndFinalTemporary(long grant, int count)
    {
        using var files = new Files();
        string output = files.PathFor("floor.bin"), work = files.PathFor("runs");
        var original = ExpectedV1(Sorted(99));
        File.WriteAllBytes(output, original);
        using (var builder = new ChessTransitionFloorBuilder(work, 1, 2, grant))
        {
            for (int i = 1; i <= count; i++) builder.Add(Pair(i).Key, Pair(i).To);
            var error = Assert.Throws<IOException>(() => builder.Complete(output));
            Assert.Contains("grant", error.Message);
            Assert.True(Directory.EnumerateFiles(work).Sum(p => new FileInfo(p).Length) <= grant);
            Assert.Equal(original, File.ReadAllBytes(output));
        }
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public void ChangedRunResultCannotReceiveAFreshValidFinalChecksum()
    {
        using var files = new Files();
        string output = files.PathFor("floor.bin"), work = files.PathFor("runs");
        var original = Sorted(99);
        ChessTransitionFloor.WriteBlob(output, original);
        ChessTransitionFloor.Load(output);
        byte[] before = File.ReadAllBytes(output);
        using var builder = new ChessTransitionFloorBuilder(work, 1, 2, 4096);
        builder.Add(Pair(1).Key, Pair(1).To);
        builder.Add(Pair(2).Key, Pair(2).To);
        string run = Directory.EnumerateFiles(work).Order(StringComparer.Ordinal).First();
        byte[] intact = File.ReadAllBytes(run);
        byte[] corrupt = (byte[])intact.Clone();
        corrupt[16] ^= 1; // Only To changes: count, key order and file length still agree.
        File.WriteAllBytes(run, corrupt);
        var failure = Assert.Throws<InvalidDataException>(() => builder.Complete(output));
        Assert.Contains("SHA256", failure.Message);
        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.True(ChessTransitionFloor.TryLookup(original[0].Key, out var actual));
        Assert.Equal(original[0].To, actual);
    }

    [Fact]
    public void UnixPublicationFailurePreservesThePriorMappedInode()
    {
        if (OperatingSystem.IsWindows()) return; // Windows retains its existing release-before-rename path.
        using var files = new Files();
        string output = files.PathFor("floor.bin");
        var original = Sorted(99);
        ChessTransitionFloor.WriteBlob(output, original);
        ChessTransitionFloor.Load(output);
        IEnumerable<(Hash128 Key, Hash128 To)> ObstructPublication()
        {
            // Remove the path while its valid inode remains mapped, then make the
            // destination a directory. The stream is valid; only final rename fails.
            File.Delete(output);
            Directory.CreateDirectory(output);
            yield return Pair(1);
        }
        Assert.ThrowsAny<IOException>(() =>
            ChessTransitionFloor.WriteBlob(output, ObstructPublication(), 1UL));
        Assert.True(ChessTransitionFloor.IsLoaded);
        Assert.True(ChessTransitionFloor.TryLookup(original[0].Key, out var actual));
        Assert.Equal(original[0].To, actual);
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.tmp"));
    }

    [Fact]
    public void ExistingValidatedFloorMergesWithoutChangingTheServingMap()
    {
        using var files = new Files();
        string serving = files.PathFor("serving.bin"), source = files.PathFor("source.bin"),
            output = files.PathFor("floor.bin"), work = files.PathFor("runs");
        ChessTransitionFloor.WriteBlob(serving, Sorted(99));
        ChessTransitionFloor.Load(serving);
        ChessTransitionFloor.WriteBlob(source, Sorted(1, 2));
        using var builder = new ChessTransitionFloorBuilder(work, 1, 2, 4096);
        builder.AddBlob(source);
        builder.Add(Pair(2).Key, Pair(2).To);
        builder.Add(Pair(3).Key, Pair(3).To);
        var result = builder.Complete(output);
        Assert.Equal(3UL, result.Records);
        Assert.Equal(4UL, result.InputOccurrences);
        Assert.Equal(ExpectedV1(Sorted(1, 2, 3)), File.ReadAllBytes(output));
        Assert.Equal(1L, ChessTransitionFloor.RecordCount);
        Assert.True(ChessTransitionFloor.TryLookup(Pair(99).Key, out var retained));
        Assert.Equal(Pair(99).To, retained);
    }

    [Fact]
    public void CorruptImportedFloorRefusesAndCannotBeCompletedAsAnEmptySuccess()
    {
        using var files = new Files();
        string source = files.PathFor("bad.bin"), work = files.PathFor("runs"),
            output = files.PathFor("absent.bin");
        ChessTransitionFloor.WriteBlob(source, Sorted(1, 2));
        byte[] bytes = File.ReadAllBytes(source);
        bytes[64] ^= 1;
        File.WriteAllBytes(source, bytes);
        using var builder = new ChessTransitionFloorBuilder(work, 1, 2, 4096);
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddBlob(source));
        Assert.Contains("CRC mismatch", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Throws<InvalidOperationException>(() => builder.Complete(output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ImportedMappedBodyMutationDuringVisitCannotBeAccepted()
    {
        if (OperatingSystem.IsWindows()) return; // Windows denies the in-place writer sharing this map.
        using var files = new Files();
        string source = files.PathFor("source.bin"), serving = files.PathFor("serving.bin");
        var records = Sorted(1, 2);
        var retained = Pair(99);
        ChessTransitionFloor.WriteBlob(source, records);
        ChessTransitionFloor.WriteBlob(serving, [retained]);
        ChessTransitionFloor.Load(serving);
        int visited = 0;
        Hash128 lastObserved = default;
        var failure = Assert.Throws<InvalidDataException>(() =>
            ChessTransitionFloor.VisitEntries(source, (_, to) =>
            {
                lastObserved = to;
                if (++visited != 1) return;
                // Alter only the future record's result after initial checksum/order
                // validation. Length and key order remain valid; the mapped visit
                // sees the write, but must refuse before its caller can complete.
                using var mutation = new FileStream(source, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                mutation.Position = ChessTransitionFloor.HeaderSize + ChessTransitionFloor.RecordSize + 16;
                mutation.WriteByte((byte)(records[1].To.ToBytes()[0] ^ 1));
                mutation.Flush(flushToDisk: true);
            }));
        Assert.Contains("changed during", failure.Message);
        Assert.Equal(2, visited);
        Assert.NotEqual(records[1].To, lastObserved);
        Assert.True(ChessTransitionFloor.TryLookup(retained.Key, out var actual, out var kind));
        Assert.Equal(retained.To, actual);
        Assert.Equal(ChessTransitionFloor.LookupSource.Persistent, kind);
    }

    [Fact]
    public void EmptyBuildIsAValidV1FloorAndPrecancelledBuildCannotPublish()
    {
        using var files = new Files();
        string output = files.PathFor("empty.bin");
        using (var empty = new ChessTransitionFloorBuilder(files.PathFor("empty-runs"), 1, 2, 80))
        {
            var result = empty.Complete(output);
            Assert.Equal(new ChessTransitionFloorBuilder.Result(0, 0, 80), result);
            Assert.Equal(ExpectedV1([]), File.ReadAllBytes(output));
        }
        using var cancelled = new CancellationTokenSource();
        using var builder = new ChessTransitionFloorBuilder(files.PathFor("cancelled-runs"), 1, 2, 4096);
        builder.Add(Pair(1).Key, Pair(1).To);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => builder.Complete(output, cancelled.Token));
        Assert.Equal(ExpectedV1([]), File.ReadAllBytes(output));
        Assert.Throws<InvalidOperationException>(() => builder.Complete(output));
    }

    [Fact]
    public void WorkOwnershipAndFanInBoundsAreEnforcedBeforeGeneration()
    {
        using var files = new Files();
        Assert.Throws<IOException>(() => new ChessTransitionFloorBuilder(files.Root, 1, 2, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChessTransitionFloorBuilder(files.PathFor("bad-buffer"), 0, 2, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChessTransitionFloorBuilder(files.PathFor("bad-fanin"), 1, 33, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChessTransitionFloorBuilder(files.PathFor("one-way"), 1, 1, 4096));
        string work = files.PathFor("runs");
        using (var builder = new ChessTransitionFloorBuilder(work, 1, 2, 4096))
            Assert.Throws<ArgumentException>(() => builder.Complete(Path.Combine(work, "floor.bin")));
        Assert.False(Directory.Exists(work));
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")
            ?? throw new InvalidOperationException("TMPDIR must identify the permanent test workspace"),
            $"transition-builder-{Guid.NewGuid():N}");
        public Files() => Directory.CreateDirectory(Root);
        public string PathFor(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            ChessTransitionFloor.Unload();
            Directory.Delete(Root, recursive: true);
        }
    }
}
