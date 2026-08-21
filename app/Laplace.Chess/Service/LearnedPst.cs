using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public readonly record struct LearnedSquare(char Piece, int File, int Rank, double DevPoints, double Witness);

public static class LearnedPst
{
    public const string WhitePieces = "PNBRQK";

    private static Piece WhitePiece(char c) => c switch
    {
        'P' => Piece.WPawn,
        'N' => Piece.WKnight,
        'B' => Piece.WBishop,
        'R' => Piece.WRook,
        'Q' => Piece.WQueen,
        'K' => Piece.WKing,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "not a white piece letter"),
    };

    /// <summary>
    /// How many games the fold reads. The learned table is a BOUNDED reusable statistic --
    /// 384 cells -- derived from unbounded testimony, so the cost knob is how much testimony
    /// to read, never how many rows to write.
    /// </summary>
    /// MEASURED 2026-08-21 on this host: ~73 games/s, the cost being ChessCompose.MoveId
    /// across ~35 legal actions per ply to resolve each typed move id. 5,000 games is ~70s
    /// once per process and already fills 366 of 384 cells with coherent signal (advanced
    /// pawns positive, back-rank knights negative). Raise it via the overload when an
    /// operator wants a tighter table and can pay for it.
    public const int DefaultGameSample = 5_000;

    /// <summary>
    /// The learned piece-square table, folded from witnessed games.
    ///
    /// VALIDATED 2026-08-21 against the live substrate. The previous implementation asked
    /// laplace.consensus for an OUTCOME edge on each of 384 piece-square atoms. Measured:
    /// zero consensus rows and zero attestations carry a piece-square subject, and the
    /// start position's own 35 substructures are not entities at all -- because positions
    /// are NOT stored as rows. A game is one ordered trajectory; its constituents live
    /// packed in the physicality, indexed by physicalities_constituents_gin. Asking for
    /// exploded per-square edges asks for the exact rows this design exists to not write,
    /// so the table was all zeros, which BlendPeStoWith turns into plain PeSTO for the UCI
    /// engine, ChessEngineService, the live host and learned-eval-test alike.
    ///
    /// The fold reads what is actually stored: a game's HAS_RESULT and its typed move
    /// trajectory. Replay is deterministic (ChessReplay, the same surface the PGN and
    /// movetext paths use), so every board along the line is recovered without storing one.
    /// Each occupied square accumulates the mover-relative score; black squares mirror onto
    /// the white-relative table, which is what a piece-square table means.
    /// </summary>
    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds)
        => ReadWhite(ds, DefaultGameSample);

    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds, int gameSample)
    {
        ArgumentNullException.ThrowIfNull(ds);

        // Phase-weighted accumulators, white-relative: [pieceType 0..5][square 0..63].
        var sumMg = new double[6][]; var cntMg = new double[6][];
        var sumEg = new double[6][]; var cntEg = new double[6][];
        for (int t = 0; t < 6; t++)
        {
            sumMg[t] = new double[64]; cntMg[t] = new double[64];
            sumEg[t] = new double[64]; cntEg[t] = new double[64];
        }

        foreach (var (resultToken, moveIds) in ReadWitnessedLines(ds, gameSample))
        {
            double whiteScore = resultToken switch
            {
                "1-0" => 1.0,
                "0-1" => 0.0,
                "1/2-1/2" => 0.5,
                _ => double.NaN,
            };
            if (double.IsNaN(whiteScore)) continue;

            // Boards, not strings: ForEachBoard skips the SAN/FEN/position-id generation
            // this fold has no use for. A line that does not resolve is dropped whole
            // rather than folded halfway.
            ChessReplay.ForEachBoard(moveIds, b => Accumulate(b, whiteScore));
        }

        var outv = new LearnedSquare[WhitePieces.Length * 64];
        int k = 0;
        for (int t = 0; t < 6; t++)
        {
            // Centre within the piece type: a uniform shift is the corpus draw rate showing
            // through and carries no square preference. BuildTables centres again; on an
            // already-centred table that is a no-op, and the endpoint gets a deviation
            // rather than a raw win share, which is what the grid renders.
            double gs = 0, gc = 0;
            for (int sq = 0; sq < 64; sq++) { gs += sumMg[t][sq] + sumEg[t][sq]; gc += cntMg[t][sq] + cntEg[t][sq]; }
            double mean = gc > 0 ? gs / gc : 0;

            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    int sq = rank * 8 + file;
                    double w = cntMg[t][sq] + cntEg[t][sq];
                    double share = w > 0 ? (sumMg[t][sq] + sumEg[t][sq]) / w : mean;
                    // Percentage points of score. The old column was a Glicko rating
                    // deviation; scaleCpPerPoint's 6.0 default was calibrated against that
                    // magnitude, and score-share points land in the same range.
                    outv[k++] = new LearnedSquare(
                        WhitePieces[t], file, rank, (share - mean) * 100.0, w);
                }
        }
        return outv;

        void Accumulate(Board b, double whiteScore)
        {
            int phase = 0;
            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                phase += b.Squares[sq] switch
                {
                    Piece.WKnight or Piece.BKnight or Piece.WBishop or Piece.BBishop => 1,
                    Piece.WRook or Piece.BRook => 2,
                    Piece.WQueen or Piece.BQueen => 4,
                    _ => 0,
                };
            }
            double mg = Math.Min(24, phase) / 24.0, eg = 1.0 - mg;

            for (int sq = 0; sq < 128; sq++)
            {
                if ((sq & 0x88) != 0) continue;
                Piece p = b.Squares[sq];
                if (p == Piece.Empty) continue;
                int file = Board.FileOf(sq), rank = Board.RankOf(sq);
                int type = p switch
                {
                    Piece.WPawn or Piece.BPawn => 0,
                    Piece.WKnight or Piece.BKnight => 1,
                    Piece.WBishop or Piece.BBishop => 2,
                    Piece.WRook or Piece.BRook => 3,
                    Piece.WQueen or Piece.BQueen => 4,
                    Piece.WKing or Piece.BKing => 5,
                    _ => -1,
                };
                if (type < 0) continue;
                bool white = p is Piece.WPawn or Piece.WKnight or Piece.WBishop
                                or Piece.WRook or Piece.WQueen or Piece.WKing;
                // Black mirrors onto the white-relative table: same file, flipped rank,
                // complementary score.
                double score = white ? whiteScore : 1.0 - whiteScore;
                int idx = (white ? rank : 7 - rank) * 8 + file;
                sumMg[type][idx] += mg * score; cntMg[type][idx] += mg;
                sumEg[type][idx] += eg * score; cntEg[type][idx] += eg;
            }
        }
    }

    /// <summary>
    /// A game's recorded result and its typed move trajectory. HAS_RESULT sits on the line
    /// (Chess_Game) and the ordered move ids are that entity's Content physicality, so one
    /// join returns everything replay needs. realize.realize resolves the result content
    /// entity to its token, which keeps this path off the codepoint perfcache.
    /// </summary>
    private static IEnumerable<(string ResultToken, Hash128[] MoveIds)> ReadWitnessedLines(
        NpgsqlDataSource ds, int gameSample)
    {
        const string sql = """
            SELECT realize.realize(c.object_id),
                   public.laplace_trajectory_constituent_ids(p.trajectory)
            FROM laplace.consensus c
            JOIN laplace.physicalities p
              ON p.entity_id = c.subject_id AND p.type = 1 AND p.trajectory IS NOT NULL
            WHERE c.type_id = laplace.relation_type_id('HAS_RESULT')
            ORDER BY c.subject_id
            LIMIT @sample
            """;

        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.Parameters.AddWithValue("sample", gameSample);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            string token = r.GetString(0);
            var raw = (byte[][])r[1];
            if (raw.Length == 0) continue;
            var ids = new Hash128[raw.Length];
            for (int i = 0; i < raw.Length; i++) ids[i] = Hash128.FromBytes(raw[i]);
            yield return (token, ids);
        }
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
