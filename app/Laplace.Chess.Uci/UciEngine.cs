using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Backends;

namespace Laplace.Chess.Uci;

/// <summary>
/// The UCI protocol for Laplace's own engine (<c>laplace-uci</c>, <c>--engine laplace</c>): it parses commands, runs
/// the search off the input thread so <c>stop</c> is read at once, and writes the wire format. The chess itself is the
/// <see cref="IChessBackend"/>; this class holds no chess logic.
/// </summary>
public sealed class UciEngine
{
    private readonly IChessBackend _backend;
    private readonly object _outputLock = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    public UciEngine() : this(new ManagedSearchBackend()) { }
    public UciEngine(IChessBackend backend) => _backend = backend;

    internal IChessBackend Backend => _backend;

    public bool Handle(string line, TextWriter output)
    {
        var tok = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return true;
        Action<string> info = msg => Info(output, msg);

        switch (tok[0])
        {
            case "uci":
                lock (_outputLock)
                {
                    output.WriteLine($"id name {_backend.Name}");
                    output.WriteLine($"id author {_backend.Author}");
                    foreach (var option in _backend.OptionLines) output.WriteLine(option);
                    output.WriteLine("uciok");
                }
                return true;

            case "setoption":
                int nameIdx = Array.IndexOf(tok, "name");
                int valueIdx = Array.IndexOf(tok, "value");
                if (nameIdx < 0 || valueIdx < 0 || valueIdx <= nameIdx + 1 || valueIdx + 1 >= tok.Length) return true;
                _backend.SetOption(string.Join(' ', tok[(nameIdx + 1)..valueIdx]), string.Join(' ', tok[(valueIdx + 1)..]));
                return true;

            case "isready":
                // Slow setup happens here, where GUIs expect it — never inside "go", whose latency is game time.
                _backend.Prepare(info);
                lock (_outputLock) output.WriteLine("readyok");
                return true;

            case "ucinewgame":
                StopSearch();
                _backend.NewGame(info);
                return true;

            case "position":
                StopSearch();
                int fenIdx = Array.IndexOf(tok, "fen");
                int movesIdx = Array.IndexOf(tok, "moves");
                bool startpos = Array.IndexOf(tok, "startpos") >= 0;
                if (!startpos && fenIdx < 0) return true;
                string? fen = startpos ? null
                    : string.Join(' ', tok.Skip(fenIdx + 1).TakeWhile(static t => t != "moves").Take(6));
                _backend.SetPosition(fen, movesIdx >= 0 ? tok[(movesIdx + 1)..] : []);
                return true;

            case "go":
                // No first-time setup on the move clock: every real driver sends isready before the first go.
                // Missing required state is an explicit failed move, never an unannounced different player.
                if (!_backend.CanSearch(info))
                {
                    lock (_outputLock) output.WriteLine("bestmove 0000");
                    return true;
                }
                var limits = UciLimits.Decode(tok);
                StartSearch(_backend.BeginSearch(limits), output, waitForStop: limits.Infinite);
                return true;

            case "stop":
                StopSearch();
                return true;

            case "quit":
                StopSearch();
                return false;

            default:
                return true;
        }
    }

    private void Info(TextWriter? output, string msg)
    {
        if (output is null) return;
        lock (_outputLock)
        {
            output.WriteLine($"info string {msg}");
            output.Flush();
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return nl >= 0 ? s[..nl] : s;
    }

    // Runs the search on a background task so "stop" (and the next "position"/"quit") can be
    // read from stdin immediately instead of blocking behind the backend.
    private void StartSearch(Func<CancellationToken, Action<string>, ChessBackendResult> search, TextWriter output, bool waitForStop)
    {
        StopSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searchTask = Task.Run(() =>
        {
            try
            {
                var result = search(cts.Token, line =>
                {
                    lock (_outputLock)
                    {
                        output.WriteLine(line);
                        if (!line.StartsWith("info depth ", StringComparison.Ordinal)) output.Flush();
                    }
                });
                // A terminal position, a proven mate, or an explicit finite limit can finish
                // before an infinite analysis is stopped. Keep its actual result and wait
                // without spinning; UCI requires bestmove only after the caller's stop.
                if (waitForStop) cts.Token.WaitHandle.WaitOne();
                lock (_outputLock)
                {
                    foreach (var line in result.FinalLines) output.WriteLine(line);
                    output.WriteLine("bestmove " + result.BestMove + (result.Ponder is { } p ? " ponder " + p : ""));
                    output.Flush();
                }
            }
            catch (Exception ex)
            {
                lock (_outputLock)
                {
                    output.WriteLine($"info string search failed ({FirstLine(ex.Message)})");
                    output.WriteLine("bestmove 0000");
                    output.Flush();
                }
            }
        }); // Cancellation belongs inside the search: even an immediate stop must publish bestmove.
    }

    private void StopSearch()
    {
        _searchCts?.Cancel();
        try { _searchTask?.Wait(2000); } catch { /* best-effort; don't hang the UCI loop on a stuck search */ }
        if (_searchTask?.IsCompleted == true)
        {
            // Infinite analysis may have allocated the token's wait handle. Its worker is
            // finished now, so release that handle before starting another search.
            _searchCts?.Dispose();
            _searchCts = null;
            _searchTask = null;
        }
    }

    /// Blocks until any in-flight "go" search has written its bestmove, or the timeout elapses.
    /// "go" does not block, so an embedder that wants synchronous request/response behavior — a test
    /// harness, a non-interactive CLI use — needs this hook.
    public void WaitForIdle(int timeoutMs = 5000)
    {
        try { _searchTask?.Wait(timeoutMs); } catch { /* best-effort */ }
    }
}
