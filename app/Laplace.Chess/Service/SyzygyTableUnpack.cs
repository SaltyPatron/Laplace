using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// Opens Syzygy packaging: each <c>.rtbw</c> file is one material table (the package
/// unit). Fathom is the codec — thread-safe <c>tb_probe_wdl</c> for the WDL product,
/// locked <c>tb_probe_root</c> only when DTZ is needed (non-draws). Placement walks the
/// material's index space; there is no Fathom "dump table" API. Parallelism:
/// <see cref="DecomposerMultiFile{TRecord}"/> runs materials across file workers;
/// within a material, WDL probes fan out across workers.
/// </summary>
public static class SyzygyTableUnpack
{
    private static readonly int[] BoardSquares = BuildBoardSquares();

    private static int[] BuildBoardSquares()
    {
        var sq = new int[64];
        int n = 0;
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                sq[n++] = Board.Sq(f, r);
        return sq;
    }

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
    /// </summary>
    public static async IAsyncEnumerable<SyzygyProduct> ExtractMaterialAsync(
        string materialName, ISyzygyProber prober, int workers = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!TryParseMaterial(materialName, out var pieces))
            yield break;

        int degree = workers > 0
            ? workers
            : Math.Max(1, IngestTopology.Current.ComposeWorkers);
        var boardCh = Channel.CreateBounded<Board>(
            new BoundedChannelOptions(degree * 64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false,
            });
        var productCh = Channel.CreateBounded<SyzygyProduct>(
            new BoundedChannelOptions(degree * 64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true,
            });

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var board in EnumerateBoardsAsync(pieces, ct).ConfigureAwait(false))
                    await boardCh.Writer.WriteAsync(board, ct).ConfigureAwait(false);
            }
            finally { boardCh.Writer.TryComplete(); }
        }, ct);

        var consumers = new Task[degree];
        for (int w = 0; w < degree; w++)
        {
            consumers[w] = Task.Run(async () =>
            {
                var modality = new ChessModality();
                await foreach (var board in boardCh.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (prober.ProbeWdl(board) is not { } wdl) continue;
                    int dtz = 0;
                    if (wdl != SyzygyNative.Draw)
                    {
                        // Root probe is process-locked inside the native kernel.
                        if (prober.Probe(board) is not { } full) continue;
                        wdl = full.Wdl;
                        dtz = full.Dtz;
                    }

                    string surface = modality.StateKey(new ChessState(board));
                    await productCh.Writer.WriteAsync(
                        new SyzygyProduct(
                            surface, ChessCompose.PositionId(surface), wdl, dtz),
                        ct).ConfigureAwait(false);
                }
            }, ct);
        }

        var closeProducts = Task.Run(async () =>
        {
            try
            {
                await producer.ConfigureAwait(false);
                await Task.WhenAll(consumers).ConfigureAwait(false);
            }
            finally { productCh.Writer.TryComplete(); }
        }, ct);

        await foreach (var product in productCh.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return product;

        await closeProducts.ConfigureAwait(false);
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
            for (int i = 0; i < pieces.Length; i++)
                b.Squares[placed[i]] = pieces[i];

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
public readonly record struct SyzygyProduct(string Surface, Hash128 PositionId, int Wdl, int Dtz);
