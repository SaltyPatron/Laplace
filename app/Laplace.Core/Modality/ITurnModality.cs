namespace Laplace.Modality;

public interface ITurnModality<TState, TAction>
{
    string Name { get; }

    TState Initial();

    string StateKey(TState state);

    string ActionKey(TState state, TAction action);

    IReadOnlyList<TAction> LegalActions(TState state);

    TState Apply(TState state, TAction action);

    int SideToMove(TState state);

    GameOutcome? Terminal(TState state);
}

public readonly record struct GameOutcome(int? Winner)
{
    public static GameOutcome Draw => new((int?)null);
    public static GameOutcome WonBy(int side) => new(side);
    public bool IsDraw => Winner is null;

    /// <summary>
    /// The PGN result token. One spelling of the three literals, because this string is
    /// content-addressed identity input (the playing ids in ChessVocabulary) as well as
    /// witnessed HAS_RESULT testimony — a second transcription is a silent identity fork.
    /// </summary>
    public string ResultToken => Winner is null ? "1/2-1/2" : Winner == 0 ? "1-0" : "0-1";
}

public enum PlyOutcome
{
    Loss = 0,
    Draw = 1,
    Win = 2,
}

public static class PlyOutcomeExtensions
{
    public static PlyOutcome ForMover(this GameOutcome outcome, int mover) =>
        outcome.Winner is null ? PlyOutcome.Draw
        : outcome.Winner == mover ? PlyOutcome.Win
        : PlyOutcome.Loss;
}
