using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Blob-format pins for <see cref="ChessTransitionFloor"/>: write → load → lookup, and the
/// four ways a bad blob must refuse to load. The floor is a deterministic ROM the compose
/// path trusts without re-deriving, so a blob that loads is a blob whose every record is
/// believed — the failure modes matter as much as the happy path.
///
/// Serialised with the other perfcache tests: the floor is process-wide static state
/// (mmap handle + base pointer), so two tests loading at once would fight over it.
/// </summary>
[Collection("Perfcache")]
public sealed class ChessTransitionFloorTests
{
    private static Hash128 K(string s) => Hash128.OfCanonical("k:" + s);
    private static Hash128 V(string s) => Hash128.OfCanonical("v:" + s);

    /// <summary>WriteBlob demands sorted-unique input, in the same bytewise order the
    /// binary search assumes. Sorting here rather than hand-ordering literals keeps the
    /// test honest if Compare ever changes.</summary>
    private static List<(Hash128 Key, Hash128 To)> Pairs(params string[] names)
    {
        var list = names.Select(n => (Key: K(n), To: V(n))).ToList();
        list.Sort((a, b) => a.Key.CompareToBytewise(b.Key));
        return list;
    }

    private static string TempBlob() =>
        Path.Combine(Path.GetTempPath(), $"chess-transition-{Guid.NewGuid():N}.bin");

    [Fact]
    public void ObservationSeparatesParallelPersistentNovelAndMissingLookups()
    {
        ChessTransitionFloor.Unload();
        string path = TempBlob();
        var pairs = Pairs("persistent");
        try
        {
            ChessTransitionFloor.WriteBlob(path, pairs);
            Assert.False(ChessTransitionFloor.Observe().IsLoaded);
            ChessTransitionFloor.Load(path);
            ChessTransitionFloor.Remember(K("novel"), V("novel"));
            var before = ChessTransitionFloor.Observe();
            Assert.True(before.IsLoaded);
            Assert.Equal(1, before.RecordCount);
            Assert.Equal(1, before.NovelCount);
            Parallel.For(0, 64, iteration =>
            {
                Assert.True(ChessTransitionFloor.TryLookup(pairs[0].Key, out _, out var persistent));
                Assert.Equal(ChessTransitionFloor.LookupSource.Persistent, persistent);
                Assert.True(ChessTransitionFloor.TryLookup(K("novel"), out _, out var novel));
                Assert.Equal(ChessTransitionFloor.LookupSource.Novel, novel);
                Assert.False(ChessTransitionFloor.TryLookup(K("missing"), out _));
            });
            var after = ChessTransitionFloor.Observe();
            Assert.Equal(before.PersistentHits + 64, after.PersistentHits);
            Assert.Equal(before.NovelHits + 64, after.NovelHits);
            Assert.Equal(before.LookupMisses + 64, after.LookupMisses);
            ChessTransitionFloor.Unload();
            var unloaded = ChessTransitionFloor.Observe();
            Assert.False(unloaded.IsLoaded);
            Assert.Equal(0, unloaded.RecordCount);
            Assert.Equal(0, unloaded.NovelCount);
            Assert.Equal(after.PersistentHits, unloaded.PersistentHits);
            Assert.Equal(after.NovelHits, unloaded.NovelHits);
            Assert.Equal(after.LookupMisses, unloaded.LookupMisses);
        }
        finally { ChessTransitionFloor.Unload(); File.Delete(path); }
    }

    [Fact]
    public void Roundtrip_WrittenTransitionsLoadAndLookUp()
    {
        var pairs = Pairs("a", "b", "c", "d", "e", "f", "g");
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, pairs);
            ChessTransitionFloor.Load(path);

            Assert.True(ChessTransitionFloor.IsLoaded);
            Assert.Equal(pairs.Count, ChessTransitionFloor.RecordCount);

            // Every written key resolves to its own value — not merely "some hit".
            foreach (var (key, to) in pairs)
            {
                Assert.True(ChessTransitionFloor.TryLookup(key, out var got, out var source));
                Assert.Equal(to, got);
                Assert.Equal(ChessTransitionFloor.LookupSource.Persistent, source);
            }
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    public void Roundtrip_AbsentKeyMisses()
    {
        var pairs = Pairs("a", "b", "c");
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, pairs);
            ChessTransitionFloor.Load(path);

            // A miss must be a miss. The binary search walks lo/hi past both ends here,
            // which is where an off-by-one would surface as a false hit.
            Assert.False(ChessTransitionFloor.TryLookup(
                K("absent"), out var got, out var source));
            Assert.Equal(default, got);
            Assert.Equal(ChessTransitionFloor.LookupSource.None, source);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    public void RememberedTransitionIsReportedAsProcessLocalNotPersistentRom()
    {
        ChessTransitionFloor.Unload();
        var key = K("novel");
        var to = V("novel");
        try
        {
            ChessTransitionFloor.Remember(key, to);

            Assert.True(ChessTransitionFloor.TryLookup(key, out var got, out var source));
            Assert.Equal(to, got);
            Assert.Equal(ChessTransitionFloor.LookupSource.Novel, source);
        }
        finally
        {
            ChessTransitionFloor.Unload();
        }
    }

