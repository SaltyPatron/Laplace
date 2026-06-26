using Laplace.Engine.Core;

namespace Laplace.Modality;

/// <summary>A scored candidate move: the action, the successor state + its composed id, the edge id, and eff_mu.</summary>
public readonly record struct MoveCandidate<TState, TAction>(
    TAction Action,
    TState  Next,
    Hash128 ToStateId,
    Hash128 EdgeId,
    double  EffMu,
    bool    Rated);

/// <summary>A fully played game: the recorded action edges (with final per-mover credit) and the result.</summary>
public sealed record PlayedGame<TAction>(
    IReadOnlyList<RecordedEdge> Edges,
    GameOutcome                 Outcome,
    int                         Plies,
    bool                        Adjudicated);

/// <summary>
/// Drives a turn-based modality over the substrate: scores legal moves by their consensus eff_mu,
/// selects (greedy or temperature-sampled), and plays whole games into a list of <see cref="RecordedEdge"/>
/// for the host to learn online. State/move ids come from <see cref="IContentAddresser"/> (composition),
/// never minted here. Selection ranges only over the modality's <b>legal</b> actions, so the engine can
/// never choose an illegal move; an unrated (novel) move carries the Glicko prior eff_mu, so it is
/// explorable but ranked below proven moves.
/// </summary>
public sealed class ModalityEngine<TState, TAction>
{
    private readonly ITurnModality<TState, TAction> _modality;
    private readonly Hash128 _moveTypeId;
    private readonly IContentAddresser _addresser;
    private readonly IEdgeRatings _ratings;
    private readonly IStateValuer? _valuer;

    // Hard ranks for FORCED terminal outcomes, placed far outside the learned eff_mu range
    // (GlickoPriors.NeutralMu = 1500×1e9): any forced win outranks every heuristic value, any forced
    // loss ranks below every move, and a forced draw sits at neutral. So win ≫ … ≫ draw ≫ … ≫ loss.
    private const double TerminalWinValue  = 4_000_000_000_000d;
    private const double TerminalLossValue =   100_000_000_000d;

    public ModalityEngine(
        ITurnModality<TState, TAction> modality, Hash128 moveTypeId,
        IContentAddresser addresser, IEdgeRatings ratings)
    {
        _modality   = modality  ?? throw new ArgumentNullException(nameof(modality));
        _addresser  = addresser ?? throw new ArgumentNullException(nameof(addresser));
        _ratings    = ratings   ?? throw new ArgumentNullException(nameof(ratings));
        _moveTypeId = moveTypeId;
        // If the host can value a state by folding its substructures, the engine reasons over them
        // (negamax over the successor's value, blended with the exact move edge) instead of relying only
        // on the whole-position MOVE edge. Hosts that don't implement it keep the move-edge behaviour.
        _valuer     = ratings as IStateValuer ?? addresser as IStateValuer;
    }

    public ITurnModality<TState, TAction> Modality => _modality;

