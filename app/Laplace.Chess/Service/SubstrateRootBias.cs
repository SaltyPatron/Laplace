using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public sealed class SubstrateRootBias : IRootBias
{
    private readonly NpgsqlDataSource _ds;
    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;
    private readonly int _capCp;
    private readonly double? _shrinkK0;

    public SubstrateRootBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150, double? shrinkK0 = null)
    {
        _ds = ds ?? throw new ArgumentNullException(nameof(ds));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
        _shrinkK0 = shrinkK0;
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
        var byId = Laplace.SubstrateCRUD.Npgsql.NpgsqlConsensusByIds.Read(_ds, edgeIds, ChessVocabulary.MoveType);

        var outv = new double[edgeIds.Length];
        for (int i = 0; i < edgeIds.Length; i++)
            outv[i] = byId.TryGetValue(edgeIds[i], out var row)
                ? ChessShrink.Apply(row.EffMu, row.Witnesses, _shrinkK0)
                : double.NaN;
        return outv;
    }
}
