using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Chess move/transition compose floor — (from_position, move) → to_position.
/// Deterministic ROM for state→state dedupe (operator law / GH #822 companion).
/// Not testimony. Not a ConcurrentDictionary presented as the ROM; the mmap blob is.
/// A bounded process-local derived cache reuses novel transitions; collisions evict cache entries only.
/// </summary>
public static unsafe class ChessTransitionFloor
{
    public enum LookupSource : byte { None, Persistent, Novel }

    public const uint Magic = 0x5448434Cu; // 'LCHT'
    public const uint Version = 1;
    public const int HeaderSize = 64;
    public const int RecordSize = 32; // key16 + to16
    public const int TrailerBytes = 16;

    // Publication is cold and serialized; lookup holds only a SafeHandle reference.
    private static readonly object Publication = new();
    private static State _state = new(null);
    public const int NovelCapacity = 65_536;
    private static long _novelEvictions;

    private sealed class Entry(Hash128 key, Hash128 value)
    {
        public readonly Hash128 Key = key;
        public readonly Hash128 Value = value;
    }

    private sealed class State(MappedFloor? map)
    {
        public readonly MappedFloor? Map = map;
        public readonly Entry?[] Novel = new Entry[NovelCapacity];
        public int Count;
    }

    private sealed class MappedFloor : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        public readonly byte* Base;
        public readonly long Count;
        public readonly string Path;

        public MappedFloor(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Path = System.IO.Path.GetFullPath(path);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            _file = MemoryMappedFile.CreateFromFile(source, null, 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            try
            {
                _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                Base = pointer;
                try
                {
                    long length = _view.Capacity;
                    if (length < HeaderSize + TrailerBytes)
                        throw new InvalidOperationException("chess transition floor record layout mismatch");
                    if (*(uint*)Base != Magic || *(uint*)(Base + 4) != Version)
                        throw new InvalidOperationException("bad chess transition floor magic/version");
                    long recordBytes = length - HeaderSize - TrailerBytes;
                    ulong count = *(ulong*)(Base + 8);
                    if (recordBytes % RecordSize != 0 || count != (ulong)(recordBytes / RecordSize))
                        throw new InvalidOperationException("chess transition floor record layout mismatch");
                    long body = length - TrailerBytes;
                    ct.ThrowIfCancellationRequested();
                    if (BodyHash(Base, body) != *(Hash128*)(Base + body))
                        throw new InvalidOperationException("chess transition floor body CRC mismatch");
                    ct.ThrowIfCancellationRequested();
                    Count = checked((long)count);
                    for (long i = 1; i < Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var previous = (TransitionRec*)(Base + HeaderSize + (i - 1) * RecordSize);
                        if (Compare(previous->Key, (previous + 1)->Key) >= 0)
                            throw new InvalidOperationException("chess transition floor keys must be sorted and unique");
                    }
                }
                catch
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _view.Dispose();
                    throw;
                }
            }
            catch { _view?.Dispose(); _file.Dispose(); throw; }
        }

        public bool TryAcquire()
        {
            bool acquired = false;
            try { _view.SafeMemoryMappedViewHandle.DangerousAddRef(ref acquired); }
            catch (ObjectDisposedException) { return false; }
            return acquired;
        }
        public void Release() => _view.SafeMemoryMappedViewHandle.DangerousRelease();
        public void Dispose()
        {
            // Existing reader references defer the actual unmap until their copies finish.
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }

