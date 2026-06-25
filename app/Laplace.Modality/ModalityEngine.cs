using Laplace.Engine.Core;

namespace Laplace.Modality;

/// <summary>A scored candidate move: the action, the successor state's id, the edge id, and its eff_mu.</summary>
public readonly record struct MoveCandidate<TAction>(
    TAction Action,
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
/// for the host to learn online. Selection always ranges over the modality's <b>legal</b> actions, so the
/// engine can never choose an illegal move; an unrated (novel) move carries the Glicko prior eff_mu, so it
/// is explorable but ranked below proven moves.
/// </summary>
public sealed class ModalityEngine<TState, TAction>
{
    private readonly ITurnModality<TState, TAction> _modality;
    private readonly Hash128 _moveTypeId;
    private readonly IEdgeRatings _ratings;

    public ModalityEngine(
        ITurnModality<TState, TAction> modality, Hash128 moveTypeId, IEdgeRatings ratings)
    {
        _modality   = modality   ?? throw new ArgumentNullException(nameof(modality));
        _ratings    = ratings    ?? throw new ArgumentNullException(nameof(ratings));
        _moveTypeId = moveTypeId;
    }

    public ITurnModality<TState, TAction> Modality => _modality;

    /// <summary>Score every legal move from <paramref name="state"/> in one ratings round trip.</summary>
    public async Task<IReadOnlyList<MoveCandidate<TAction>>> ScoreMovesAsync(
        TState state, CancellationToken ct = default)
    {
        var actions = _modality.LegalActions(state);
        if (actions.Count == 0) return Array.Empty<MoveCandidate<TAction>>();

        var subjectId = ConsensusKeys.StateId(_modality, state);
        var toIds  = new Hash128[actions.Count];
        var edgeIds = new Hash128[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            var next = _modality.Apply(state, actions[i]);
            toIds[i]   = ConsensusKeys.StateId(_modality, next);
            edgeIds[i] = ConsensusKeys.EdgeId(subjectId, _moveTypeId, toIds[i]);
        }

        var effMu = await _ratings.EffMuAsync(edgeIds, ct).ConfigureAwait(false);
        if (effMu.Length != actions.Count)
            throw new InvalidOperationException(
                $"ratings returned {effMu.Length} values for {actions.Count} edges");

        var result = new MoveCandidate<TAction>[actions.Count];
        for (int i = 0; i < actions.Count; i++)
            result[i] = new MoveCandidate<TAction>(
                actions[i], toIds[i], edgeIds[i], effMu[i],
                Rated: effMu[i] != GlickoPriors.UnratedEffMu);
        return result;
    }

    /// <summary>
    /// Pick one move. <paramref name="temperature"/> ≤ 0 is greedy (max eff_mu, ties broken by the rng);
    /// &gt; 0 softmax-samples on eff_mu/1e9 (real-rating units) — higher temperature = more exploration.
    /// Returns the chosen candidate, or null only if the state has no legal moves (terminal).
    /// </summary>
    public async Task<MoveCandidate<TAction>?> SelectAsync(
        TState state, double temperature, Random rng, CancellationToken ct = default)
    {
        var cands = await ScoreMovesAsync(state, ct).ConfigureAwait(false);
        if (cands.Count == 0) return null;
        return Select(cands, temperature, rng);
    }

    /// <summary>Pure selection over already-scored candidates (no I/O); see <see cref="SelectAsync"/>.</summary>
    public static MoveCandidate<TAction> Select(
        IReadOnlyList<MoveCandidate<TAction>> cands, double temperature, Random rng)
    {
        if (cands.Count == 0) throw new ArgumentException("no candidates", nameof(cands));
        if (cands.Count == 1) return cands[0];

        if (temperature <= 0d)
        {
            // Greedy argmax with random tie-break, so self-play from equal priors still diversifies.
            double best = double.NegativeInfinity;
            int bestCount = 0;
            foreach (var c in cands) if (c.EffMu > best) { best = c.EffMu; }
            // reservoir over the maxima
            MoveCandidate<TAction> chosen = cands[0];
            foreach (var c in cands)
                if (c.EffMu == best && (++bestCount == 1 || rng.Next(bestCount) == 0))
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
    /// recording one MOVE edge per ply. Per-ply credit is assigned from the final outcome relative to the
    /// side that made that move. The returned edges are ready for <see cref="ITurnLearner.LearnGameAsync"/>.
    /// </summary>
    public async Task<PlayedGame<TAction>> PlayGameAsync(
        TState start, double temperature, Random rng, int maxPlies, CancellationToken ct = default)
    {
        var movers   = new List<int>();
        var subjects = new List<Hash128>();
        var types    = new List<Hash128>();
        var objects  = new List<Hash128>();
        var contexts = new List<Hash128?>();

        var state = start;
        int plies = 0;
        GameOutcome? terminal = _modality.Terminal(state);
        bool adjudicated = false;

        while (terminal is null)
        {
            if (plies >= maxPlies) { adjudicated = true; break; }

            var cands = await ScoreMovesAsync(state, ct).ConfigureAwait(false);
            if (cands.Count == 0)
            {
                // No legal moves but Terminal() said ongoing — the modality's rules are inconsistent.
                throw new InvalidOperationException(
                    $"{_modality.Name}: no legal moves at a non-terminal state (rules inconsistency)");
            }

            int mover = _modality.SideToMove(state);
            var chosen = Select(cands, temperature, rng);

            subjects.Add(ConsensusKeys.StateId(_modality, state));
            types.Add(_moveTypeId);
            objects.Add(chosen.ToStateId);
            contexts.Add(ConsensusKeys.ActionId(_modality, state, chosen.Action));
            movers.Add(mover);

            state = _modality.Apply(state, chosen.Action);
            plies++;
            terminal = _modality.Terminal(state);
        }

        var outcome = terminal ?? GameOutcome.Draw; // adjudicated cap → draw
        var edges = new RecordedEdge[subjects.Count];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new RecordedEdge(
                subjects[i], types[i], objects[i], contexts[i], outcome.ForMover(movers[i]));

        return new PlayedGame<TAction>(edges, outcome, plies, adjudicated);
    }
}
