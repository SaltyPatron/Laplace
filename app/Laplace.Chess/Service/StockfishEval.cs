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
    private readonly int _timeoutSeconds;
    private bool _broken;

    public StockfishEvaluationRecipe Recipe { get; }

    /// <summary>nodes &gt; 0 switches to a node-capped search ("go nodes N") — bounded worst
    /// case and reproducible testimony, where a depth budget has an unbounded tail on sharp
    /// positions (measured: depth 12 cost 4x depth 10 on corpus middlegames).</summary>
    public StockfishProcessEvaluator(string exePath, int depth, long nodes = 0)
        : this(exePath, StockfishEvaluationOptions.FromEnvironment(depth, nodes)) { }

    public StockfishProcessEvaluator(string exePath, StockfishEvaluationOptions options,
        StockfishEvaluationRecipe? expectedRecipe = null)
    {
        options.Validate();
        _depth = options.Depth;
        _nodes = options.Nodes;
        _timeoutSeconds = options.TimeoutSeconds;
        string binaryBefore = StockfishEvaluationRecipe.FileSha256(exePath);
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
            var advertised = new Dictionary<string, UciOption>(StringComparer.Ordinal);
            string? engineName = null;
            Send("uci");
            WaitFor("uciok", TimeSpan.FromSeconds(10), line =>
            {
                if (line.StartsWith("id name ", StringComparison.Ordinal)) engineName = line[8..];
                if (UciOption.Parse(line) is { } option) advertised[option.Name] = option;
            });
            if (string.IsNullOrWhiteSpace(engineName))
                throw new InvalidDataException("Stockfish did not report its UCI engine identity.");
            var effective = advertised.Values.ToDictionary(static o => o.Name, static o => o.Default, StringComparer.Ordinal);
            void Set(string name, string value, bool required = true)
            {
                if (!advertised.TryGetValue(name, out var option))
                {
                    if (!required) return;
                    throw new InvalidDataException($"Stockfish does not support required evaluation option '{name}'.");
                }
                option.Validate(value);
                Send($"setoption name {name} value {value}");
                effective[name] = value;
            }
            Set("NumaPolicy", options.NumaPolicy, required: options.NumaPolicy != "auto");
            Set("Threads", options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("Hash", options.HashMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("UCI_LimitStrength", "false");
            Set("Skill Level", "20");
            Set("Ponder", "false");
            Set("MultiPV", "1");
            if (!string.IsNullOrWhiteSpace(options.EvalFile))
                Set("EvalFile", Path.GetFullPath(options.EvalFile));
            string tablePath = string.IsNullOrWhiteSpace(options.SyzygyPath) ? ""
                : string.Join(Path.PathSeparator, options.SyzygyPath.Split(Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SelectMany(static root => ChessInput.SyzygyProbePath(root).Split(Path.PathSeparator))
                    .Distinct(StringComparer.Ordinal));
            Set("SyzygyPath", tablePath, required: tablePath.Length > 0);
            Send("isready");
            WaitFor("readyok", TimeSpan.FromSeconds(10), line =>
            {
                if (line.Contains("invalid value", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("No such option", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Stockfish rejected evaluation configuration: " + line);
            });
            if (StockfishEvaluationRecipe.FileSha256(exePath) != binaryBefore)
                throw new InvalidDataException("Stockfish executable changed during initialization; restart the evaluation job.");
            var capturedRecipe = StockfishEvaluationRecipe.Capture(exePath, engineName, options, effective);
            if (capturedRecipe.EngineSha256 != binaryBefore)
                throw new InvalidDataException("Stockfish executable changed while capturing its evaluation recipe.");
            if (expectedRecipe is not null)
            {
                // A newly acquired process may load external networks/tables again. Verify
                // their exact bytes once here; never reuse a metadata-only fingerprint or
                // hash this artifact set inside the position/search hot path.
                if (expectedRecipe.Id != capturedRecipe.Id
                    || expectedRecipe.Settings != options
                    || !expectedRecipe.EffectiveOptions.OrderBy(static p => p.Key, StringComparer.Ordinal)
                        .SequenceEqual(effective.OrderBy(static p => p.Key, StringComparer.Ordinal)))
                    throw new InvalidDataException("Stockfish engine, options or external artifacts changed during this evaluation job; start a new recipe scope.");
                Recipe = expectedRecipe;
            }
            else
                Recipe = capturedRecipe;
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
            // Each cache entry witnesses one complete FEN under a cold search recipe.
            // Prior positions must not silently change a later value through TT/history.
            Send("ucinewgame");
            Send("isready");
            WaitFor("readyok", TimeSpan.FromSeconds(10));
            Send($"position fen {fen}");
            Send(_nodes > 0 ? $"go nodes {_nodes}" : $"go depth {_depth}");

            int? last = null;
            long deadline = DeadlineAfter(TimeSpan.FromSeconds(_timeoutSeconds));
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

    private void WaitFor(string marker, TimeSpan timeout, Action<string>? observe = null)
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
            observe?.Invoke(line);
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

    private sealed record UciOption(string Name, string Type, string Default, long? Min, long? Max)
    {
        public static UciOption? Parse(string line)
        {
            const string prefix = "option name ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int typeAt = line.IndexOf(" type ", prefix.Length, StringComparison.Ordinal);
            if (typeAt < 0) return null;
            string rest = line[(typeAt + 6)..];
            string type = rest.Split(' ', 2)[0];
            if (type == "button") return null;
            string Field(string name)
            {
                int start = rest.IndexOf(" " + name + " ", StringComparison.Ordinal);
                if (start < 0) return "";
                start += name.Length + 2;
                int end = rest.Length;
                foreach (var marker in new[] { " min ", " max ", " var " })
                {
                    int next = rest.IndexOf(marker, start, StringComparison.Ordinal);
                    if (next >= 0) end = Math.Min(end, next);
                }
                return rest[start..end];
            }
            return new UciOption(line[prefix.Length..typeAt], type, Field("default"),
                long.TryParse(Field("min"), out var min) ? min : null,
                long.TryParse(Field("max"), out var max) ? max : null);
        }

        public void Validate(string value)
        {
            if (value.Contains('\n') || value.Contains('\r'))
                throw new ArgumentException($"Stockfish option {Name} must be a single UCI value.");
            if (Type == "spin" && (!long.TryParse(value, out var number)
                || Min.HasValue && number < Min || Max.HasValue && number > Max))
                throw new ArgumentOutOfRangeException(Name, $"Stockfish advertises {Name} range {Min}..{Max}.");
        }
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
    private readonly object _gate = new();
    private readonly Stack<IPositionEvaluator> _idle = new();
    private readonly HashSet<IPositionEvaluator> _all = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IPositionEvaluator> _leased = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public int Capacity { get; }

    public StockfishEvaluatorPool(Func<IPositionEvaluator> factory, int capacity = 1,
        IPositionEvaluator? initial = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _factory = factory;
        Capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
        if (initial is not null)
        {
            _idle.Push(initial);
            _all.Add(initial);
        }
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public IPositionEvaluator Rent(CancellationToken cancellationToken = default)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _slots.Wait(waiting.Token);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                while (_idle.TryPop(out var evaluator))
                {
                    if (evaluator is StockfishProcessEvaluator { Broken: true })
                    {
                        _all.Remove(evaluator);
                        (evaluator as IDisposable)?.Dispose();
                        continue;
                    }
                    _leased.Add(evaluator);
                    return evaluator;
                }
            }
            // Engine startup is outside the bookkeeping lock. The acquired semaphore slot
            // owns its capacity, including failed or cancelled startup.
            var fresh = _factory();
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    (fresh as IDisposable)?.Dispose();
                    waiting.Token.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(nameof(StockfishEvaluatorPool));
                }
                _all.Add(fresh);
                _leased.Add(fresh);
            }
            return fresh;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public void Return(IPositionEvaluator evaluator)
    {
        bool discard;
        lock (_gate)
        {
            if (!_leased.Remove(evaluator))
                throw new InvalidOperationException("Evaluator was not leased by this pool, or was already returned.");
            discard = _disposed || evaluator is StockfishProcessEvaluator { Broken: true };
            if (discard) _all.Remove(evaluator);
            else _idle.Push(evaluator);
        }
        try { if (discard) (evaluator as IDisposable)?.Dispose(); }
        finally { _slots.Release(); }
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        IPositionEvaluator[] engines;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            engines = _all.ToArray();
            _all.Clear();
            _idle.Clear();
        }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _lifetime.Cancel();
        foreach (var evaluator in engines) (evaluator as IDisposable)?.Dispose();
        // Outstanding leases may still return after disposal. Keep the semaphore and CTS
        // usable until those managed owners disappear; neither allocates a native wait handle.
    }
}
