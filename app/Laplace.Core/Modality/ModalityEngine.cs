using Laplace.Engine.Core;

namespace Laplace.Modality;

public readonly record struct MoveCandidate<TState, TAction>(
    TAction Action,
    TState Next,
    Hash128 ToStateId,
    Hash128 EdgeId,
    double EffMu,
    bool Rated);

public sealed record PlayedGame<TAction>(
    IReadOnlyList<RecordedEdge> Edges,
    GameOutcome Outcome,
    int Plies,
    bool Adjudicated);

public sealed class ModalityEngine<TState, TAction>
{
    private readonly ITurnModality<TState, TAction> _modality;
    private readonly Hash128 _moveTypeId;
    private readonly IContentAddresser _addresser;
    private readonly IEdgeRatings _ratings;
    private readonly IStateValuer? _valuer;




    private const double TerminalWinValue = 4_000_000_000_000d;
    private const double TerminalLossValue = 100_000_000_000d;

    public ModalityEngine(
        ITurnModality<TState, TAction> modality, Hash128 moveTypeId,
        IContentAddresser addresser, IEdgeRatings ratings)
    {
        _modality = modality ?? throw new ArgumentNullException(nameof(modality));
        _addresser = addresser ?? throw new ArgumentNullException(nameof(addresser));
        _ratings = ratings ?? throw new ArgumentNullException(nameof(ratings));
        _moveTypeId = moveTypeId;



        _valuer = ratings as IStateValuer ?? addresser as IStateValuer;
    }

    public ITurnModality<TState, TAction> Modality => _modality;

    public async Task<IReadOnlyList<MoveCandidate<TState, TAction>>> ScoreMovesAsync(
    TState state, CancellationToken ct = default)
    {
        var actions = _modality.LegalActions(state);
        if (actions.Count == 0) return Array.Empty<MoveCandidate<TState, TAction>>();

        var subjectId = _addresser.Address(_modality.StateKey(state));
        var nexts = new TState[actions.Count];
        var toIds = new Hash128[actions.Count];
        var edgeIds = new Hash128[actions.Count];
        for (int i = 0; i < actions.Count; i++)
        {
            nexts[i] = _modality.Apply(state, actions[i]);
            toIds[i] = _addresser.Address(_modality.StateKey(nexts[i]));
            edgeIds[i] = ConsensusKeys.EdgeId(subjectId, _moveTypeId, toIds[i]);
        }

        var effMu = await _ratings.EffMuAsync(edgeIds, ct).ConfigureAwait(false);
        if (effMu.Length != actions.Count)
            throw new InvalidOperationException(
                $"ratings returned {effMu.Length} values for {actions.Count} edges");




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




            if (_modality.Terminal(nexts[i]) is { } term)
            {
                double tEff = term.ForMover(mover) switch
                {
                    PlyOutcome.Win => TerminalWinValue,
                    PlyOutcome.Loss => TerminalLossValue,
                    _ => GlickoPriors.NeutralMu,
                };
                result[i] = new MoveCandidate<TState, TAction>(
                    actions[i], nexts[i], toIds[i], edgeIds[i], tEff, Rated: true);
                continue;
            }




            if (AllowsOpponentMate(nexts[i]))
            {
                result[i] = new MoveCandidate<TState, TAction>(
                    actions[i], nexts[i], toIds[i], edgeIds[i], TerminalLossValue, Rated: true);
                continue;
            }

            double eff = effMu[i];
            bool rated = eff != GlickoPriors.UnratedEffMu;
            if (succVals is not null)
            {

                double reflected = 2d * GlickoPriors.NeutralMu - succVals[i];
                eff = rated ? 0.5d * (reflected + effMu[i]) : reflected;
                rated = rated || succVals[i] != GlickoPriors.NeutralMu;
            }
            result[i] = new MoveCandidate<TState, TAction>(
                actions[i], nexts[i], toIds[i], edgeIds[i], eff, rated);
        }
        return result;
    }




    private bool AllowsOpponentMate(TState state)
    {
        int opp = _modality.SideToMove(state);
        foreach (var a in _modality.LegalActions(state))
            if (_modality.Terminal(_modality.Apply(state, a)) is { } t && t.ForMover(opp) == PlyOutcome.Win)
                return true;
        return false;
    }

    public async Task<MoveCandidate<TState, TAction>?> SelectAsync(
    TState state, double temperature, Random rng, CancellationToken ct = default)
    {
        var cands = await ScoreMovesAsync(state, ct).ConfigureAwait(false);
        if (cands.Count == 0) return null;
        return Select(cands, temperature, rng);
    }

    public static MoveCandidate<TState, TAction> Select(
    IReadOnlyList<MoveCandidate<TState, TAction>> cands, double temperature, Random rng)
    {
        if (cands.Count == 0) throw new ArgumentException("no candidates", nameof(cands));
        if (cands.Count == 1) return cands[0];

        if (temperature <= 0d)
        {

            double best = double.NegativeInfinity;
            foreach (var c in cands) if (c.EffMu > best) best = c.EffMu;
            int seen = 0;
            MoveCandidate<TState, TAction> chosen = cands[0];
            foreach (var c in cands)
                if (c.EffMu == best && rng.Next(++seen) == 0)
                    chosen = c;
            return chosen;
        }


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

    public async Task<PlayedGame<TAction>> PlayGameAsync(
    TState start, double temperature, Random rng, int maxPlies, CancellationToken ct = default)
    {
        var movers = new List<int>();
        var subjectKeys = new List<string>();
        var objectKeys = new List<string>();
        var moveKeys = new List<string?>();

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

        var outcome = terminal ?? GameOutcome.Draw;
        var edges = new RecordedEdge[subjectKeys.Count];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new RecordedEdge(
                subjectKeys[i], objectKeys[i], moveKeys[i], outcome.ForMover(movers[i]));

        return new PlayedGame<TAction>(edges, outcome, plies, adjudicated);
    }
}