    /// <summary>Score every legal move from <paramref name="state"/> in one ratings round trip.</summary>
    public async Task<IReadOnlyList<MoveCandidate<TState, TAction>>> ScoreMovesAsync(
        TState state, CancellationToken ct = default)
    {
        var actions = _modality.LegalActions(state);
        if (actions.Count == 0) return Array.Empty<MoveCandidate<TState, TAction>>();

        var subjectId = _addresser.Address(_modality.StateKey(state));
        var nexts   = new TState[actions.Count];
        var toIds   = new Hash128[actions.Count];
        var edgeIds = new Hash128[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            nexts[i]   = _modality.Apply(state, actions[i]);
            toIds[i]   = _addresser.Address(_modality.StateKey(nexts[i]));
            edgeIds[i] = ConsensusKeys.EdgeId(subjectId, _moveTypeId, toIds[i]);
        }

        var effMu = await _ratings.EffMuAsync(edgeIds, ct).ConfigureAwait(false);
        if (effMu.Length != actions.Count)
            throw new InvalidOperationException(
                $"ratings returned {effMu.Length} values for {actions.Count} edges");

        // Substructure-fold reasoning: value each successor by folding its substructures, then take the
        // negamax reflection (a position good for the opponent is bad for us) and blend with the exact
        // move edge. Without a valuer, this is a no-op and the move edge stands alone.
        double[]? succVals = null;
        if (_valuer is not null)
        {
            var nextSurfaces = new string[actions.Count];
            for (int i = 0; i < actions.Count; i++) nextSurfaces[i] = _modality.StateKey(nexts[i]);
            succVals = await _valuer.ValueStatesAsync(nextSurfaces, ct).ConfigureAwait(false);
            if (succVals.Length != actions.Count)
                throw new InvalidOperationException(
                    $"valuer returned {succVals.Length} values for {actions.Count} states");
        }

        int mover = _modality.SideToMove(state);
        var result = new MoveCandidate<TState, TAction>[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            // A move into a TERMINAL state is a HARD rank that overrides the learned fold: a forced win
            // (a checkmate confirms it worked) ≫ any heuristic value ≫ a draw ≫ a forced loss. So the
            // engine always takes a forced mate and never walks into one. (Non-terminal moves fall to the
            // learned substructure fold + move edge below.)
            if (_modality.Terminal(nexts[i]) is { } term)
            {
                double tEff = term.ForMover(mover) switch
                {
                    PlyOutcome.Win  => TerminalWinValue,
                    PlyOutcome.Loss => TerminalLossValue,
                    _               => GlickoPriors.NeutralMu, // forced draw = neutral
                };
                result[i] = new MoveCandidate<TState, TAction>(
                    actions[i], nexts[i], toIds[i], edgeIds[i], tEff, Rated: true);
                continue;
            }

            // Depth-2 mate safety (movegen only — NO eval, NO DB): reject any move that lets the
            // opponent deliver mate on the reply. Depth-1 can't see this; it's the single biggest
            // blunder, and it's deterministic regardless of how fuzzy the learned positional eval is.
            if (AllowsOpponentMate(nexts[i]))
            {
                result[i] = new MoveCandidate<TState, TAction>(
                    actions[i], nexts[i], toIds[i], edgeIds[i], TerminalLossValue, Rated: true);
                continue;
            }

            double eff   = effMu[i];
            bool   rated = eff != GlickoPriors.UnratedEffMu;
            if (succVals is not null)
            {
                // Negamax: our value of this move = reflection of the successor's side-to-move value.
                double reflected = 2d * GlickoPriors.NeutralMu - succVals[i];
                eff   = rated ? 0.5d * (reflected + effMu[i]) : reflected;
                rated = rated || succVals[i] != GlickoPriors.NeutralMu;
            }
            result[i] = new MoveCandidate<TState, TAction>(
                actions[i], nexts[i], toIds[i], edgeIds[i], eff, rated);
        }
        return result;
    }

    // True if the side to move at <paramref name="state"/> has any move that ends the game as THEIR win
    // (a mate). Used to reject our candidate moves that walk into a forced mate-in-1 — pure movegen, no
    // eval/DB, so the guard is exact even when the learned eval is weak.
    private bool AllowsOpponentMate(TState state)
    {
        int opp = _modality.SideToMove(state);
        foreach (var a in _modality.LegalActions(state))
            if (_modality.Terminal(_modality.Apply(state, a)) is { } t && t.ForMover(opp) == PlyOutcome.Win)
                return true;
        return false;
    }

    /// <summary>
    /// Pick one move. <paramref name="temperature"/> ≤ 0 is greedy (max eff_mu, ties broken by the rng);
    /// &gt; 0 softmax-samples on eff_mu/1e9 (real-rating units) — higher temperature = more exploration.
    /// Returns null only if the state has no legal moves (terminal).
    /// </summary>
    public async Task<MoveCandidate<TState, TAction>?> SelectAsync(
        TState state, double temperature, Random rng, CancellationToken ct = default)
    {
        var cands = await ScoreMovesAsync(state, ct).ConfigureAwait(false);
        if (cands.Count == 0) return null;
        return Select(cands, temperature, rng);
    }

