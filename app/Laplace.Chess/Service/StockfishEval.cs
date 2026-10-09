using Laplace.Chess.Service.Uci;

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
/// One Stockfish process evaluating at a fixed depth/node budget, over the generic UCI client (<see cref="UciProcess"/>).
/// Mate scores map to the same magnitude convention PgnEvals.ParseToken uses for "#N" tokens
/// so stockfish evals and PGN-carried evals are comparable on the HAS_EVAL axis.
/// </summary>
public sealed class StockfishProcessEvaluator : IPositionEvaluator, IDisposable
{
    private readonly UciProcess _engine;
    private readonly UciLimits _limits;
    private readonly int _timeoutSeconds;

    public StockfishEvaluationRecipe Recipe { get; }

    /// <summary>The engine as the generic client receipts it: path, SHA-256, UCI id.</summary>
    public EngineIdentity Identity => _engine.Identity;

    /// <summary>nodes &gt; 0 switches to a node-capped search ("go nodes N") — bounded worst
    /// case and reproducible testimony, where a depth budget has an unbounded tail on sharp
    /// positions (measured: depth 12 cost 4x depth 10 on corpus middlegames).</summary>
    public StockfishProcessEvaluator(string exePath, int depth, long nodes = 0)
        : this(exePath, StockfishEvaluationOptions.FromEnvironment(depth, nodes)) { }

    public StockfishProcessEvaluator(string exePath, StockfishEvaluationOptions options,
        StockfishEvaluationRecipe? expectedRecipe = null)
    {
        options.Validate();
        _limits = options.Nodes > 0 ? new UciLimits(Nodes: options.Nodes) : new UciLimits(Depth: options.Depth);
        _timeoutSeconds = options.TimeoutSeconds;
        string binaryBefore = StockfishEvaluationRecipe.FileSha256(exePath);
        try
        {
            _engine = UciProcess.Start(new UciProcessStart(exePath, HandshakeTimeout: TimeSpan.FromSeconds(10)));
        }
        catch (UciEngineException ex) when (ex.Failure == UciFailure.Unavailable)
        {
            throw new InvalidOperationException($"failed to start stockfish at {exePath}", ex);
        }
        catch (UciEngineException ex)
        {
            throw new InvalidDataException("Stockfish did not complete its UCI handshake: " + ex.Message, ex);
        }

        try
        {
            var effective = _engine.Options.Values.Where(static o => o.Type != "button")
                .ToDictionary(static o => o.Name, static o => o.Default, StringComparer.Ordinal);
            void Set(string name, string value, bool required = true)
            {
                if (!_engine.Supports(name))
                {
                    if (!required) return;
                    throw new InvalidDataException($"Stockfish does not support required evaluation option '{name}'.");
                }
                try { _engine.SetOption(name, value); }
                catch (UciEngineException ex) when (ex.InnerException is ArgumentException argument)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(argument).Throw();
                }
                effective[_engine.Options[name].Name] = value;
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
            try { _engine.IsReady(TimeSpan.FromSeconds(10)); }
            catch (UciEngineException ex) when (ex.Failure == UciFailure.Rejected)
            {
                throw new InvalidDataException("Stockfish rejected evaluation configuration: " + ex.Message, ex);
            }
            catch (UciEngineException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            if (StockfishEvaluationRecipe.FileSha256(exePath) != binaryBefore)
                throw new InvalidDataException("Stockfish executable changed during initialization; restart the evaluation job.");
            var capturedRecipe = StockfishEvaluationRecipe.Capture(exePath, _engine.Identity.Name, options, effective);
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
            _engine.Dispose();
            throw;
        }
    }

    public bool Broken => _engine.Broken;

    public int? EvaluateCp(string fen) => Analyse(fen)?.Final?.Score?.SideToMoveCentipawns;

    /// <summary>The typed analysis behind <see cref="EvaluateCp"/>: score, PV, bestmove and receipt. Null when the
    /// engine could not answer (it is then Broken) or the FEN is not one.</summary>
    public EngineAnalysis? Analyse(string fen)
    {
        if (Broken) return null;
        ChessPosition position;
        try { position = ChessPosition.From(fen); }
        catch (FormatException) { return null; }
        try
        {
            // Each cache entry witnesses one complete FEN under a cold search recipe.
            // Prior positions must not silently change a later value through TT/history.
            _engine.NewGame(TimeSpan.FromSeconds(10));
            return _engine.Search(position, _limits, TimeSpan.FromSeconds(_timeoutSeconds), "fresh");
        }
        catch (UciEngineException)
        {
            return null;
        }
    }

    public void Dispose() => _engine.Dispose();

    // NO FINALIZER. System.Diagnostics.Process has its own finalizer and there is no safe
    // finalization order between the two managed objects. Pool/process-exit disposal owns
    // normal teardown; the OS owns the last-resort child cleanup.
}

/// <summary>
/// Rent/return pool of evaluators for the compose workers, over the generic <see cref="EnginePool{T}"/>. Broken
/// engines are discarded on return and replaced lazily. All engines are killed on process exit.
/// </summary>
public sealed class StockfishEvaluatorPool : IDisposable
{
    private readonly EnginePool<IPositionEvaluator> _pool;

    public int Capacity => _pool.Capacity;

    public StockfishEvaluatorPool(Func<IPositionEvaluator> factory, int capacity = 1,
        IPositionEvaluator? initial = null)
        => _pool = new EnginePool<IPositionEvaluator>(factory,
            static e => e is StockfishProcessEvaluator { Broken: true }, capacity, initial);

    public IPositionEvaluator Rent(CancellationToken cancellationToken = default) => _pool.Rent(cancellationToken);

    public void Return(IPositionEvaluator evaluator) => _pool.Return(evaluator);

    public void Dispose() => _pool.Dispose();
}
