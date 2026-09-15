using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Chess move/transition compose floor — (from_position, move) → to_position.
/// Deterministic ROM for state→state dedupe (operator law / GH #822 companion).
/// Not testimony. Not a ConcurrentDictionary presented as the ROM; the mmap blob is.
/// Process-lifetime novel hits accumulate in a side map so a run saturates like O(tier).
/// </summary>
public static unsafe class ChessTransitionFloor
{
    public enum LookupSource : byte { None, Persistent, Novel }

    public const uint Magic = 0x5448434Cu; // 'LCHT'
    public const uint Version = 1;
    public const int HeaderSize = 64;
    public const int RecordSize = 32; // key16 + to16
    public const int TrailerBytes = 16;

    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static byte* _base;
    private static long _len;
    private static long _count;
    private static string? _loadedPath;
    private static readonly ConcurrentDictionary<Hash128, Hash128> Novel = new();
    private static long _persistentHits;
    private static long _novelHits;
    private static long _lookupMisses;

    public readonly record struct Observation(bool IsLoaded, long RecordCount,
        int NovelCount, long PersistentHits, long NovelHits, long LookupMisses);

    /// <summary>Map state and completed managed lookup counters. Counters cover this
    /// process lifetime, including earlier mappings; observation never loads a file.</summary>
    public static Observation Observe() => new(IsLoaded, RecordCount, NovelCount,
        Interlocked.Read(ref _persistentHits), Interlocked.Read(ref _novelHits),
        Interlocked.Read(ref _lookupMisses));

    static ChessTransitionFloor()
    {
        // Release the mapping before the runtime tears itself down. Load() holds an
        // AcquirePointer for the life of the process and only ReleasePointer()s in
        // Unload(), which the compose path never calls — it loads the ROM once and keeps
        // it. That leaves the SafeMemoryMappedViewHandle to be finalized at exit with an
        // outstanding pointer reference, which is not a supported state to finalize from.
        // Defensive, not a diagnosed fix: the Chess.Tests host crash was traced to
        // unsynchronized Fathom first-touch (fixed in #849), NOT to this. Unload() is
        // idempotent, so running it here is safe whether or not a caller already did.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Unload();
    }

    /// <summary>
    /// Hash the complete mapped body through the existing native size_t interface.
    /// A mapping may exceed the length of one managed span; no prefix checksum or
    /// managed-sized copy is needed.
    /// </summary>
    private static Hash128 BodyHash(byte* data, long body)
    {
        Hash128 result;
        NativeInterop.Hash128Blake3(data, checked((nuint)body), &result);
        return result;
    }

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Unload();
        string fullPath = Path.GetFullPath(path);
        var fi = new FileInfo(fullPath);
        if (!fi.Exists || fi.Length < HeaderSize + TrailerBytes)
            throw new InvalidOperationException($"chess transition floor missing/short: {path}");

