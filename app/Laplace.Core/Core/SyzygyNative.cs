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
    public static int Init(string path) => NativeInterop.SyzygyInit(path);

    /// <summary>Release every table mapping. Idempotent.</summary>
    public static void Free() => NativeInterop.SyzygyFree();

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
        => NativeInterop.SyzygyProbeWdl(
            white, black, kings, queens, rooks, bishops, knights, pawns,
            ep, whiteToMove ? 1 : 0);

    /// <summary>
    /// WDL + DTZ probe (needs DTZ tables). Returns null when the probe failed or the
    /// position is terminal (a terminal position needs no oracle). Serialized natively.
    /// </summary>
    public static (int Wdl, int Dtz)? ProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove)
        => NativeInterop.SyzygyProbeRoot(
               white, black, kings, queens, rooks, bishops, knights, pawns,
               ep, whiteToMove ? 1 : 0, out int wdl, out int dtz) == 0
            ? (wdl, dtz)
            : null;
}
