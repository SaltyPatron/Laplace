namespace Laplace.Modality;

/// <summary>
/// A turn-based, two-or-more-player modality the substrate can play and learn from. The contract is
/// deliberately tiny: a domain need only know how to <b>encode</b> a state to a canonical content key
/// (so identical states — transpositions — collapse to one substrate entity), enumerate the
/// <b>legal</b> actions, <b>apply</b> one, and report when a state is <b>terminal</b> and who won.
///
/// Everything else — content-addressing, rating storage, online learning, selection, lookahead — is
/// supplied by <see cref="ModalityEngine{TState,TAction}"/> over the substrate. Chess is the first
/// instance; Go, dialogue-with-reward, tool-use trajectories, etc. fit the same shape.
/// </summary>
/// <typeparam name="TState">Immutable (or treated-as-immutable) game state.</typeparam>
/// <typeparam name="TAction">A legal action/move from a state.</typeparam>
public interface ITurnModality<TState, TAction>
{
    /// <summary>Stable short name (used as the content-address namespace, e.g. "chess").</summary>
    string Name { get; }

    /// <summary>The canonical opening state(s). Standard chess is one; Chess960 is 960 roots.</summary>
    TState Initial();

    /// <summary>
    /// Canonical content key for a state — the basis of its 16-byte content address. MUST be identical
    /// for states that are the same for play purposes (e.g. chess: FEN minus the halfmove/fullmove
    /// counters) so transpositions share one node. MUST differ whenever the legal-move set or outcome
    /// could differ.
    /// </summary>
    string StateKey(TState state);

    /// <summary>Canonical content key for an action in a state (e.g. UCI long algebraic).</summary>
    string ActionKey(TState state, TAction action);

    /// <summary>All legal actions from <paramref name="state"/> (empty iff terminal).</summary>
    IReadOnlyList<TAction> LegalActions(TState state);

    /// <summary>Apply a legal action, returning the successor state.</summary>
    TState Apply(TState state, TAction action);

    /// <summary>Index of the player to move (0-based). Chess: 0 = White, 1 = Black.</summary>
    int SideToMove(TState state);

    /// <summary>
    /// Terminal status: <c>null</c> while the game continues, otherwise the result. This is the exact
    /// game logic (legality/mate/stalemate/draw rules) — never inferred, so the engine cannot produce
    /// an illegal or unfinished game.
    /// </summary>
    GameOutcome? Terminal(TState state);
}

/// <summary>
/// The result of a finished game. <see cref="Winner"/> is the side index that won, or <c>null</c> for
/// a draw. Per-mover credit is derived from this in <see cref="PlyOutcomeExtensions.ForMover"/>.
/// </summary>
public readonly record struct GameOutcome(int? Winner)
{
    public static GameOutcome Draw => new((int?)null);
    public static GameOutcome WonBy(int side) => new(side);
    public bool IsDraw => Winner is null;
}

/// <summary>
/// Per-ply credit from a mover's perspective, matching the substrate's Glicko-2 score scale
/// (loss/draw/win → 0 / 0.5 / 1). Numeric values mirror <c>AttestationOutcome</c> so the mapping is
/// unambiguous at the wiring layer.
/// </summary>
public enum PlyOutcome
{
    Loss = 0,
    Draw = 1,
    Win  = 2,
}

public static class PlyOutcomeExtensions
{
    /// <summary>Credit for the move made by <paramref name="mover"/> given the final outcome.</summary>
    public static PlyOutcome ForMover(this GameOutcome outcome, int mover) =>
        outcome.Winner is null ? PlyOutcome.Draw
        : outcome.Winner == mover ? PlyOutcome.Win
        : PlyOutcome.Loss;
}
