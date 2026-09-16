using System.Linq;
using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Uci;

public sealed class UciEngine
{
    public const string Name = "Laplace";
    public const string Author = "Laplace";

    private readonly ChessModality _modality = new();
    private ChessState _state;
    private Search _search = new();
    private readonly object _outputLock = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    // Substrate wiring. Guided mode keeps one conventional proposal tree but its configured
    // provider set now includes exact transition evidence, board-constituent outcome evidence,
    // the learned PST residual and exact Syzygy. "off" is the explicit classical control.
    // Provider setup happens on isready/ucinewgame, never after a move clock starts.
    private string _substrateMode =
        NormalizeMode(Environment.GetEnvironmentVariable("LAPLACE_UCI_SUBSTRATE")) ?? "substrate";
    private bool _substrateTried;
    private bool _searchStale = true;
    private string? _builtMode;
    private NpgsqlDataSource? _ds;
    private ChessSearchProviders? _providers;

    public UciEngine() => _state = _modality.Initial();

    public bool Handle(string line, TextWriter output)
    {
        var tok = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return true;

        switch (tok[0])
        {
            case "uci":
                lock (_outputLock)
                {
                    output.WriteLine($"id name {Name}");
                    output.WriteLine($"id author {Author}");
                    output.WriteLine($"option name Substrate type combo default {_substrateMode} var substrate var off");
                    output.WriteLine("uciok");
                }
                return true;

            case "setoption":
                ApplyOption(tok);
                return true;

            case "isready":
                // Substrate init happens here, where GUIs expect the engine to do slow setup —
                // never inside "go", whose latency is game time.
                _ = EnsureSearch(output, allowInit: true);
                lock (_outputLock) output.WriteLine("readyok");
                return true;

            case "ucinewgame":
                StopSearch();
                _state = _modality.Initial();
                // A prior game may have folded new move outcomes. Refresh the learned residual
                // now, outside the next move clock. Failure invalidates readiness instead of
                // silently running a different classical player under a substrate label.
                if (_substrateMode != "off" && _providers is not null)
                {
                    try { _providers.RefreshLearnedPst(); }
                    catch (Exception ex)
                    {
                        Info(output, $"learned PST refresh failed ({FirstLine(ex.Message)})");
                        _providers = null;
                        _substrateTried = false;
                    }
                }
                _searchStale = true;
                return true;

            case "position":
                StopSearch();
                SetPosition(tok);
                return true;

            case "go":
                // No first-time DB/provider init on the move clock: every real driver sends
                // isready before the first go. Missing required provider state is an explicit
                // failed move, never an unannounced classical player.
                if (!EnsureSearch(output, allowInit: false))
                {
                    lock (_outputLock) output.WriteLine("bestmove 0000");
                    return true;
                }
                StartSearch(ParseGo(tok), output, waitForStop: Array.IndexOf(tok, "infinite") >= 0);
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

    private static string? NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "substrate" or "fold" or "edge" => "substrate",
        "off" or "false" or "none" => "off",
        "true" or "on" => "substrate",
        _ => null,
    };

    private void ApplyOption(string[] tok)
    {
        int nameIdx = Array.IndexOf(tok, "name");
        int valueIdx = Array.IndexOf(tok, "value");
        if (nameIdx < 0 || valueIdx < 0 || valueIdx <= nameIdx + 1 || valueIdx + 1 >= tok.Length) return;
        string name = string.Join(' ', tok[(nameIdx + 1)..valueIdx]);
        string value = string.Join(' ', tok[(valueIdx + 1)..]);

        if (name.Equals("Substrate", StringComparison.OrdinalIgnoreCase)
            && NormalizeMode(value) is { } mode && mode != _substrateMode)
        {
            _substrateMode = mode;
            _searchStale = true;
        }
    }

    // (Re)build the search to match the requested mode. allowInit gates slow provider setup.
    // False means the requested engine is not ready; it never authorizes a different player.
    private bool EnsureSearch(TextWriter? output, bool allowInit)
    {
        if (!_searchStale && _builtMode == _substrateMode) return true;

        if (_substrateMode == "off")
        {
            _search = new Search();
            _builtMode = "off";
            _searchStale = false;
            return true;
        }

        if (!_substrateTried || _providers is null)
        {
            if (!allowInit)
            {
                Info(output, "substrate mode requires a successful isready initialization");
                return false;
            }
            _substrateTried = true;
            try
            {
                CodepointPerfcache.LoadDefault();
                if (_ds is null)
                {
                    var basis = new NpgsqlConnectionStringBuilder(ChessEngineService.ResolveConnString())
                    {
                        Timeout = 3,
                        CommandTimeout = 5,
                    }.ConnectionString;
                    var ds = LaplaceDataSource.Create(SubstrateAccess.Serving, basis);
                    using (ds.OpenConnection()) { } // fail fast while setup time is available
                    _ds = ds;
                }
                _providers = new ChessSearchProviders(_ds);
            }
            catch (Exception ex)
            {
                _substrateTried = false;
                _providers = null;
                Info(output, $"substrate provider initialization failed ({FirstLine(ex.Message)})");
                return false;
            }
        }

        if (_providers is null)
        {
            Info(output, "substrate initialization did not produce a provider stack");
            return false;
        }

        var configured = _providers.Configure(substrate: true);
        _search = configured.BuildSearch(ttBits: 20);
        _builtMode = _substrateMode;
        _searchStale = false;
        var prepared = configured.Receipt();
        Info(output,
            $"substrate provider stack prepared (learned-pst " +
            $"{(prepared.LearnedPstContributes ? $"active:{prepared.LearnedPstNonZeroCells}" : "selected:no-delta")}, " +
            $"syzygy {prepared.SyzygyLargestMen}-men); per-search usage is receipted after go");
        return true;
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
    // read from stdin immediately instead of blocking behind Think(). Each substrate search gets
    // a fresh counting configuration so the receipt belongs to THIS go, not process lifetime.
    private void StartSearch(Search.Limits limits, TextWriter output, bool waitForStop)
    {
        StopSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        // Board is mutable; repetition history is immutable. Snapshot both so a later UCI
        // "position" command cannot mutate the in-flight decision's game context.
        var state = new ChessState(
            Board.FromFen(_state.Board.ToFen()),
            _state.RepetitionHistory);
        var search = _search;
        ChessSearchConfiguration? configured = null;
        if (_substrateMode != "off" && _providers is not null)
        {
            configured = _providers.Configure(substrate: true);
            configured.ApplyTo(search);
        }

        _searchTask = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = search.Think(state, limits, cts.Token, iteration =>
                {
                    var providers = configured?.Receipt() ?? ChessSearchProviderReceipt.Classical;
                    lock (_outputLock)
                    {
                        output.WriteLine(
                            $"info depth {iteration.Depth} score {ScoreStr(iteration.Score)} " +
                            $"nodes {iteration.Nodes} time {iteration.ElapsedMilliseconds} " +
                            $"nps {(long)(iteration.Nodes * 1000.0 / Math.Max(1, iteration.ElapsedMilliseconds))} " +
                            $"pv {iteration.BestMove.ToUci()}");
                        output.WriteLine($"info string providers depth {iteration.Depth} {providers.Summary}");
                        output.Flush();
                    }
                });
                sw.Stop();
                // A terminal position, a proven mate, or an explicit finite limit can finish
                // Think before an infinite analysis is stopped. Keep its actual result and
                // wait without spinning; UCI requires bestmove only after the caller's stop.
                if (waitForStop) cts.Token.WaitHandle.WaitOne();
                string best = result.BestMove?.ToUci() ?? "0000";
                var receipt = configured?.Receipt() ?? ChessSearchProviderReceipt.Classical;
                lock (_outputLock)
                {
                    output.WriteLine(
                        $"info depth {result.Depth} score {ScoreStr(result.Score)} " +
                        $"nodes {result.Nodes} time {sw.ElapsedMilliseconds} pv {best}");
                    output.WriteLine($"info string providers {receipt.Summary}");
                    output.WriteLine($"bestmove {best}");
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
        }); // Cancellation belongs inside Think: even immediate stop must publish bestmove.
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
    /// "go" itself no longer blocks (see StartSearch), so an embedder that wants synchronous
    /// request/response behavior — a test harness, a non-interactive CLI use — needs this hook.
    public void WaitForIdle(int timeoutMs = 5000)
    {
        try { _searchTask?.Wait(timeoutMs); } catch { /* best-effort */ }
    }

    private void SetPosition(string[] tok)
    {
        try
        {
            int startIdx = Array.IndexOf(tok, "startpos");
            int fenIdx = Array.IndexOf(tok, "fen");
            ChessState next = startIdx >= 0
                ? _modality.Initial()
                : fenIdx >= 0
                    ? _modality.FromFen(string.Join(' ', tok.Skip(fenIdx + 1).Take(6)))
                    : _state;

            int movesIdx = Array.IndexOf(tok, "moves");
            if (movesIdx >= 0)
                for (int k = movesIdx + 1; k < tok.Length; k++)
                    next = ApplyUciMove(next, tok[k]);

            _state = next;
        }
        catch (FormatException)
        {
            // Malformed "position fen ..." must not crash the engine process — keep whatever
            // position was already current, same as how a real UCI engine degrades.
        }
    }

    private ChessState ApplyUciMove(ChessState state, string uci)
    {
        foreach (var m in MoveGen.Legal(state.Board))
            if (m.ToUci() == uci)
                return _modality.Apply(state, m);
        return state;
    }

    private static string ScoreStr(int score)
    {
        const int mate = 30_000, threshold = mate - 1_000;
        if (Math.Abs(score) < threshold) return $"cp {score}";
        int pliesToMate = mate - Math.Abs(score);
        int moves = (pliesToMate + 1) / 2;
        return $"mate {(score > 0 ? moves : -moves)}";
    }

    private Search.Limits ParseGo(string[] tok)
    {
        long? Number(string key)
        {
            int i = Array.IndexOf(tok, key);
            return i >= 0 && i + 1 < tok.Length
                && long.TryParse(tok[i + 1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value : null;
        }

        // Independent UCI limits combine; depth must not discard a declared clock or node
        // budget. Missing time/nodes retain Search's existing unbounded representation.
        int depth = Number("depth") is > 0 and var requestedDepth
            ? (int)Math.Min(64, requestedDepth) : 64;
        long nodes = Number("nodes") is > 0 and var requestedNodes ? requestedNodes : long.MaxValue;
        int milliseconds = Number("movetime") is { } movetime
            ? (int)Math.Clamp(movetime, 1, int.MaxValue) : int.MaxValue;

        if (Number(_state.Board.WhiteToMove ? "wtime" : "btime") is { } remaining)
        {
            long myTime = Math.Min(remaining, int.MaxValue);
            long myInc = Math.Min(Number(_state.Board.WhiteToMove ? "winc" : "binc") ?? 0, int.MaxValue);
            long moves = Number("movestogo") is > 0 and var movesToGo ? movesToGo : 30;
            // Preserve the clock allocation policy, now using the caller's moves-to-go and
            // wide arithmetic. A zero/exhausted clock cannot become an absent time limit.
            int budget = (int)Math.Clamp(Math.Min(myTime - 30, myTime / moves + myInc * 4 / 5),
                1, int.MaxValue);
            milliseconds = Math.Min(milliseconds, budget);
        }

        return new Search.Limits(MaxDepth: depth, MaxNodes: nodes, MaxTimeMs: milliseconds);
    }
}