        try
        {
            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _len = _view.Capacity;
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _base = ptr;

            if (_len < HeaderSize + TrailerBytes)
                throw new InvalidOperationException("chess transition floor record layout mismatch");
            if (ReadU32(0) != Magic || ReadU32(4) != Version)
                throw new InvalidOperationException("bad chess transition floor magic/version");

            // Derive the envelope from mapped bytes before trusting the count. This
            // also rejects partial records and unchecksummed trailing data.
            long recordBytes = _len - HeaderSize - TrailerBytes;
            ulong count = ReadU64(8);
            if (recordBytes % RecordSize != 0 || count != (ulong)(recordBytes / RecordSize))
                throw new InvalidOperationException("chess transition floor record layout mismatch");
            long body = _len - TrailerBytes;
            if (BodyHash(_base, body) != *(Hash128*)(_base + body))
                throw new InvalidOperationException("chess transition floor body CRC mismatch");
            _count = checked((long)count);
            for (long i = 1; i < _count; i++)
            {
                var previous = (TransitionRec*)(_base + HeaderSize + (i - 1) * RecordSize);
                var current = previous + 1;
                if (Compare(previous->Key, current->Key) >= 0)
                    throw new InvalidOperationException("chess transition floor keys must be sorted and unique");
            }
            _loadedPath = fullPath;
        }
        catch
        {
            Unload();
            throw;
        }
    }

    public static void LoadDefault()
    {
        if (_base != null) return;
        string? path = Environment.GetEnvironmentVariable("LAPLACE_CHESS_TRANSITION_BIN");
        if (string.IsNullOrEmpty(path))
        {
            try
            {
                string t0 = CodepointPerfcache.ResolveDefaultPath();
                string? dir = Path.GetDirectoryName(t0);
                if (dir != null)
                {
                    string cand = Path.Combine(dir, "laplace_chess_transition_perfcache.bin");
                    if (File.Exists(cand)) path = cand;
                }
            }
            catch { /* optional */ }
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Load(path); }
        catch { /* optional until emit configured */ }
    }

    public static void Unload()
    {
        if (_view != null && _base != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _count = 0;
        _len = 0;
        _loadedPath = null;
        Novel.Clear();
    }

    public static bool IsLoaded => _base != null;
    public static long RecordCount => _count;
    public static int NovelCount => Novel.Count;

    public static bool TryLookup(Hash128 key, out Hash128 toId)
        => TryLookup(key, out toId, out _);

    /// <summary>
    /// Resolve one deterministic transition and report whether it came from the installed
    /// memory-mapped catalog or from a transition composed earlier in this process.  Keeping
    /// those two sources distinct makes the runtime receipt prove that the generated catalog
    /// is actually serving decisions instead of counting process-local saturation as a ROM hit.
    /// </summary>
    public static bool TryLookup(Hash128 key, out Hash128 toId, out LookupSource source)
    {
        if (Novel.TryGetValue(key, out toId))
        {
            source = LookupSource.Novel;
            Interlocked.Increment(ref _novelHits);
            return true;
        }
        toId = default;
        if (_base == null || _count == 0)
        {
            source = LookupSource.None;
            Interlocked.Increment(ref _lookupMisses);
            return false;
        }
        long lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            var rec = (TransitionRec*)(_base + HeaderSize + mid * RecordSize);
            int cmp = Compare(rec->Key, key);
            if (cmp == 0)
            {
                toId = rec->To;
                source = LookupSource.Persistent;
                Interlocked.Increment(ref _persistentHits);
                return true;
            }
            if (cmp < 0) lo = mid + 1;
            else
            {
                if (mid == 0) break;
                hi = mid - 1;
            }
        }
        source = LookupSource.None;
        Interlocked.Increment(ref _lookupMisses);
        return false;
    }

    /// <summary>Remember a novel transition for the rest of this process (run saturation).</summary>
    public static void Remember(Hash128 key, Hash128 toId) => Novel[key] = toId;

    public static void WriteBlob(string path, IReadOnlyList<(Hash128 Key, Hash128 To)> sortedUnique)
    {
        ArgumentNullException.ThrowIfNull(sortedUnique);
        for (int i = 1; i < sortedUnique.Count; i++)
            if (Compare(sortedUnique[i - 1].Key, sortedUnique[i].Key) >= 0)
                throw new ArgumentException("Chess transition keys must be sorted and unique.", nameof(sortedUnique));
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        // The catalog generator composes positions while building this floor. Compose
        // warmup loads an existing floor from the same perfcache directory, so an
        // incremental build reaches here with its own destination mmap'd. Release only
        // that mapping; an unrelated floor supplied by a test/caller is not ours to drop.
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (_loadedPath is not null && string.Equals(_loadedPath, fullPath, pathComparison))
            Unload();

        long count = sortedUnique.Count;
        long body = HeaderSize + count * RecordSize;
        long total = body + TrailerBytes;
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Publish only a complete, closed blob. Readers either retain the previous
            // inode or open the complete replacement; they can never mmap a truncated
            // FileMode.Create destination while generation is in progress. Unique temp
            // names also make two deterministic generators safe to run concurrently.
            using (var fs = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(total);
                using var mmf = MemoryMappedFile.CreateFromFile(fs, null, total,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None,
                    leaveOpen: true);
                using var view = mmf.CreateViewAccessor(0, total,
                    MemoryMappedFileAccess.ReadWrite);
                byte* ptr = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try
                {
                    *(uint*)ptr = Magic;
                    *(uint*)(ptr + 4) = Version;
                    *(ulong*)(ptr + 8) = (ulong)count;
                    for (int i = 0; i < sortedUnique.Count; i++)
                    {
                        var rec = (TransitionRec*)(ptr + HeaderSize + (long)i * RecordSize);
                        rec->Key = sortedUnique[i].Key;
                        rec->To = sortedUnique[i].To;
                    }
                    var crc = BodyHash(ptr, body);
                    *(Hash128*)(ptr + body) = crc;
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                view.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static uint ReadU32(long off) => *(uint*)(_base + off);
    private static ulong ReadU64(long off) => *(ulong*)(_base + off);

    private static int Compare(Hash128 a, Hash128 b) => a.CompareToBytewise(b);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TransitionRec
    {
        public Hash128 Key;
        public Hash128 To;
    }
}
