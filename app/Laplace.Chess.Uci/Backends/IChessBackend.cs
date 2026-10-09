using Laplace.Chess.Service.Uci;

namespace Laplace.Chess.Uci.Backends;

/// <summary>
/// Laplace's own chess behind the UCI front: a position goes in; a move, its info lines and a receipt come out. The
/// protocol, the CLI and the endpoint talk only to this interface, so the managed stand-in
/// (<see cref="ManagedSearchBackend"/>) can be replaced by the native core over P/Invoke without touching them.
/// Info is handed out as UCI lines, the form any backend (managed or native) can produce.
/// </summary>
public interface IChessBackend : IDisposable
{
    string Name { get; }
    string Author { get; }
    /// <summary>The backend's <c>option name …</c> lines for the uci handshake.</summary>
    IReadOnlyList<string> OptionLines { get; }
    void SetOption(string name, string value);
    /// <summary>isready: slow setup belongs here, before any clock runs. Problems are reported through <paramref name="info"/>.</summary>
    void Prepare(Action<string> info);
    /// <summary>ucinewgame.</summary>
    void NewGame(Action<string> info);
    /// <summary>The position to search: a FEN (null: the start position) and the UCI moves from it. A malformed
    /// position leaves the previous one in place.</summary>
    void SetPosition(string? fen, IReadOnlyList<string> moves);
    /// <summary>Whether a search can start now without setup; false is an explicit failed move, never another player.</summary>
    bool CanSearch(Action<string> info);
    /// <summary>Snapshot the current position and limits; the returned search runs on the caller's thread, emits
    /// progress lines as it goes, and returns the move with its final lines.</summary>
    Func<CancellationToken, Action<string>, ChessBackendResult> BeginSearch(UciLimits limits);
}

/// <summary>A finished search: the move (<c>0000</c> when there is none), the final info lines to send before it, and
/// the backend's receipt line when it has one.</summary>
public sealed record ChessBackendResult(string BestMove, IReadOnlyList<string> FinalLines, string? Ponder = null);
