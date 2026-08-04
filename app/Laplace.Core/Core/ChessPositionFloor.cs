namespace Laplace.Engine.Core;

/// <summary>
/// App load/lookup for the chess compose-floor blob (GH #822 / spec 33):
/// tier-1 piece×square vocab + catalog tier-2 positions. Tier 0 remains codepoints.
/// Native mmap only (<c>chess_position_table_*</c>). Not a managed catalog walker.
/// </summary>
public static unsafe class ChessPositionFloor
{
    // mmap ROM after load: concurrent readers are safe. Only load/unload take the gate.
    private static volatile bool _ready;

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
            _ready = true;
        }
    }

    public static void LoadDefault()
    {
        if (_ready) return;
        lock (LaplaceCoreGate.Native)
        {
            if (_ready) return;
            try
            {
                if (IsLoadedUnlocked()) { _ready = true; return; }
            }
            catch (EntryPointNotFoundException)
            {
                // Stale liblaplace_core without chess_position_table_* — floor optional.
                return;
            }
            string? path = Environment.GetEnvironmentVariable("LAPLACE_CHESS_PERFCACHE_BIN");
            if (string.IsNullOrEmpty(path))
                path = ResolveBesideT0();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return; // optional until catalog emit is configured
            try
            {
                int rc = NativeInterop.ChessPositionTableLoad(path);
                if (rc == 0) _ready = true;
            }
            catch (EntryPointNotFoundException)
            {
                // Same stale-lib case after path resolved.
            }
        }
    }

    public static void Unload()
    {
        lock (LaplaceCoreGate.Native)
        {
            NativeInterop.ChessPositionTableUnload();
            _ready = false;
        }
    }

    public static bool IsLoaded => _ready || IsLoadedUnlockedSafe();

    public static long RecordCount
    {
        get
        {
            if (!_ready && !IsLoadedUnlockedSafe()) return 0;
            ulong n = 0;
            return NativeInterop.ChessPositionTableRecordCount(&n) == 0 ? (long)n : 0;
        }
    }

    /// <summary>
    /// Lock-free after load. The blob is an immutable mmap; taking
    /// <see cref="LaplaceCoreGate.Native"/> here serialized every parallel compose worker.
    /// </summary>
    public static bool TryLookup(Hash128 id, out double x, out double y, out double z, out double m,
        out Hilbert128 hb, out uint n, out byte tier)
    {
        x = y = z = m = 0;
        hb = default;
        n = 0;
        tier = 0;
        if (!_ready && !IsLoadedUnlockedSafe()) return false;
        double* coord = stackalloc double[4];
        Hilbert128 hbLocal;
        uint nLocal;
        byte tierLocal;
        if (NativeInterop.ChessPositionTableLookupGeom(
                &id, coord, &hbLocal, &nLocal, &tierLocal) != 0)
            return false;
        x = coord[0]; y = coord[1]; z = coord[2]; m = coord[3];
        hb = hbLocal;
        n = nLocal;
        tier = tierLocal;
        return true;
    }

    private static bool IsLoadedUnlocked()
        => NativeInterop.ChessPositionTableIsLoaded() != 0;

    private static bool IsLoadedUnlockedSafe()
    {
        try { return IsLoadedUnlocked(); }
        catch (EntryPointNotFoundException) { return false; }
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
        catch
        {
            return null;
        }
    }
}
