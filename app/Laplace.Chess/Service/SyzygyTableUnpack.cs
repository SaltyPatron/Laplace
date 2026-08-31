using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Opens Syzygy packaging: each <c>.rtbw</c> file is one material table (the package
/// unit). Fathom is the codec — thread-safe <c>tb_probe_wdl</c> for the WDL product,
/// locked <c>tb_probe_root</c> only when DTZ is needed (non-draws). Placement walks the
/// material's index space; there is no Fathom "dump table" API. Parallelism:
/// <see cref="DecomposerMultiFile{TRecord}"/> runs materials across file workers;
/// the decomposer invokes one stream per material, and concurrent materials share one
/// compose-worker-sized probe budget (the optional gate on
/// <see cref="ExtractMaterialAsync"/>) so file fan × probe fan never oversubscribes.
/// </summary>
public static class SyzygyTableUnpack
{
    private static readonly int[] BoardSquares = BuildBoardSquares();
    private static readonly ChessModality Modality = new();

    private static int[] BuildBoardSquares()
    {
        var sq = new int[64];
        int n = 0;
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                sq[n++] = Board.Sq(f, r);
        return sq;
    }

    /// <summary>
    /// Full material-graph enumeration limit, default 3. The decoder walks the raw
    /// placement space, which is factorial in men: 3-men is ~500k products/table
    /// (fine), 4-men ~30M/table (~10^9 across the 30 tables of a 3-4-5 set) and
    /// 5-men ~1.8×10^9/table (~10^11 across its 110 tables). Three-men tables form
    /// the complete terminal graph; larger table packages stay available to the
    /// same Fathom perfcache during full-depth search.
    /// </summary>
    public const int DefaultMaxMen = 3;

    /// <summary>
    /// The active ceiling: <c>LAPLACE_SYZYGY_MAX_MEN</c> (mirrors the
    /// <c>LAPLACE_SYZYGY</c> packaging knob; parse style per ChessShrink), else
    /// <see cref="DefaultMaxMen"/>. Values below 2 (no material has fewer men
    /// than the two kings) fall back to the default.
    /// </summary>
    public static int ResolveMaxMen() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN"),
            out int v) && v >= 2
            ? v
            : DefaultMaxMen;

    public static int ParseMen(string materialName)
        => TryParseMaterial(materialName, out var m) ? m.Length : int.MaxValue;

    public static bool TryParseMaterial(string name, out Piece[] pieces)
    {
        pieces = Array.Empty<Piece>();
        int v = name.IndexOf('v');
        if (v <= 0 || v >= name.Length - 1) return false;
        if (!TryParseSide(name.AsSpan(0, v), white: true, out var w)) return false;
        if (!TryParseSide(name.AsSpan(v + 1), white: false, out var b)) return false;
        pieces = new Piece[w.Count + b.Count];
        w.CopyTo(pieces, 0);
        b.CopyTo(pieces, w.Count);
        return pieces.Length >= 2;
    }

    private static bool TryParseSide(ReadOnlySpan<char> side, bool white, out List<Piece> pieces)
    {
        pieces = new List<Piece>(side.Length);
        if (side.Length < 1 || side[0] != 'K') return false;
        pieces.Add(white ? Piece.WKing : Piece.BKing);
        for (int i = 1; i < side.Length; i++)
        {
            Piece? p = side[i] switch
            {
                'Q' => white ? Piece.WQueen : Piece.BQueen,
                'R' => white ? Piece.WRook : Piece.BRook,
                'B' => white ? Piece.WBishop : Piece.BBishop,
                'N' => white ? Piece.WKnight : Piece.BKnight,
                'P' => white ? Piece.WPawn : Piece.BPawn,
                _ => null,
            };
            if (p is null) return false;
            pieces.Add(p.Value);
        }
        return true;
    }

    /// <summary>
    /// Extract every probeable product for one material table (one <c>.rtbw</c> basename).
    /// WDL probes run in parallel; DTZ uses the locked root probe only for non-draws.
    /// <paramref name="probeGate"/>, when supplied, is a probe budget SHARED with the other
    /// concurrently-unpacking materials: each probe holds one slot for its duration, so
    /// the total probe fan across materials never exceeds the gate's capacity, and the
    /// tail of a run (few materials still unpacking) widens into the released slots.
    /// </summary>
    public static async IAsyncEnumerable<SyzygyProduct> ExtractMaterialAsync(
        string materialName, ISyzygyProber prober, int workers = 0,
        SemaphoreSlim? probeGate = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!TryParseMaterial(materialName, out var pieces))
            yield break;

        int degree = workers > 0
            ? workers
            : Math.Max(1, IngestTopology.Current.ComposeWorkers);
        await foreach (var product in ParallelIngestWork.RunAsync(
                           EnumerateBoardsAsync(pieces, ct),
                           degree,
                           (board, token) => ProbeBoardAsync(board, prober, probeGate, token),
                           ct).ConfigureAwait(false))
            yield return product;
    }

    private static async IAsyncEnumerable<SyzygyProduct> ProbeBoardAsync(
        Board board,
        ISyzygyProber prober,
        SemaphoreSlim? probeGate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SyzygyProduct? product;
        if (probeGate is null)
        {
            product = ProbeBoard(board, prober);
        }
        else
        {
            // Hold the slot for the probe only — released BEFORE the yield so output
            // backpressure never parks a slot of the shared budget.
            await probeGate.WaitAsync(ct).ConfigureAwait(false);
            try { product = ProbeBoard(board, prober); }
            finally { probeGate.Release(); }
        }
        if (product is { } p) yield return p;
    }

    private static SyzygyProduct? ProbeBoard(Board board, ISyzygyProber prober)
    {
        if (prober.ProbeWdl(board) is not { }) return null;
        // Root probe supplies the optimal deterministic transition for wins, draws and losses.
        // WDL alone cannot compose an endgame trajectory.
        if (prober.Probe(board) is not { } full) return null;

        string surface = Modality.StateKey(new ChessState(board));
        return new SyzygyProduct(
            surface, ChessCompose.PositionId(surface), full.Wdl, full.Dtz,
            full.From, full.To, full.Promotes);
    }

    internal static async IAsyncEnumerable<Board> EnumerateBoardsAsync(
        Piece[] pieces, [EnumeratorCancellation] CancellationToken ct)
    {
        var placed = new int[pieces.Length];
        var used = new bool[64];
        await foreach (var board in PlaceAsync(pieces, 0, placed, used, ct).ConfigureAwait(false))
            yield return board;
    }

    private static async IAsyncEnumerable<Board> PlaceAsync(
        Piece[] pieces, int index, int[] placed, bool[] used,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (index >= pieces.Length)
        {
            foreach (var board in FinishBothSides(pieces, placed))
                yield return board;
            yield break;
        }

        int start = 0;
        if (index > 0 && pieces[index] == pieces[index - 1])
            start = IndexOfSquare(placed[index - 1]) + 1;

        for (int si = start; si < 64; si++)
        {
            ct.ThrowIfCancellationRequested();
            if (used[si]) continue;
            int sq = BoardSquares[si];
            if (!SquareOkFor(pieces[index], sq)) continue;
            used[si] = true;
            placed[index] = sq;
            await foreach (var b in PlaceAsync(pieces, index + 1, placed, used, ct).ConfigureAwait(false))
                yield return b;
            used[si] = false;
            if ((si & 63) == 0) await Task.Yield();
        }
    }

    private static IEnumerable<Board> FinishBothSides(Piece[] pieces, int[] placed)
    {
        int wk = -1, bk = -1;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (pieces[i] == Piece.WKing) wk = placed[i];
            else if (pieces[i] == Piece.BKing) bk = placed[i];
        }
        if (wk < 0 || bk < 0 || KingsAdjacent(wk, bk)) yield break;

        foreach (bool whiteToMove in new[] { true, false })
        {
            var b = new Board
            {
                WhiteToMove = whiteToMove,
                Castle = CastleRights.None,
                EpSquare = -1,
                HalfmoveClock = 0,
                FullmoveNumber = 1,
            };
            // Set(), NOT a raw Squares[] write. Board maintains incremental bitboards and
            // Set is what keeps them in step; assigning Squares directly leaves _bb all
            // zeroes. Two things then break silently: MoveGen.InCheck is bitboard-driven, so
            // the legality filter below stops rejecting anything, and SyzygyNativeProber
            // hands Fathom the zero bitboards — an empty board with no kings. A man-count
            // guard cannot catch that (popcount 0 passes any limit) and the probe faults
            // inside gen_captures. Regression from the bitboard conversion, which converted
            // MoveApply's write sites and missed this one.
            for (int i = 0; i < pieces.Length; i++)
                b.Set(placed[i], pieces[i]);

            if (MoveGen.InCheck(b, !whiteToMove)) continue;
            yield return b;
        }
    }

    private static bool SquareOkFor(Piece p, int sq)
    {
        if (Board.TypeOf(p) != Piece.WPawn) return true;
        int r = Board.RankOf(sq);
        return r is >= 1 and <= 6;
    }

    private static bool KingsAdjacent(int a, int b)
    {
        int df = Math.Abs(Board.FileOf(a) - Board.FileOf(b));
        int dr = Math.Abs(Board.RankOf(a) - Board.RankOf(b));
        return df <= 1 && dr <= 1;
    }

    private static int IndexOfSquare(int sq)
    {
        for (int i = 0; i < 64; i++)
            if (BoardSquares[i] == sq) return i;
        return -1;
    }
}

/// <summary>Raw Syzygy product after packaging unpack — one board state's oracle facts.</summary>
public readonly record struct SyzygyProduct(
    string Surface, Hash128 PositionId, int Wdl, int Dtz,
    int From = -1, int To = -1, int Promotes = 0);
