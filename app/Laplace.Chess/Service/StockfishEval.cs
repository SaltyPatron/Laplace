using System.Diagnostics;
using System.Collections.Concurrent;

namespace Laplace.Chess.Service;

/// <summary>
/// Side-to-move centipawn evaluation of a single position. Null = no evaluation
/// available (terminal position, engine died, malformed FEN) — never a fabricated 0.
/// </summary>
public interface IPositionEvaluator
{
    int? EvaluateCp(string fen);
}

/// <summary>
/// One stockfish process speaking UCI, evaluated synchronously at a fixed depth.
/// Mate scores map to the same magnitude convention PgnEvals.ParseToken uses for
/// "#N" tokens (sign · (20000 − N·100)), so stockfish evals and PGN-carried evals
/// are comparable on the HAS_EVAL axis.
/// </summary>
public sealed class StockfishProcessEvaluator : IPositionEvaluator, IDisposable
{
    private readonly Process _proc;
    private readonly int _depth;
    private readonly long _nodes;
    private bool _broken;

    /// <summary>nodes &gt; 0 switches to a node-capped search ("go nodes N") — bounded worst
    /// case and reproducible testimony, where a depth budget has an unbounded tail on sharp
    /// positions (measured: depth 12 cost 4x depth 10 on corpus middlegames).</summary>
    public StockfishProcessEvaluator(string exePath, int depth, long nodes = 0)
    {
        _depth = Math.Clamp(depth, 1, 40);
        _nodes = nodes;
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"failed to start stockfish at {exePath}");

        Send("uci");
        WaitFor("uciok", TimeSpan.FromSeconds(10));
        // One thread per engine instance — parallelism comes from the pool, not from
        // oversubscribing each engine against the compose workers.
        Send("setoption name Threads value 1");
        Send("setoption name Hash value 16");
        Send("isready");
        WaitFor("readyok", TimeSpan.FromSeconds(10));
    }

    public bool Broken => _broken || _proc.HasExited;

    public int? EvaluateCp(string fen)
    {
        if (Broken) return null;
        try
        {
            Send($"position fen {fen}");
            Send(_nodes > 0 ? $"go nodes {_nodes}" : $"go depth {_depth}");

            int? last = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                string? line = _proc.StandardOutput.ReadLine();
                if (line is null) { _broken = true; return null; }
                if (line.StartsWith("bestmove", StringComparison.Ordinal)) return last;
                int si = line.IndexOf(" score ", StringComparison.Ordinal);
                if (si < 0) continue;
                var tok = line[(si + 7)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;
                if (tok[0] == "cp" && int.TryParse(tok[1], out int cp))
                    last = cp;
                else if (tok[0] == "mate" && int.TryParse(tok[1], out int mate))
                    // mate 0 = side to move is already mated (checkmate delivered against them).
                    last = mate == 0 ? -20_000
                         : Math.Sign(mate) * (20_000 - Math.Min(Math.Abs(mate), 100) * 100);
            }
            _broken = true; // engine hung past the deadline — stop trusting this instance
            return null;
        }
        catch (Exception)
        {
            _broken = true;
            return null;
        }
    }

    private void Send(string cmd) => _proc.StandardInput.WriteLine(cmd);

    private void WaitFor(string marker, TimeSpan timeout)
    {
        _proc.StandardInput.Flush();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string? line = _proc.StandardOutput.ReadLine();
            if (line is null) break;
            if (line.StartsWith(marker, StringComparison.Ordinal)) return;
        }
        _broken = true;
        throw new InvalidOperationException($"stockfish never answered '{marker}'");
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) Send("quit"); } catch { }
        try { if (!_proc.WaitForExit(1000)) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
        GC.SuppressFinalize(this);
    }

    ~StockfishProcessEvaluator()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
    }
}

/// <summary>
/// Rent/return pool of evaluators for the parallel compose workers. Broken engines are
/// discarded on return and replaced lazily. All engines are killed on process exit —
/// decomposers have no disposal lifecycle in the ingest runner, so the pool guards itself.
/// </summary>
public sealed class StockfishEvaluatorPool : IDisposable
{
    private readonly Func<IPositionEvaluator> _factory;
    private readonly ConcurrentBag<IPositionEvaluator> _idle = new();
    private readonly ConcurrentBag<IPositionEvaluator> _all = new();
    private bool _disposed;

    public StockfishEvaluatorPool(Func<IPositionEvaluator> factory)
    {
        _factory = factory;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public IPositionEvaluator Rent()
    {
        if (_idle.TryTake(out var e))
        {
            if (e is not StockfishProcessEvaluator { Broken: true }) return e;
            (e as IDisposable)?.Dispose();
        }
        var fresh = _factory();
        _all.Add(fresh);
        return fresh;
    }

    public void Return(IPositionEvaluator evaluator)
    {
        if (_disposed || evaluator is StockfishProcessEvaluator { Broken: true })
        {
            (evaluator as IDisposable)?.Dispose();
            return;
        }
        _idle.Add(evaluator);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var e in _all)
            (e as IDisposable)?.Dispose();
    }
}