    [Fact]
    public void EmptyBlob_LoadsAndMissesEverything()
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, []);
            ChessTransitionFloor.Load(path);

            Assert.Equal(0, ChessTransitionFloor.RecordCount);
            Assert.True(ChessTransitionFloor.Observe().IsLoaded);
            Assert.Equal(0, ChessTransitionFloor.Observe().RecordCount);
            Assert.False(ChessTransitionFloor.TryLookup(K("a"), out _));
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteBlob_AtomicallyReplacesItsCurrentlyMappedDestination()
    {
        string path = TempBlob();
        var before = Pairs("before");
        var after = Pairs("after-a", "after-b");
        try
        {
            ChessTransitionFloor.WriteBlob(path, before);
            ChessTransitionFloor.Load(path);
            Assert.True(ChessTransitionFloor.TryLookup(before[0].Key, out _));

            // Incremental catalog builds compose before emitting. That warmup maps the
            // previous destination in this same process; publication must not truncate
            // or collide with its own live ROM.
            ChessTransitionFloor.WriteBlob(path, after);
            ChessTransitionFloor.Load(path);

            Assert.False(ChessTransitionFloor.TryLookup(before[0].Key, out _));
            foreach (var (key, to) in after)
            {
                Assert.True(ChessTransitionFloor.TryLookup(key, out var got));
                Assert.Equal(to, got);
            }
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.*.tmp"));
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    public void BadMagic_Refuses()
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            Corrupt(path, offset: 0, value: 0xDEADBEEFu);

            var ex = Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.Contains("magic/version", ex.Message, StringComparison.Ordinal);
            Assert.False(ChessTransitionFloor.IsLoaded);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Fact]
    public void BadVersion_Refuses()
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            Corrupt(path, offset: 4, value: ChessTransitionFloor.Version + 1);

            var ex = Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.Contains("magic/version", ex.Message, StringComparison.Ordinal);
            Assert.False(ChessTransitionFloor.IsLoaded);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1_000_000UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 63)]
    [InlineData((1UL << 59) + 2)]
    public void CountPastFileLength_RefusesBeforeReadingRecords(ulong count)
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            // Claim far more records than the file holds. This must be caught by the layout
            // check, NOT by walking off the mapping during the CRC or a lookup.
            Corrupt64(path, offset: 8, value: count);
            RewriteChecksum(path);

            var ex = Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.Contains("layout mismatch", ex.Message, StringComparison.Ordinal);
            Assert.False(ChessTransitionFloor.IsLoaded);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void TrailingBytesAreRejectedEvenWhenTheOriginalChecksumMatches(int count)
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            using (var file = new FileStream(path, FileMode.Append)) file.Write(new byte[count]);
            Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.False(ChessTransitionFloor.IsLoaded);
            Assert.Equal(0, ChessTransitionFloor.RecordCount);
        }
        finally { ChessTransitionFloor.Unload(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChecksummedUnsortedOrDuplicateRecordsAreRejected(bool duplicate)
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            var bytes = File.ReadAllBytes(path);
            int start = ChessTransitionFloor.HeaderSize, size = ChessTransitionFloor.RecordSize;
            var first = bytes.AsSpan(start, size).ToArray();
            if (!duplicate) bytes.AsSpan(start + size, size).CopyTo(bytes.AsSpan(start));
            first.CopyTo(bytes.AsSpan(start + size));
            File.WriteAllBytes(path, bytes);
            RewriteChecksum(path);
            var error = Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.Contains("sorted and unique", error.Message);
            Assert.False(ChessTransitionFloor.IsLoaded);
        }
        finally { ChessTransitionFloor.Unload(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidWriterInputPreservesThePublishedFileAndMapping(bool duplicate)
    {
        string path = TempBlob();
        try
        {
            var pairs = Pairs("a", "b");
            ChessTransitionFloor.WriteBlob(path, pairs);
            ChessTransitionFloor.Load(path);
            var original = File.ReadAllBytes(path);
            var invalid = duplicate ? new[] { pairs[0], pairs[0] } : new[] { pairs[1], pairs[0] };
            Assert.Throws<ArgumentException>(() => ChessTransitionFloor.WriteBlob(path, invalid));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(ChessTransitionFloor.TryLookup(pairs[0].Key, out var actual));
            Assert.Equal(pairs[0].To, actual);
        }
        finally { ChessTransitionFloor.Unload(); File.Delete(path); }
    }

    private static void RewriteChecksum(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Hash128.Blake3(bytes.AsSpan(0, bytes.Length - ChessTransitionFloor.TrailerBytes))
            .WriteBytes(bytes.AsSpan(bytes.Length - ChessTransitionFloor.TrailerBytes));
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void FlippedBodyByte_FailsCrc()
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b", "c"));
            // Flip one bit inside the first record. Header and trailer are untouched, so
            // only the body CRC can catch this.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(ChessTransitionFloor.HeaderSize, SeekOrigin.Begin);
                int b = fs.ReadByte();
                fs.Seek(ChessTransitionFloor.HeaderSize, SeekOrigin.Begin);
                fs.WriteByte((byte)(b ^ 0x01));
            }

            var ex = Assert.Throws<InvalidOperationException>(() => ChessTransitionFloor.Load(path));
            Assert.Contains("CRC mismatch", ex.Message, StringComparison.Ordinal);
            Assert.False(ChessTransitionFloor.IsLoaded);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
        }
    }

    private static void Corrupt(string path, long offset, uint value)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(value));
    }

    private static void Corrupt64(string path, long offset, ulong value)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(value));
    }
}
