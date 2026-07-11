using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

public readonly record struct LearnedSquare(char Piece, int File, int Rank, double DevPoints, double Witness);

public static class LearnedPst
{
    public const string WhitePieces = "PNBRQK";

    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds)
    {
        var coords = new (char Piece, int File, int Rank)[WhitePieces.Length * 64];
        var edgeIds = new Hash128[coords.Length];
        lock (ChessCompose.Gate)
        {
            int k = 0;
            foreach (char pc in WhitePieces)
                for (int rank = 0; rank < 8; rank++)
                    for (int file = 0; file < 8; file++)
                    {
                        string token = $"{pc}{(char)('a' + file)}{(char)('1' + rank)}";
                        var subId = ChessCompose.Position(token).Substructures[0].Id;
                        edgeIds[k] = ConsensusKeys.EdgeId(subId, ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject);
                        coords[k] = (pc, file, rank);
                        k++;
                    }
        }

        var stats = ReadStats(ds, edgeIds);
        var outv = new LearnedSquare[coords.Length];
        for (int i = 0; i < coords.Length; i++)
        {
            var (pc, file, rank) = coords[i];
            if (stats.TryGetValue(edgeIds[i], out var s))
                outv[i] = new LearnedSquare(pc, file, rank, (s.Mu - GlickoPriors.NeutralMu) / 1e9, s.W);
            else
                outv[i] = new LearnedSquare(pc, file, rank, 0d, 0d);
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

    private static Dictionary<Hash128, (double Mu, double W)> ReadStats(NpgsqlDataSource ds, Hash128[] ids)
    {
        var raw = new byte[ids.Length][];
        for (int i = 0; i < ids.Length; i++) raw[i] = ids[i].ToBytes();

        var map = new Dictionary<Hash128, (double, double)>(ids.Length);
        using var conn = ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, eff_mu, witness_count FROM laplace.consensus_by_ids($1)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            Value = raw,
        });
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[Hash128.FromBytes((byte[])r[0])] = (r.GetDouble(1), r.GetDouble(2));
        return map;
    }
}
