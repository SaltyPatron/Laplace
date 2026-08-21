using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

public readonly record struct LearnedSquare(char Piece, int File, int Rank, double DevPoints, double Witness);

public static class LearnedPst
{
    public const string WhitePieces = "PNBRQK";

    /// <summary>
    /// How much testimony chess.learned_moves reads. The statistic it returns is bounded --
    /// the corpus move vocabulary is 7,797 tier-2 move entities -- and MEASURED 2026-08-21,
    /// 20,000 games already surface 7,723 of them, so the table saturates almost immediately
    /// and a bigger sample buys precision on the tail, not coverage.
    /// </summary>
    public const int DefaultGameSample = 20_000;

    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds)
        => ReadWhite(ds, DefaultGameSample);

    /// <summary>
    /// The learned piece-square table, projected from the move-keyed statistic.
    ///
    /// A piece-square cell is a DOT PRODUCT -- "a pawn on e4" has to be summed over every way
    /// of arriving there (e2e4, e3e4, d3xe4...). A move is a LOOKUP. So chess.learned_moves
    /// owns the primary object and this reduces it to the 384 PeSTO-shaped cells its consumers
    /// expect: each move contributes its mover-relative score, weighted by how often it was
    /// played, to the square it ARRIVES on, with black mirroring onto the white-relative table.
    ///
    /// No board is reconstructed and no legal move is generated. The move's own five atoms say
    /// which piece and which square (ChessPositionIdentity.MoveAtomIndex), and the game already
    /// happened, so there is nothing to search for.
    ///
    /// This previously asked laplace.consensus for an OUTCOME edge on each of 384 piece-square
    /// atoms. Measured: zero consensus rows and zero attestations carry a piece-square subject,
    /// and they cannot -- positions along corpus lines are not materialized (1,033 stored
    /// against 1.6M games). The table was all zeros, which BlendPeStoWith turns into plain
    /// PeSTO for the UCI engine, ChessEngineService, the live host and learned-eval-test alike.
    /// </summary>
    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds, int gameSample)
    {
        ArgumentNullException.ThrowIfNull(ds);

        var rows = NpgsqlSubstrateReads
            .ChessLearnedMovesAsync(ds, gameSample, CancellationToken.None)
            .GetAwaiter().GetResult();

        var sum = new double[6][]; var wt = new double[6][];
        for (int t = 0; t < 6; t++) { sum[t] = new double[64]; wt[t] = new double[64]; }

        var index = ChessPositionIdentity.MoveAtomIndex;
        foreach (var row in rows)
        {
            int piece = -1, to = -1;
            foreach (var atom in row.Atoms)
            {
                if (!index.TryGetValue(Hash128.FromBytes(atom), out var d)) continue;
                if (d.Domain == ChessPositionIdentity.MovePieceDomain) piece = d.Value;
                else if (d.Domain == ChessPositionIdentity.MoveToDomain) to = d.Value;
            }
            if (piece is < 0 or > 11 || to < 0) continue;

            // PieceOrdinal is white 0-5 then black 6-11; a black move mirrors onto the
            // white-relative table, and mover_score is already from the mover's side.
            int type = piece % 6;
            bool white = piece < 6;
            int file = to & 7, rank = to >> 3;
            int idx = (white ? rank : 7 - rank) * 8 + file;
            sum[type][idx] += row.MoverScore * row.Plays;
            wt[type][idx] += row.Plays;
        }

        var outv = new LearnedSquare[WhitePieces.Length * 64];
        int k = 0;
        for (int t = 0; t < 6; t++)
        {
            // Centre within the piece type: a uniform shift is the corpus draw rate showing
            // through and carries no square preference. BuildTables centres again, which on an
            // already-centred table is a no-op, and the endpoint renders a deviation rather
            // than a raw win share.
            double gs = 0, gc = 0;
            for (int sq = 0; sq < 64; sq++) { gs += sum[t][sq]; gc += wt[t][sq]; }
            double mean = gc > 0 ? gs / gc : 0;
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    int sq = rank * 8 + file;
                    double w = wt[t][sq];
                    double share = w > 0 ? sum[t][sq] / w : mean;
                    // Percentage points of score. The retired column was a Glicko rating
                    // deviation and scaleCpPerPoint's 6.0 default was calibrated against that
                    // magnitude; score-share points land in the same range.
                    outv[k++] = new LearnedSquare(
                        WhitePieces[t], file, rank, (share - mean) * 100.0, w);
                }
        }
        return outv;
    }

    // The learned overlay must stay a positional nudge on top of PeSTO, never a material-scale
    // force: raw eff_mu deviations from a draw-heavy or thin fold run to hundreds of points per
    // square, which summed over a position crosses the MATE threshold and breaks the search
    // outright (observed: depth-1 "mate -94" from the openings-only fold). Three guards:
    // witness-shrink toward zero, mean-centering per piece type (a uniform shift is the fold's
    // prior showing through — it carries no square preference, only distorts material trades),
    // and a hard per-square clamp at bishop-pair scale.
    private const int ClampCp = 75;

    public static (int[][] Mg, int[][] Eg) BuildTables(NpgsqlDataSource ds, double scaleCpPerPoint = 6.0)
    {
        var learned = ReadWhite(ds);
        var raw = new double[6][];
        for (int t = 0; t < 6; t++) raw[t] = new double[64];
        foreach (var s in learned)
        {
            int t = WhitePieces.IndexOf(s.Piece);
            if (t < 0) continue;
            int idx = (7 - s.Rank) * 8 + s.File;
            double shrink = s.Witness / (s.Witness + ChessShrink.DefaultK0);
            raw[t][idx] = s.DevPoints * shrink * scaleCpPerPoint;
        }

        var mg = new int[6][]; var eg = new int[6][];
        for (int t = 0; t < 6; t++)
        {
            mg[t] = new int[64]; eg[t] = new int[64];
            double mean = raw[t].Average();
            for (int idx = 0; idx < 64; idx++)
            {
                int cp = Math.Clamp((int)Math.Round(raw[t][idx] - mean), -ClampCp, ClampCp);
                mg[t][idx] = cp; eg[t][idx] = cp;
            }
        }
        return (mg, eg);
    }

}
