using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

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
/// One stockfish process speaking UCI, evaluated synchronously at a fixed depth/node budget.
/// Mate scores map to the same magnitude convention PgnEvals.ParseToken uses for "#N" tokens
/// so stockfish evals and PGN-carried evals are comparable on the HAS_EVAL axis.
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

        // Redirected stderr used to be left unread forever. A noisy/broken engine could fill
        // that pipe and block before stdout produced bestmove, presenting as an ingest hang.
        _proc.ErrorDataReceived += static (_, _) => { };
        _proc.BeginErrorReadLine();

        try
        {
            Send("uci");
            WaitFor("uciok", TimeSpan.FromSeconds(10));
            // One thread per engine instance — parallelism comes from the pool, not from
            // oversubscribing each engine against the compose workers.
            Send("setoption name Threads value 1");
            Send("setoption name Hash value 16");
            Send("isready");
            WaitFor("readyok", TimeSpan.FromSeconds(10));
        }
        catch
        {
            TerminateProcess();
            _proc.Dispose();
            throw;
        }
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
            long deadline = DeadlineAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                string? line = ReadLineUntil(deadline);
                if (line is null)
                {
                    _broken = true;
                    return null;
                }
                if (line.StartsWith("bestmove", StringComparison.Ordinal)) return last;
                int si = line.IndexOf(" score ", StringComparison.Ordinal);
                if (si < 0) continue;
                var tok = line[(si + 7)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;
                if (tok[0] == "cp" && int.TryParse(tok[1], out int cp))
                    last = cp;
                else if (tok[0] == "mate" && int.TryParse(tok[1], out int mate))
                    last = mate == 0 ? -20_000
                         : Math.Sign(mate) * (20_000 - Math.Min(Math.Abs(mate), 100) * 100);
            }
        }
        catch (TimeoutException)
        {
            // ReadLine() used to block *inside* the deadline loop, so the 30-second timeout
            // was fictitious. ReadLineAsync+WaitAsync makes the deadline enforceable even if
            // the engine stops producing output.
            _broken = true;
            try { Send("stop"); } catch { }
            return null;
        }
        catch (Exception)
        {
            _broken = true;
            return null;
        }
    }

    private void Send(string cmd)
    {
        _proc.StandardInput.WriteLine(cmd);
        _proc.StandardInput.Flush();
    }

    private void WaitFor(string marker, TimeSpan timeout)
    {
        long deadline = DeadlineAfter(timeout);
        while (true)
        {
            string? line;
            try { line = ReadLineUntil(deadline); }
            catch (TimeoutException)
            {
                _broken = true;
                throw new InvalidOperationException($"stockfish never answered '{marker}' before timeout");
            }
            if (line is null) break;
            if (line.StartsWith(marker, StringComparison.Ordinal)) return;
        }
        _broken = true;
        throw new InvalidOperationException($"stockfish never answered '{marker}'");
    }

    private string? ReadLineUntil(long deadline)
    {
        long remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) throw new TimeoutException();
        var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
        return _proc.StandardOutput.ReadLineAsync()
            .WaitAsync(remaining)
            .GetAwaiter()
            .GetResult();
    }

    private static long DeadlineAfter(TimeSpan timeout)
    {
        double ticks = timeout.TotalSeconds * Stopwatch.Frequency;
        return checked(Stopwatch.GetTimestamp() + (long)Math.Ceiling(ticks));
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_proc.HasExited) Send("quit"); } catch { }
        TerminateProcess();
        _proc.Dispose();
    }

    private void TerminateProcess()
    {
        try
        {
            if (!_proc.HasExited && !_proc.WaitForExit(1000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
    }

    // NO FINALIZER. System.Diagnostics.Process has its own finalizer and there is no safe
    // finalization order between the two managed objects. Pool/process-exit disposal owns
    // normal teardown; the OS owns the last-resort child cleanup.
}

/// <summary>
/// Rent/return pool of evaluators for the compose workers. Broken engines are discarded on
/// return and replaced lazily. All engines are killed on process exit.
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
