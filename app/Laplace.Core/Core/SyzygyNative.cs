namespace Laplace.Engine.Core;

/// <summary>
/// Managed surface over the native Syzygy tablebase probe kernel
/// (<c>engine/core/src/syzygy.c</c>, the Laplace ABI over the vendored Fathom prober
/// at <c>external/fathom</c>, pinned c9c6fef0dddc05d2e242c183acf5833149ab676d, MIT).
/// A probe is a memory-mapped table lookup — in-process by design (compute at ingest).
/// State is process-global: one loaded table set at a time, like the perfcache blobs.
/// Bitboards are a1=bit0..h8=bit63; results are side-to-move POV; probes run with
/// rule50 = 0 (Laplace position identity excludes the halfmove clock — the lawful
/// per-position fact is the rule50-agnostic verdict) and never with castling rights
/// (not covered by tablebases; the caller skips such positions).
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
    /// Load (or re-load) the tablebase set under <paramref name="path"/>. Returns the
    /// largest man count the discovered tables cover (0 = directory holds no tables),
    /// or -1 when init failed.
    /// </summary>
    public static int Init(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        string normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // IDEMPOTENT, and it has to be here rather than in every caller. The mapping is
        // process-global, but Init is called from a test fixture, from the decomposer's
        // InitializeAsync and again from its prober-resolution path — none of which can
        // know whether another one already ran. Re-entering tb_init on an already-mapped
        // set is what corrupts Fathom's statics and lands a later probe in gen_captures.
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

    // Managed-side truth about what is mapped. Largest() cannot serve as the "is it safe to
    // probe" signal: it reads a native global that is not guaranteed zero before a successful
    // init, so a probe could pass a man-count check while nothing is mapped and fault inside
    // gen_captures. Only an Init that RETURNED a positive count proves tables exist. Volatile
    // so a probe on another thread observes the publish.
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
    /// Thread-safe once initialized.
    /// </summary>
    public static int ProbeWdl(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
    {
        if (!Probeable(white, black)) return -1;
        // SERIALIZED. The old doc on this type said "thread-safe once initialized"; the core
        // dump says otherwise. Fathom maps each material configuration's table file LAZILY, on
        // the first probe that needs it, and that first-touch is not synchronized. Concurrent
        // callers — SyzygyTableUnpack.ExtractMaterialAsync runs parallel workers — first-touch
        // the same configuration together, corrupt its descriptor, and the next probe faults
        // inside gen_captures. That is the whole crash: several threads were sitting in
        // SyzygyProbeWdl simultaneously in the dump.
        //
        // A probe is an mmap'd lookup, so the lock costs little once a configuration is mapped,
        // and this lane runs at ingest rather than on a read path. Correctness first; if this
        // ever measures as a bottleneck the fix is per-configuration first-touch locking inside
        // syzygy.c, not removing the guard here.
        lock (InitGate)
        {
            return NativeInterop.SyzygyProbeWdl(
                white, black, kings, queens, rooks, bishops, knights, pawns,
                ep, whiteToMove ? 1 : 0);
        }
    }

    // ONE gate for init, free and probe. Two separate locks left a hole: Free() could unmap
    // the tables while a probe was already inside native code holding a different lock. Every
    // entry point that touches the process-global mapping serializes on this.

    /// <summary>
    /// TRUE only when the loaded table set can actually answer for this many men.
    ///
    /// Fathom does not range-check. probe_wdl walks straight into probe_ab -> gen_captures,
    /// which indexes tables that were never mapped for this man count, and the process takes
    /// a SIGSEGV inside gen_captures — no managed exception, no stack, nothing to catch. It
    /// killed the test host after a varying number of passing tests, and on the ingest path
    /// it would take a multi-hour chess run down with no log line explaining why.
    ///
    /// Largest() is 0 when nothing loaded and otherwise the largest man count discovered on
    /// disk (5 for a 3-4-5 set). Anything above it has no table, which is not an error — it
    /// is simply a position the oracle has nothing to say about. -1 is already this API's
    /// "no answer", and an absent verdict is the correct outcome, not a crash.
    /// </summary>
    private static bool Probeable(ulong white, ulong black)
    {
        int largest = _mappedLargest;
        if (largest <= 0) return false;
        int men = System.Numerics.BitOperations.PopCount(white | black);
        // A tablebase position has at least two kings. Fewer than two occupied squares means
        // the caller handed us an empty or half-built board — which a man-count ceiling alone
        // waves through, since 0 <= largest. That is exactly how a board whose bitboards were
        // never populated reached Fathom and faulted in gen_captures.
        if (men < 2) return false;
        return men <= largest;
    }

    /// <summary>
    /// WDL + DTZ probe (needs DTZ tables). Returns null when the probe failed or the
    /// position is terminal (a terminal position needs no oracle). Serialized natively.
    /// </summary>
    public static (int Wdl, int Dtz)? ProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
    {
        if (!Probeable(white, black)) return null;
        lock (InitGate)   // same first-touch hazard as ProbeWdl
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
