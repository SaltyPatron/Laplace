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

/// <summary>
/// The exact process a job is about to launch, published before it starts. The viewer
/// renders it as a prompt line and lets the operator copy it, so "what did the lab
/// actually run?" is answerable from the UI instead of only from a server-side log.
/// </summary>
public sealed record ChessLabCommandEvent(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null) : ChessLabEvent
{
    /// <summary>The command as a single copy-pasteable line, POSIX-quoted where needed.</summary>
    public string CommandLine => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '\'', '\\', '$', '&', '|', ';', '<', '>']) < 0
            ? arg
            : '"' + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
}

/// <summary>
/// One line of raw process I/O. Routed to the job's <see cref="ChessLabTerminal"/> rather
/// than the structured event channel — see that type for why the two cannot share one.
/// </summary>
public sealed record ChessLabTerminalEvent(
    string Stream,
    string Text,
    string? Engine = null,
    string? Direction = null) : ChessLabEvent;
