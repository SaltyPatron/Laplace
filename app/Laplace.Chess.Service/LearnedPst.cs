using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;

namespace Laplace.Chess.Service;

/// <summary>One learned piece-square value: the corpus's OUTCOME deviation (in Glicko rating points from a
/// draw) for "this piece on this square is present for the side to move", plus the evidence weight.</summary>
public readonly record struct LearnedSquare(char Piece, int File, int Rank, double DevPoints, double Witness);

/// <summary>
/// Reads what the substrate has actually LEARNED about each piece-square from the ~29M-move corpus — the
/// data behind a data-driven (learned) PST. Each placement token (<c>Pe4</c>, <c>nf6</c>, …) is a
/// substructure node; its <c>OUTCOME</c> edge eff_mu says how often the side to move won with that feature
/// present. This is read-only inspection: it touches NO eval path, so it cannot regress play. It is the
/// foundation for later swapping PeSTO's hand-tuned PST for these measured weights (the guardrail at
/// <see cref="SubstrateTurnHost"/>:124-128 — validate before trusting — is why we surface + inspect first).
/// </summary>
public static class LearnedPst
{
    public const string WhitePieces = "PNBRQK";

    /// <summary>Learned values for the white piece set across all 64 squares (eff_mu deviation in rating
    /// points; witness = evidence). Black mirrors by symmetry, so white is sufficient to inspect.</summary>
    public static IReadOnlyList<LearnedSquare> ReadWhite(NpgsqlDataSource ds)
    {
        // Build every (piece, square) placement token and its OUTCOME edge id (compose is native → gated).
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
                outv[i] = new LearnedSquare(pc, file, rank, 0d, 0d); // unseen → neutral
        }
        return outv;
    }

    /// <summary>Materialize the learned values as PeSTO-shaped PST tables (<c>[type-1][idx]</c>, white POV
    /// rank-8-first — the exact convention <see cref="Laplace.Modality.Chess.Evaluation"/> indexes), scaled
    /// to centipawns. Same table for mid + endgame (the corpus doesn't phase-split). Unseen squares → 0.
    /// Drop straight into <c>new Search(..., mgPst, egPst)</c> to run the data-driven eval.</summary>
    public static (int[][] Mg, int[][] Eg) BuildTables(NpgsqlDataSource ds, double scaleCpPerPoint = 6.0)
    {
        var learned = ReadWhite(ds);
        var mg = new int[6][]; var eg = new int[6][];
        for (int t = 0; t < 6; t++) { mg[t] = new int[64]; eg[t] = new int[64]; }
        foreach (var s in learned)
        {
            int t = WhitePieces.IndexOf(s.Piece);
            if (t < 0) continue;
            int idx = (7 - s.Rank) * 8 + s.File;            // white POV, rank-8-first → matches Evaluation
            int cp = (int)Math.Round(s.DevPoints * scaleCpPerPoint);
            mg[t][idx] = cp; eg[t][idx] = cp;
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
            "SELECT id, (rating - 2*rd)::double precision, witness_count::double precision " +
            "FROM laplace.consensus WHERE id = ANY($1)";
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
