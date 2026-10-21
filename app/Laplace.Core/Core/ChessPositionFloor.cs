namespace Laplace.Engine.Core;

/// <summary>
/// Load and lookup of the game-position perfcache blob: a read-only map over finite typed
/// board/move atoms, bounded move objects, and catalog positions, derived from canonical
/// structure and separate from the codepoint perfcache. Mapped and read natively
/// (<c>chess_position_table_*</c>).
/// </summary>
public static unsafe class ChessPositionFloor
{
    // Native lookup owns a concurrent reader guard through the complete geometry copy.
    private static long _lookupHits;
    private static long _lookupMisses;
    private static int _missingNativeReported;
    private static int _missingFileReported;

    private static void MissingNative(EntryPointNotFoundException exception)
    {
        if (Interlocked.Exchange(ref _missingNativeReported, 1) == 0)
            System.Diagnostics.Trace.TraceWarning("Chess position cache native ABI is unavailable; lookups cannot use the cache. {0}", exception);
    }

    public readonly record struct Observation(
        bool IsLoaded, long RecordCount, long LookupHits, long LookupMisses);

    /// <summary>Actual native map state and completed managed lookup counters. Counters
    /// cover this process lifetime, including earlier mappings; observation never loads a file.</summary>
    public static Observation Observe()
    {
        lock (LaplaceCoreGate.Native)
        {
            ulong count = 0;
            bool loaded;
            try { loaded = NativeInterop.ChessPositionTableRecordCount(&count) == 0; }
            catch (EntryPointNotFoundException ex) { MissingNative(ex); loaded = false; }
            return new(loaded, checked((long)count),
                Interlocked.Read(ref _lookupHits), Interlocked.Read(ref _lookupMisses));
        }
    }

    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (LaplaceCoreGate.Native)
        {
            int rc = NativeInterop.ChessPositionTableLoad(path);
            if (rc != 0)
            {
                string why = rc switch
                {
                    -1 => "open/stat/mmap failure",
                    -2 => "bad magic / unsupported format version",
                    -3 => "record layout mismatch",
                    -4 => "body CRC mismatch",
                    _ => "unknown error",
                };
                throw new InvalidOperationException(
                    $"chess_position_table_load(\"{path}\") failed (rc={rc}): {why}");
            }
        }
    }

    public static void LoadDefault()
    {
        if (IsLoadedUnlockedSafe()) return;
        lock (LaplaceCoreGate.Native)
        {
            if (IsLoadedUnlockedSafe()) return;
            try
            {
                if (IsLoadedUnlocked()) return;
            }
            catch (EntryPointNotFoundException ex)
            {
                MissingNative(ex);
                // A native library without chess_position_table_* leaves the map unloaded.
                return;
            }
            string? path = Environment.GetEnvironmentVariable("LAPLACE_CHESS_PERFCACHE_BIN");
            if (string.IsNullOrEmpty(path))
                path = ResolveBesideT0();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                if (Interlocked.Exchange(ref _missingFileReported, 1) == 0)
                    System.Diagnostics.Trace.TraceWarning("Chess position cache file is unavailable at '{0}'; lookups cannot use the cache.", path ?? "(unresolved)");
                return;
            }
            try
            {
                Load(path); // A corrupt or incompatible file must not become an ordinary cache miss.
            }
            catch (EntryPointNotFoundException ex)
            {
                MissingNative(ex);
                // Native library lacks the load entry point: the map stays unloaded.
            }
        }
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
        {
            NativeInterop.ChessPositionTableUnload();
        }
    }

    public static bool IsLoaded => IsLoadedUnlockedSafe();

    public static long RecordCount
    {
        get
        {
            ulong n = 0;
            try { return NativeInterop.ChessPositionTableRecordCount(&n) == 0 ? (long)n : 0; }
            catch (EntryPointNotFoundException ex) { MissingNative(ex); return 0; }
        }
    }

    /// <summary>
    /// Concurrent native readers retain the immutable map through the complete copy.
    /// No global managed gate serializes parallel compose workers or exposes mmap pointers.
    /// </summary>
    public static bool TryLookup(Hash128 id, out double x, out double y, out double z, out double m,
        out Hilbert128 hb, out uint n, out byte tier)
    {
        x = y = z = m = 0;
        hb = default;
        n = 0;
        tier = 0;
        double* coord = stackalloc double[4];
        Hilbert128 hbLocal = default;
        uint nLocal = 0;
        byte tierLocal = 0;
        int result;
        try { result = NativeInterop.ChessPositionTableLookupGeom(&id, coord, &hbLocal, &nLocal, &tierLocal); }
        catch (EntryPointNotFoundException ex) { MissingNative(ex); result = -1; }
        if (result != 0)
        {
            Interlocked.Increment(ref _lookupMisses);
            return false;
        }
        x = coord[0]; y = coord[1]; z = coord[2]; m = coord[3];
        hb = hbLocal;
        n = nLocal;
        tier = tierLocal;
        Interlocked.Increment(ref _lookupHits);
        return true;
    }

    private static bool IsLoadedUnlocked()
        => NativeInterop.ChessPositionTableIsLoaded() != 0;

    private static bool IsLoadedUnlockedSafe()
    {
        try { return IsLoadedUnlocked(); }
        catch (EntryPointNotFoundException ex) { MissingNative(ex); return false; }
    }

    private static string? ResolveBesideT0()
    {
        try
        {
            string t0 = CodepointPerfcache.ResolveDefaultPath();
            string? dir = Path.GetDirectoryName(t0);
            if (dir is null) return null;
            string candidate = Path.Combine(dir, "laplace_chess_position_perfcache.bin");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Cannot resolve chess cache beside the Unicode cache. {0}", ex);
            return null;
        }
    }
}
