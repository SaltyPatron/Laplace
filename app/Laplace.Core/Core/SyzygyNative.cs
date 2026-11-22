namespace Laplace.Engine.Core;

public static class SyzygyNative
{
    public const int Loss = 0;
    public const int BlessedLoss = 1;
    public const int Draw = 2;
    public const int CursedWin = 3;
    public const int Win = 4;

    private static string? _mappedPath;

    public static int Init(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        string normalized = NormalizeTablePath(path);
        if (normalized.Length == 0) return -1;
        _mappedPath = normalized;
        // Process-level tablebase probing is performed natively in C via lp_engine_chess_play.
        return 0;
    }

    internal static string NormalizeTablePath(string path)
        => string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    public static void Free() => _mappedPath = null;
    public static int Largest() => 0;

    public static int ProbeWdl(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => -1;

    public static (int Wdl, int Dtz)? ProbeRoot(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => null;

    public static (int Wdl, int Dtz, int From, int To, int Promotes)? ProbeRootTransition(
        ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
        ulong bishops, ulong knights, ulong pawns, uint ep, bool whiteToMove) => null;
}
