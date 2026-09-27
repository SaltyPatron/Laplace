namespace Laplace.Engine.Core;

/// <summary>
/// Managed surface over the native Syzygy tablebase probe kernel
/// (<c>engine/core/src/syzygy.c</c>, the Laplace ABI over the vendored Fathom prober
/// at <c>external/fathom</c>, pinned c9c6fef0dddc05d2e242c183acf5833149ab676d, MIT).
/// A probe is an in-process memory-mapped table lookup. State is process-global: one
/// loaded table set at a time. Bitboards are a1=bit0..h8=bit63; results are side-to-move
/// POV. Probes run with rule50 = 0 because position identity excludes the halfmove clock,
/// so the per-position verdict is rule50-agnostic; positions with castling rights are
/// outside tablebase coverage and callers do not probe them.
/// </summary>
public static class SyzygyNative
{
    /// <summary>WDL values, side-to-move POV, in Fathom's TB_LOSS..TB_WIN order.</summary>
    public const int Loss = 0;
    public const int BlessedLoss = 1;
    public const int Draw = 2;
    public const int CursedWin = 3;
    public const int Win = 4;

    /// <summary>
    /// Load the tablebase directories in <paramref name="path"/> (colon-separated on
    /// Unix, semicolon-separated on Windows, matching Fathom). Returns the
    /// largest man count the discovered tables cover (0 = directory holds no tables),
    /// or -1 when init failed.
    /// </summary>
    public static int Init(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        string normalized = NormalizeTablePath(path);
        if (normalized.Length == 0) return -1;
        // Idempotent for the same path: several callers may init without knowing whether
        // another already did, and re-entering tb_init on a mapped set corrupts Fathom's
        // statics. A different path while mapped is refused.
        lock (InitGate)
        {
            if (_mappedLargest > 0 && string.Equals(_mappedPath, normalized, StringComparison.Ordinal))
                return _mappedLargest;

            if (_mappedLargest > 0)
                throw new InvalidOperationException(
                    $"Syzygy is already initialized from '{_mappedPath}' and cannot be replaced "
                    + $"process-wide by '{normalized}'. Configure ingest and play to use one table set.");

            int n = NativeInterop.SyzygyInit(normalized);
            _mappedLargest = n > 0 ? n : 0;
            _mappedPath = n > 0 ? normalized : null;
            if (n <= 0) NativeInterop.SyzygyFree();
            return n;
        }
    }

    private static readonly object InitGate = new();
    private static string? _mappedPath;

    internal static string NormalizeTablePath(string path)
        => string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    // Managed record of what is mapped: set only from an Init that returned a positive man
    // count. Largest() reads a native global that is not guaranteed zero before a successful
    // init, so it is not the probe-safety signal. Volatile so other threads observe the publish.
    private static volatile int _mappedLargest;

    /// <summary>Release every table mapping. Idempotent.</summary>
    public static void Free()
    {
        lock (InitGate)
        {
            _mappedLargest = 0;
            _mappedPath = null;
            NativeInterop.SyzygyFree();
        }
    }

    /// <summary>Largest man count of the loaded set (0 when nothing is loaded).</summary>
    public static int Largest() => NativeInterop.SyzygyLargest();

    /// <summary>
    /// WDL-only probe. <paramref name="ep"/> is the en-passant square (0 = none).
    /// Returns 0..4 (<see cref="Loss"/>..<see cref="Win"/>) or -1 on failure.
    /// Serialized on the mapping gate.
    /// </summary>
    public static int ProbeWdl(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
    {
        if (!Probeable(white, black)) return -1;
        // Serialized: Fathom maps each material configuration's table file lazily on first
        // probe, and that first touch is unsynchronized, so concurrent first touches corrupt
        // the descriptor. Once mapped, a probe is an mmap lookup and the lock is cheap.
        lock (InitGate)
        {
            return NativeInterop.SyzygyProbeWdl(
                white, black, kings, queens, rooks, bishops, knights, pawns,
                ep, whiteToMove ? 1 : 0);
        }
    }

    // Init, Free, and every probe share InitGate, so Free cannot unmap tables while a probe
    // is inside native code.

    /// <summary>
    /// True only when the mapped table set covers this many men. Fathom does not
    /// range-check: probing a man count with no mapped table indexes unmapped memory and
    /// faults the process. A position beyond coverage yields no verdict (-1 / null), which
    /// is absence, not refutation.
    /// </summary>
    private static bool Probeable(ulong white, ulong black)
    {
        int largest = _mappedLargest;
        if (largest <= 0) return false;
        int men = System.Numerics.BitOperations.PopCount(white | black);
        // A tablebase position has at least two kings; fewer occupied squares is an
        // unpopulated board that the man-count ceiling alone would admit.
        if (men < 2) return false;
        return men <= largest;
    }

    /// <summary>
    /// WDL + DTZ probe (needs DTZ tables). Returns null when the probe failed or the
    /// position is terminal (a terminal position needs no oracle). Serialized on the mapping gate.
    /// </summary>
    public static (int Wdl, int Dtz)? ProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
    {
        if (!Probeable(white, black)) return null;
        lock (InitGate)   // lazy first-touch mapping, as in ProbeWdl
        {
            return NativeInterop.SyzygyProbeRoot(
                       white, black, kings, queens, rooks, bishops, knights, pawns,
                       ep, whiteToMove ? 1 : 0, out int wdl, out int dtz,
                       out _, out _, out _) == 0
                ? (wdl, dtz)
                : null;
        }
    }

    /// <summary>WDL/DTZ plus Fathom's optimal board transition.</summary>
    public static (int Wdl, int Dtz, int From, int To, int Promotes)? ProbeRootTransition(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
    {
        if (!Probeable(white, black)) return null;
        lock (InitGate)
        {
            return NativeInterop.SyzygyProbeRoot(
                       white, black, kings, queens, rooks, bishops, knights, pawns,
                       ep, whiteToMove ? 1 : 0, out int wdl, out int dtz,
                       out int from, out int to, out int promotes) == 0
                ? (wdl, dtz, from, to, promotes)
                : null;
        }
    }
}
