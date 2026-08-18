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
                Assert.True(ChessTransitionFloor.TryLookup(key, out var got));
                Assert.Equal(to, got);
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
            Assert.False(ChessTransitionFloor.TryLookup(K("absent"), out var got));
            Assert.Equal(default, got);
        }
        finally
        {
            ChessTransitionFloor.Unload();
            File.Delete(path);
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

    [Fact]
    public void CountPastFileLength_RefusesBeforeReadingRecords()
    {
        string path = TempBlob();
        try
        {
            ChessTransitionFloor.WriteBlob(path, Pairs("a", "b"));
            // Claim far more records than the file holds. This must be caught by the layout
            // check, NOT by walking off the mapping during the CRC or a lookup.
            Corrupt64(path, offset: 8, value: 1_000_000UL);

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
