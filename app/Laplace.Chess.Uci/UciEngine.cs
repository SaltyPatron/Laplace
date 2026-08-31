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

    private Board _board = Board.FromFen(ChessModality.StartFen);
    private Search _search = new();
    private readonly object _outputLock = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    // Substrate wiring. Guided modes keep the full chess search, add exact-transition root
    // evidence and evaluate reusable board constituents throughout the tree. "off" is the
    // same conventional search without those substrate inputs. Substrate mode fails visibly
    // when its required data is unavailable; only an explicit "off" selects classical play.
    private string _substrateMode =
        NormalizeMode(Environment.GetEnvironmentVariable("LAPLACE_UCI_SUBSTRATE")) ?? "substrate";
    private bool _substrateTried;
    private bool _searchStale = true;
    private string? _builtMode;
    private NpgsqlDataSource? _ds;
    private IRootBias? _bias;

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
                _board = Board.FromFen(ChessModality.StartFen);
                // Learned PST comes from consensus that the previous game may have folded into.
                _searchStale = true;
                return true;

            case "position":
                StopSearch();
                SetPosition(tok);
                return true;

            case "go":
                // No first-time DB init on the move clock: every real driver sends isready
                // before the first go (cutechess does). Missing required substrate state is
                // an explicit failed move, never an unannounced classical player.
                if (!EnsureSearch(output, allowInit: false))
                {
                    lock (_outputLock) output.WriteLine("bestmove 0000");
                    return true;
                }
                StartSearch(ParseGo(tok), output);
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

    // (Re)build the search to match the requested substrate mode. Cheap no-op when current.
    // allowInit gates the one-time DB connection. False means the requested engine is not ready;
    // it never authorizes a different player.
    private bool EnsureSearch(TextWriter? output, bool allowInit)
    {
        if (!_searchStale && _builtMode == _substrateMode) return true;

        if (_substrateMode == "off")
        {
            _search = new Search();
            _bias = null;
            _builtMode = "off";
            _searchStale = false;
            return true;
        }

        if (!_substrateTried)
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
                var basis = new NpgsqlConnectionStringBuilder(ChessEngineService.ResolveConnString())
                {
                    Timeout = 3,
                    CommandTimeout = 5,
                }.ConnectionString;
                var ds = LaplaceDataSource.Create(SubstrateAccess.Serving, basis);
                using (ds.OpenConnection()) { } // fail fast while we can still report it
                _ds = ds;
            }
            catch (Exception ex)
            {
                _substrateTried = false;
                Info(output, $"substrate unavailable ({FirstLine(ex.Message)})");
                return false;
            }
        }

        if (_ds is null)
        {
            Info(output, "substrate initialization did not produce a serving connection");
            return false;
        }

        SubstrateBoardEvaluator evaluator;
        try
        {
            evaluator = new SubstrateBoardEvaluator(_ds);
        }
        catch (Exception ex)
        {
            _substrateTried = false;
            Info(output, $"substrate position census unavailable ({FirstLine(ex.Message)})");
            return false;
        }

        IRootBias inner = new SubstrateRootBias(_ds);
        _bias = inner;
        _search = new Search(
            EvalTerm.All, _bias, ttBits: 20,
            positionEvaluator: evaluator,
            tablebase: ChessTablebaseRuntime.ProbeSearch);
        _builtMode = _substrateMode;
        _searchStale = false;
        Info(output,
            $"substrate-guided search active (mode {_substrateMode}, position atoms {evaluator.LoadedAtoms}, syzygy {ChessTablebaseRuntime.Largest}-men)");
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
    // read from stdin immediately instead of blocking behind Think() — real UCI GUIs (including
    // cutechess-cli, which drives this exact path with tc=inf/depth=N, i.e. no time control at
    // all) expect "stop" to be honored promptly, not just accepted and ignored.
    private void StartSearch(Search.Limits limits, TextWriter output)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var board = Board.FromFen(_board.ToFen()); // stable snapshot; _board may be reassigned by a later "position"
        var search = _search; // stable snapshot; a later setoption may rebuild _search
        _searchTask = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = search.Think(board, limits, cts.Token);
                sw.Stop();
                string best = result.BestMove?.ToUci() ?? "0000";
                lock (_outputLock)
                {
                    output.WriteLine(
                        $"info depth {result.Depth} score {ScoreStr(result.Score)} " +
                        $"nodes {result.Nodes} time {sw.ElapsedMilliseconds} pv {best}");
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
        }, cts.Token);
    }

    private void StopSearch()
    {
        _searchCts?.Cancel();
        try { _searchTask?.Wait(2000); } catch { /* best-effort; don't hang the UCI loop on a stuck search */ }
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
            Board next = startIdx >= 0
                ? Board.FromFen(ChessModality.StartFen)
                : fenIdx >= 0 ? Board.FromFen(string.Join(' ', tok.Skip(fenIdx + 1).Take(6))) : _board;

            int movesIdx = Array.IndexOf(tok, "moves");
            if (movesIdx >= 0)
                for (int k = movesIdx + 1; k < tok.Length; k++)
                    ApplyUciMove(next, tok[k]);

            _board = next;
        }
        catch (FormatException)
        {
            // Malformed "position fen ..." must not crash the engine process — keep whatever
            // position was already current, same as how a real UCI engine degrades.
        }
    }

    private static void ApplyUciMove(Board board, string uci)
    {
        foreach (var m in MoveGen.Legal(board))
            if (m.ToUci() == uci) { MoveApply.Make(board, m); return; }
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
        int Int(string key, int def)
        {
            int i = Array.IndexOf(tok, key);
            return i >= 0 && i + 1 < tok.Length && int.TryParse(tok[i + 1], out var v) ? v : def;
        }

        int depth = Int("depth", 0);
        // A bounded ceiling even for an explicit depth request — "go depth N" with no other time
        // control (e.g. cutechess-cli's tc=inf/depth=N, the exact invocation this engine is
        // actually driven by) previously left MaxTimeMs at Limits' int.MaxValue default, so a
        // pathological position could hang the process indefinitely with no way to recover.
        if (depth > 0) return new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 64), MaxTimeMs: 120_000);

        int movetime = Int("movetime", 0);
        if (movetime > 0) return new Search.Limits(MaxDepth: 64, MaxTimeMs: Math.Max(10, movetime - 20));

        int wtime = Int("wtime", 0), btime = Int("btime", 0), winc = Int("winc", 0), binc = Int("binc", 0);
        if (wtime > 0 || btime > 0)
        {
            int myTime = _board.WhiteToMove ? wtime : btime;
            int myInc = _board.WhiteToMove ? winc : binc;
            int budget = Math.Max(10, Math.Min(myTime - 30, myTime / 30 + (int)(myInc * 0.8)));
            return new Search.Limits(MaxDepth: 64, MaxTimeMs: budget);
        }

        return new Search.Limits(MaxDepth: 64, MaxNodes: 1_000_000, MaxTimeMs: 2000);
    }
}
