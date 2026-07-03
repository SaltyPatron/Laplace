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

public sealed record ChessLabTableEvent(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows) : ChessLabEvent;

public sealed record ChessLabDoneEvent(ChessLabJobState FinalState, string? Message = null) : ChessLabEvent;
