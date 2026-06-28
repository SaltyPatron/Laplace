using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// The SUBSTRATE seam for the classical search (<see cref="IRootBias"/>): at the root, look up each
/// candidate move's learned value — the eff_mu of its <c>MOVE</c> edge in <c>laplace.consensus</c>,
/// accumulated from the 34.5M-relation game graph — and convert it to a centipawn bonus the search blends
/// into root selection. Same content = same id, so this is an INDEXED point lookup (consensus_pkey),
/// batched into ONE query per engine move (never inside the search tree).
///
/// <para>Empirical-Bayes confidence shrinkage (the same K0=15000 the substrate host uses) pulls a move's
/// eff_mu toward neutral by its evidence, so a high rating from few games can't dominate. Unrated/novel
/// moves get 0 (no signal) — the classical floor stands where the graph is silent; rated moves are nudged
/// toward what actually won.</para>
/// </summary>
public sealed class SubstrateRootBias : IRootBias
{
    private const double ShrinkK0 = 15_000d;

    private readonly NpgsqlDataSource _ds;
    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;  // centipawns per Glicko rating-point of deviation from neutral
    private readonly int _capCp;          // clamp so the prior never overrides tactics

    public SubstrateRootBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
    }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0) return bonus;

        // Content-address the root and each successor → the MOVE edge ids (pure compute, perfcache-backed).
        var state = _modality.FromFen(root.ToFen());
        var edgeIds = new Hash128[moves.Count];
        lock (ChessCompose.Gate)
        {
            var rootId = ChessCompose.PositionId(_modality.StateKey(state));
            for (int i = 0; i < moves.Count; i++)
            {
                var next = _modality.Apply(state, moves[i]);
                var toId = ChessCompose.PositionId(_modality.StateKey(next));
                edgeIds[i] = ConsensusKeys.EdgeId(rootId, ChessVocabulary.MoveType, toId);
            }
        }

        var effMu = ReadShrunkEffMu(edgeIds); // NaN where unrated/novel
        for (int i = 0; i < moves.Count; i++)
        {
            if (double.IsNaN(effMu[i])) { bonus[i] = 0; continue; }
            double pts = (effMu[i] - GlickoPriors.NeutralMu) / 1e9; // rating-point deviation from a draw
            bonus[i] = Math.Clamp((int)Math.Round(_cpPerPoint * pts), -_capCp, _capCp);
        }
        return bonus;
    }

    private double[] ReadShrunkEffMu(Hash128[] edgeIds)
    {
        var raw = new byte[edgeIds.Length][];
        for (int i = 0; i < edgeIds.Length; i++) raw[i] = edgeIds[i].ToBytes();

        var map = new Dictionary<Hash128, double>(edgeIds.Length);
        using (var conn = _ds.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
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
            {
                double mu = r.GetDouble(1), w = r.GetDouble(2);
                double shrunk = GlickoPriors.NeutralMu + (mu - GlickoPriors.NeutralMu) * (w / (w + ShrinkK0));
                map[Hash128.FromBytes((byte[])r[0])] = shrunk;
            }
        }

        var outv = new double[edgeIds.Length];
        for (int i = 0; i < edgeIds.Length; i++)
            outv[i] = map.TryGetValue(edgeIds[i], out var v) ? v : double.NaN; // no row → unrated
        return outv;
    }
}
