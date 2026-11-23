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

public readonly record struct GameOutcome
{
    private readonly bool _isUnknown;

    public GameOutcome(int? winner)
    {
        Winner = winner;
        _isUnknown = false;
    }

    private GameOutcome(int? winner, bool isUnknown)
    {
        Winner = winner;
        _isUnknown = isUnknown;
    }

    public int? Winner { get; init; }
    public bool IsUnknown => _isUnknown;

    public static GameOutcome Draw => new(null, isUnknown: false);
    public static GameOutcome Unknown => new(null, isUnknown: true);
    public static GameOutcome WonBy(int side) => new(side, isUnknown: false);
    public bool IsDraw => !_isUnknown && Winner is null;

    /// <summary>
    /// The PGN result token. One spelling of the four canonical literals: the string is
    /// content-addressed identity input as well as witnessed HAS_RESULT testimony, and a
    /// second spelling would be a different entity.
    /// </summary>
    public string ResultToken => _isUnknown ? "*" : Winner is null ? "1/2-1/2" : Winner == 0 ? "1-0" : "0-1";
}

public enum PlyOutcome
{
    Loss = 0,
    Draw = 1,
    Win = 2,
    Neutral = 3,
}

public static class PlyOutcomeExtensions
{
    public static PlyOutcome ForMover(this GameOutcome outcome, int mover) =>
        outcome.IsUnknown ? PlyOutcome.Neutral
        : outcome.Winner is null ? PlyOutcome.Draw
        : outcome.Winner == mover ? PlyOutcome.Win
        : PlyOutcome.Loss;
}