        public bool Lookup(Hash128 key, out Hash128 value)
        {
            long lo = 0, hi = Count - 1;
            while (lo <= hi)
            {
                long mid = lo + ((hi - lo) >> 1);
                var record = (TransitionRec*)(Base + HeaderSize + mid * RecordSize);
                int comparison = Compare(record->Key, key);
                if (comparison == 0) { value = record->To; return true; }
                if (comparison < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            value = default;
            return false;
        }
    }

    private static long _persistentHits;
    private static long _novelHits;
    private static long _lookupMisses;

    public readonly record struct Observation(bool IsLoaded, long RecordCount,
        int NovelCount, long PersistentHits, long NovelHits, long LookupMisses,
        int NovelEntryCapacity = NovelCapacity, long NovelEvictions = 0);

    /// <summary>Map state and completed managed lookup counters. Counters cover this
    /// process lifetime, including earlier mappings; observation never loads a file.</summary>
    public static Observation Observe()
    {
        State state = Volatile.Read(ref _state);
        return new(state.Map is not null, state.Map?.Count ?? 0, Volatile.Read(ref state.Count),
            Interlocked.Read(ref _persistentHits), Interlocked.Read(ref _novelHits),
            Interlocked.Read(ref _lookupMisses), NovelCapacity, Interlocked.Read(ref _novelEvictions));
    }

    static ChessTransitionFloor()
        => AppDomain.CurrentDomain.ProcessExit += static (_, _) => Unload();

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
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < HeaderSize + TrailerBytes)
            throw new InvalidOperationException($"chess transition floor missing/short: {path}");
        // Fully validate privately. A rejected replacement must not discard a valid map.
        var candidate = new MappedFloor(path);
        State next;
        try { next = new State(candidate); }
        catch { candidate.Dispose(); throw; }
        lock (Publication)
        {
            State previous = Interlocked.Exchange(ref _state, next);
            previous.Map?.Dispose();
        }
    }

    public static void LoadDefault()
    {
        if (IsLoaded) return;
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
        lock (Publication)
        {
            State previous = Interlocked.Exchange(ref _state, new State(null));
            previous.Map?.Dispose();
        }
    }

    public static bool IsLoaded => Volatile.Read(ref _state).Map is not null;
    public static long RecordCount => Volatile.Read(ref _state).Map?.Count ?? 0;
    public static int NovelCount
    {
        get { State state = Volatile.Read(ref _state); return Volatile.Read(ref state.Count); }
    }

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
        while (true)
        {
            State state = Volatile.Read(ref _state);
            Entry? entry = Volatile.Read(ref state.Novel[Bucket(key)]);
            if (entry is not null && entry.Key == key)
            {
                toId = entry.Value;
                source = LookupSource.Novel;
                Interlocked.Increment(ref _novelHits);
                return true;
            }
            MappedFloor? map = state.Map;
            if (map is not null)
            {
                if (!map.TryAcquire()) continue; // The publication raced this acquisition.
                try
                {
                    if (map.Lookup(key, out toId))
                    {
                        source = LookupSource.Persistent;
                        Interlocked.Increment(ref _persistentHits);
                        return true;
                    }
                }
                finally { map.Release(); }
            }
            toId = default;
            source = LookupSource.None;
            Interlocked.Increment(ref _lookupMisses);
            return false;
        }
    }

    private static int Bucket(Hash128 key) => key.GetHashCode() & (NovelCapacity - 1);

    /// <summary>Reuse an actually composed deterministic transition in this process.
    /// The fixed-size derived cache may evict on collisions; misses recompute normally.
    /// No file, PostgreSQL record, testimony or historical provenance is created.</summary>
    public static void Remember(Hash128 key, Hash128 toId)
    {
        while (true)
        {
            State state = Volatile.Read(ref _state);
            MappedFloor? map = state.Map;
            if (map is not null)
            {
                if (!map.TryAcquire()) continue;
                try
                {
                    if (map.Lookup(key, out Hash128 persisted))
                    {
                        if (persisted != toId)
                            throw new InvalidOperationException("Derived transition conflicts with the mapped deterministic result.");
                        return;
                    }
                }
                finally { map.Release(); }
            }
            int index = Bucket(key);
            Entry? previous = Volatile.Read(ref state.Novel[index]);
            if (previous is not null && previous.Key == key)
            {
                if (previous.Value != toId)
                    throw new InvalidOperationException("Derived transition conflicts with an existing deterministic result.");
                return;
            }
            var entry = new Entry(key, toId);
            if (Interlocked.CompareExchange(ref state.Novel[index], entry, previous) != previous) continue;
            if (previous is null) Interlocked.Increment(ref state.Count);
            else Interlocked.Increment(ref _novelEvictions);
            return;
        }
    }

    /// <summary>Visit a privately validated v1 file without replacing the serving map.
    /// The mapping and its open-file identity remain owned for the whole visit.</summary>
    internal static void VisitEntries(string path, Action<Hash128, Hash128> visitor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        using var map = new MappedFloor(path, ct);
        for (long i = 0; i < map.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var record = (TransitionRec*)(map.Base + HeaderSize + i * RecordSize);
            visitor(record->Key, record->To);
        }
        ct.ThrowIfCancellationRequested();
    }

    public static void WriteBlob(string path, IReadOnlyList<(Hash128 Key, Hash128 To)> sortedUnique)
    {
        ArgumentNullException.ThrowIfNull(sortedUnique);
        WriteBlob(path, sortedUnique, (ulong)sortedUnique.Count);
    }

    /// <summary>Write one sorted, unique stream in the unchanged v1 layout. The declared
    /// count is checked against the actual single enumeration; invalid input or cancellation
    /// cannot publish a prefix or discard the currently serving map. No collection-sized
    /// allocation or int-sized checksum span is used.</summary>
    public static void WriteBlob(string path, IEnumerable<(Hash128 Key, Hash128 To)> sortedUnique,
        ulong recordCount, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sortedUnique);
        if (recordCount > (ulong)((long.MaxValue - HeaderSize - TrailerBytes) / RecordSize))
            throw new ArgumentOutOfRangeException(nameof(recordCount), "Transition blob exceeds the file-length envelope.");
        ct.ThrowIfCancellationRequested();
        long count = (long)recordCount;
        long body = checked(HeaderSize + count * RecordSize);
        long total = checked(body + TrailerBytes);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Unique same-directory temporary files preserve atomic replacement and the
            // exact zero-filled reserved header bytes of the existing serializer.
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
                    *(ulong*)(ptr + 8) = recordCount;
                    long i = 0;
                    Hash128 previous = default;
                    foreach (var (key, to) in sortedUnique)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (i == count)
                            throw new ArgumentException("Transition stream exceeds its declared count.", nameof(sortedUnique));
                        if (i != 0 && Compare(previous, key) >= 0)
                            throw new ArgumentException("Chess transition keys must be sorted and unique.", nameof(sortedUnique));
                        var record = (TransitionRec*)(ptr + HeaderSize + i * RecordSize);
                        record->Key = key;
                        record->To = to;
                        previous = key;
                        i++;
                    }
                    if (i != count)
                        throw new ArgumentException("Transition stream is shorter than its declared count.", nameof(sortedUnique));
                    ct.ThrowIfCancellationRequested();
                    var crc = BodyHash(ptr, body);
                    ct.ThrowIfCancellationRequested();
                    *(Hash128*)(ptr + body) = crc;
                }
                finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
                view.Flush();
                fs.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            lock (Publication)
            {
                // Compose warmup can map this generator's destination. Release only that
                // mapping, after the replacement is complete and all input has validated.
                State current = Volatile.Read(ref _state);
                bool replacingMapped = current.Map is not null
                    && string.Equals(current.Map.Path, fullPath, pathComparison);
                // Unix replacement preserves the old mapped inode. Rename first so a
                // publication failure cannot discard the valid serving generation.
                if (!OperatingSystem.IsWindows())
                {
                    File.Move(temporary, fullPath, overwrite: true);
                    if (replacingMapped)
                    {
                        Interlocked.Exchange(ref _state, new State(null));
                        current.Map!.Dispose();
                    }
                }
                else
                {
                    // Keep the existing Windows mapped-destination release contract.
                    if (replacingMapped)
                    {
                        Interlocked.Exchange(ref _state, new State(null));
                        current.Map!.Dispose();
                    }
                    File.Move(temporary, fullPath, overwrite: true);
                }
            }
        }
        finally { File.Delete(temporary); }
    }

    private static int Compare(Hash128 a, Hash128 b) => a.CompareToBytewise(b);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TransitionRec
    {
        public Hash128 Key;
        public Hash128 To;
    }
}
