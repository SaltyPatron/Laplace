using global::Npgsql;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// The HONEST substrate seam (<see cref="IRootBias"/>): bias the search root by the substructure-fold
/// value of each candidate move's resulting position, not by the raw <c>MOVE</c>-edge popularity that
/// <see cref="SubstrateRootBias"/> reads. The fold (<see cref="SubstrateStateValuer"/>) credits a
/// position from the OUTCOME consensus over the piece-square / pawn / material <i>substructures</i> it
/// shares with the corpus — so it returns a signal even on positions never played, the real test of
/// whether learned positional knowledge <b>transfers</b>. The first naive root-eff_mu test came back null
/// (−17±54) precisely because random middlegames aren't in the corpus and eff_mu ≈ popularity; this is
/// the fair re-run.
///
/// <para>For the root (us to move) and each candidate move, we apply the move and value the CHILD
/// position — where the <b>opponent</b> is to move. The fold is side-to-move-relative, so we negate
/// around neutral μ to get the value to US, then map rating-point deviation → centipawns (clamped, so the
/// learned prior nudges but never overrides tactics). Where the fold has no evidence it returns neutral μ
/// → 0 cp → the classical floor stands. Computed once at the root, never inside the search tree.</para>
/// </summary>
public sealed class SubstructureFoldBias : IRootBias
{
    private readonly IStateValuer _valuer;
    private readonly ChessModality _modality = new();
    private readonly double _cpPerPoint;  // centipawns per Glicko rating-point of deviation from neutral
    private readonly int _capCp;          // clamp so the learned prior never overrides tactics

    public SubstructureFoldBias(IStateValuer valuer, double cpPerPoint = 8.0, int capCp = 150)
    {
        _valuer = valuer ?? throw new ArgumentNullException(nameof(valuer));
        _cpPerPoint = cpPerPoint;
        _capCp = capCp;
    }

    /// <summary>Convenience: build the read-only fold valuer straight from a data source.</summary>
    public SubstructureFoldBias(NpgsqlDataSource ds, double cpPerPoint = 8.0, int capCp = 150)
        : this(new SubstrateStateValuer(ds), cpPerPoint, capCp) { }

    public int[] Bonus(Board root, IReadOnlyList<ChessMove> moves)
    {
        var bonus = new int[moves.Count];
        if (moves.Count == 0) return bonus;

        // The child position's canonical surface (PositionContent.Surface via StateKey) is pure C#; the
        // native compose happens inside the valuer, which serializes it under ChessCompose.Gate.
        var state = _modality.FromFen(root.ToFen());
        var childSurfaces = new string[moves.Count];
        for (int i = 0; i < moves.Count; i++)
            childSurfaces[i] = _modality.StateKey(_modality.Apply(state, moves[i]));

        // One batched fold for all successors (block at the root only — never in the hot loop).
        var childVals = _valuer.ValueStatesAsync(childSurfaces).GetAwaiter().GetResult();

        for (int i = 0; i < moves.Count; i++)
        {
            // childVals[i] is opponent-to-move-relative; negate around neutral μ to value it for us.
            double ourVal = 2d * GlickoPriors.NeutralMu - childVals[i];
            double pts = (ourVal - GlickoPriors.NeutralMu) / 1e9; // rating-point deviation from a draw
            bonus[i] = Math.Clamp((int)Math.Round(_cpPerPoint * pts), -_capCp, _capCp);
        }
        return bonus;
    }
}
