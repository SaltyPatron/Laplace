using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>Process-wide runtime access to the same local Syzygy set used by ingest.</summary>
public static class ChessTablebaseRuntime
{
    private static readonly Lazy<int> Loaded = new(Load,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static string? _testTableSet;
    private static string? _loadedTableSet;

    public static int Largest => Math.Min(Loaded.Value, Math.Max(0, SyzygyNative.Largest()));

    public static int? ProbeWdl(Board board)
    {
        int largest = Largest;
        if (largest <= 0 || board.Castle != CastleRights.None
            || ChessSyzygy.MenCount(board) > largest)
            return null;
        var bb = ChessSyzygy.ToBitboards(board);
        int result = SyzygyNative.ProbeWdl(
            bb.White, bb.Black, bb.Kings, bb.Queens, bb.Rooks,
            bb.Bishops, bb.Knights, bb.Pawns, bb.Ep, board.WhiteToMove);
        return result < 0 ? null : result;
    }

    public static SearchTablebaseVerdict? ProbeSearch(Board board)
    {
        if (Prober?.Probe(board) is not { } verdict) return null;
        return new SearchTablebaseVerdict(verdict.Wdl, verdict.Dtz);
    }

    public static ISyzygyProber? Prober => Largest > 0 ? new SyzygyNativeProber() : null;

    /// <summary>
    /// Select one deterministic table set for an in-process test host before first use.
    /// This is intentionally internal: deployed processes retain ChessLabPaths as the sole
    /// configured/data-root authority, and child processes do not inherit this selection.
    /// </summary>
    internal static void ConfigureTestTableSet(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Loaded.IsValueCreated)
            throw new InvalidOperationException(
                "The Syzygy runtime was already initialized before test authority was selected.");
        _testTableSet = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string? LoadedTableSetForTest => _loadedTableSet;

    private static int Load()
    {
        if (_testTableSet is { Length: > 0 } testTableSet)
        {
            int loaded = Math.Max(0, SyzygyNative.Init(testTableSet));
            if (loaded > 0) _loadedTableSet = testTableSet;
            return loaded;
        }

        var probe = ChessLabPaths.SyzygyDir;
        if (!probe.Found || probe.Path is not { Length: > 0 } path) return 0;
        int largest = Math.Max(0, SyzygyNative.Init(path));
        if (largest > 0)
            _loadedTableSet = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return largest;
    }
}
