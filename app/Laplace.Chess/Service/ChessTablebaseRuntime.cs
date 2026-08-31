using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>Process-wide runtime access to the same local Syzygy set used by ingest.</summary>
public static class ChessTablebaseRuntime
{
    private static readonly Lazy<int> Loaded = new(Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

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

    private static int Load()
    {
        var probe = ChessLabPaths.SyzygyDir;
        return probe.Found && probe.Path is { Length: > 0 }
            ? Math.Max(0, SyzygyNative.Init(probe.Path))
            : 0;
    }
}
