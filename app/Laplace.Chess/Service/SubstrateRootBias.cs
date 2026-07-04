using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public sealed class SubstrateRootBias : IRootBias
{
    private const double ShrinkK0 = 15_000d;

    private readonly NpgsqlDataSource _ds;
    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;
    private readonly int _capCp;

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

        var effMu = ReadShrunkEffMu(edgeIds);
        for (int i = 0; i < moves.Count; i++)
        {
            if (double.IsNaN(effMu[i])) { bonus[i] = 0; continue; }
            double pts = (effMu[i] - GlickoPriors.NeutralMu) / 1e9;
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
                "SELECT id, eff_mu, witness_count FROM laplace.consensus_by_ids($1)";
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
            outv[i] = map.TryGetValue(edgeIds[i], out var v) ? v : double.NaN;
        return outv;
    }
}
