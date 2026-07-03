using global::Npgsql;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public sealed class SubstructureFoldBias : IRootBias
{
    private readonly IStateValuer _valuer;
    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;
    private readonly int _capCp;

    public SubstructureFoldBias(IStateValuer valuer, double cpPerPoint = 8.0, int capCp = 150)
    {
        _valuer = valuer ?? throw new ArgumentNullException(nameof(valuer));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
    }

    public SubstructureFoldBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150)
        : this(new SubstrateStateValuer(ds), cpPerPoint, capCp) { }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0) return bonus;

        var state = _modality.FromFen(root.ToFen());
        var childSurfaces = new string[moves.Count];
        for (int i = 0; i < moves.Count; i++)
            childSurfaces[i] = _modality.StateKey(_modality.Apply(state, moves[i]));

        var childVals = _valuer.ValueStatesAsync(childSurfaces).GetAwaiter().GetResult();

        for (int i = 0; i < moves.Count; i++)
        {
            double ourVal = 2d * GlickoPriors.NeutralMu - childVals[i];
            double pts = (ourVal - GlickoPriors.NeutralMu) / 1e9;
            bonus[i] = Math.Clamp((int)Math.Round(_cpPerPoint * pts), -_capCp, _capCp);
        }
        return bonus;
    }
}