    /// <summary>Pure selection over already-scored candidates (no I/O); see <see cref="SelectAsync"/>.</summary>
    public static MoveCandidate<TState, TAction> Select(
        IReadOnlyList<MoveCandidate<TState, TAction>> cands, double temperature, Random rng)
    {
        if (cands.Count == 0) throw new ArgumentException("no candidates", nameof(cands));
        if (cands.Count == 1) return cands[0];

        if (temperature <= 0d)
        {
            // Greedy argmax with random tie-break, so self-play from equal priors still diversifies.
            double best = double.NegativeInfinity;
            foreach (var c in cands) if (c.EffMu > best) best = c.EffMu;
            int seen = 0;
            MoveCandidate<TState, TAction> chosen = cands[0];
            foreach (var c in cands)
                if (c.EffMu == best && rng.Next(++seen) == 0)
                    chosen = c;
            return chosen;
        }

        // Softmax over eff_mu in real-rating units (÷1e9), numerically stabilised by the max logit.
        double maxLogit = double.NegativeInfinity;
        foreach (var c in cands) maxLogit = Math.Max(maxLogit, c.EffMu / 1e9);
        double sum = 0d;
        Span<double> w = cands.Count <= 256 ? stackalloc double[cands.Count] : new double[cands.Count];
        for (int i = 0; i < cands.Count; i++)
        {
            double v = Math.Exp(((cands[i].EffMu / 1e9) - maxLogit) / temperature);
            w[i] = v;
            sum += v;
        }
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < cands.Count; i++)
        {
            r -= w[i];
            if (r <= 0d) return cands[i];
        }
        return cands[^1];
    }

    /// <summary>
    /// Play one game to its terminal state (or to <paramref name="maxPlies"/>, which adjudicates a draw),
    /// recording one move edge per ply as canonical surfaces. Per-ply credit is assigned from the final
    /// outcome relative to the side that made that move. The returned edges are ready for
    /// <see cref="ITurnLearner.LearnGameAsync"/>.
    /// </summary>
    public async Task<PlayedGame<TAction>> PlayGameAsync(
        TState start, double temperature, Random rng, int maxPlies, CancellationToken ct = default)
    {
        var movers      = new List<int>();
        var subjectKeys = new List<string>();
        var objectKeys  = new List<string>();
        var moveKeys    = new List<string?>();

        var state = start;
        int plies = 0;
        GameOutcome? terminal = _modality.Terminal(state);
        bool adjudicated = false;

        while (terminal is null)
        {
            if (plies >= maxPlies) { adjudicated = true; break; }

            var cands = await ScoreMovesAsync(state, ct).ConfigureAwait(false);
            if (cands.Count == 0)
                throw new InvalidOperationException(
                    $"{_modality.Name}: no legal moves at a non-terminal state (rules inconsistency)");

            int mover = _modality.SideToMove(state);
            var chosen = Select(cands, temperature, rng);

            subjectKeys.Add(_modality.StateKey(state));
            objectKeys.Add(_modality.StateKey(chosen.Next));
            moveKeys.Add(_modality.ActionKey(state, chosen.Action));
            movers.Add(mover);

            state = chosen.Next;
            plies++;
            terminal = _modality.Terminal(state);
        }

        var outcome = terminal ?? GameOutcome.Draw; // adjudicated cap → draw
        var edges = new RecordedEdge[subjectKeys.Count];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new RecordedEdge(
                subjectKeys[i], objectKeys[i], moveKeys[i], outcome.ForMover(movers[i]));

        return new PlayedGame<TAction>(edges, outcome, plies, adjudicated);
    }
}
