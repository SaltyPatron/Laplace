namespace Laplace.Chess.Service;

public abstract record ChessLabEvent
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChessLabLogEvent(string Level, string Message) : ChessLabEvent;

public sealed record ChessLabProgressEvent(int Done, int Total, string? Label = null) : ChessLabEvent;

public sealed record ChessLabGameEvent(
    int Index,
    string? White,
    string? Black,
    string Result,
    string? PgnPath = null) : ChessLabEvent;

public sealed record ChessLabMetricEvent(string Name, double Value, string? Unit = null) : ChessLabEvent;

/// <summary>
/// One ply of a live game, enough to draw a board: emitted by the cutechess
/// runner (parsed from -debug UCI traffic) and by in-process self-play (via the
/// MatchRunner onPly tap). Game numbers disambiguate interleaved parallel games.
/// </summary>
public sealed record ChessLabBoardEvent(
    int Game,
    int Ply,
    string Uci,
    string Fen,
    string? White = null,
    string? Black = null) : ChessLabEvent;

public sealed record ChessLabTableEvent(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows) : ChessLabEvent;

public sealed record ChessLabDoneEvent(ChessLabJobState FinalState, string? Message = null) : ChessLabEvent;
