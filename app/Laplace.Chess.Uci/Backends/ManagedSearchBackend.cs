using global::Npgsql;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Uci.Backends;

/// <summary>
/// The stand-in backend: the existing C# provider stack and <see cref="Search"/>, unchanged, behind
/// <see cref="IChessBackend"/>. The native core replaces this class, not the protocol around it.
/// </summary>
public sealed class ManagedSearchBackend : IChessBackend
{
    private readonly ChessModality _modality = new();
    private ChessState _state;
    private Search _search = new();

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

    public ManagedSearchBackend() => _state = _modality.Initial();

    public string Name => "Laplace";
    public string Author => "Laplace";
    public IReadOnlyList<string> OptionLines =>
        [$"option name Substrate type combo default {_substrateMode} var substrate var off"];

    public void SetOption(string name, string value)
    {
        if (name.Equals("Substrate", StringComparison.OrdinalIgnoreCase)
            && NormalizeMode(value) is { } mode && mode != _substrateMode)
        {
            _substrateMode = mode;
            _searchStale = true;
        }
    }

    public void Prepare(Action<string> info) => _ = EnsureSearch(info, allowInit: true);

    public void NewGame(Action<string> info)
    {
        _state = _modality.Initial();
        // A prior game may have folded new move outcomes. Refresh the learned residual
        // now, outside the next move clock. Failure invalidates readiness instead of
        // silently running a different classical player under a substrate label.
        if (_substrateMode != "off" && _providers is not null)
        {
            try { _providers.RefreshLearnedPst(); }
            catch (Exception ex)
            {
                info($"learned PST refresh failed ({FirstLine(ex.Message)})");
                _providers = null;
                _substrateTried = false;
            }
        }
        _searchStale = true;
    }

    public void SetPosition(string? fen, IReadOnlyList<string> moves)
    {
        try
        {
            ChessState next = fen is null ? _modality.Initial() : _modality.FromFen(fen);
            foreach (var move in moves) next = ApplyUciMove(next, move);
            _state = next;
        }
        catch (FormatException)
        {
            // Malformed "position fen ..." must not crash the engine process — keep whatever
            // position was already current, same as how a real UCI engine degrades.
        }
    }

    public bool CanSearch(Action<string> info) => EnsureSearch(info, allowInit: false);

    public Func<CancellationToken, Action<string>, ChessBackendResult> BeginSearch(UciLimits uciLimits)
    {
        // Board is mutable; repetition history is immutable. Snapshot both so a later UCI
        // "position" command cannot mutate the in-flight decision's game context.
        var state = new ChessState(Board.FromFen(_state.Board.ToFen()), _state.RepetitionHistory);
        var limits = ToSearchLimits(uciLimits);
        var search = _search;
        ChessSearchConfiguration? configured = null;
        if (_substrateMode != "off" && _providers is not null)
        {
            // Each substrate search gets a fresh counting configuration so the receipt belongs to THIS go.
            configured = _providers.Configure(substrate: true);
            configured.ApplyTo(search);
        }
        return (ct, emit) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = search.Think(state, limits, ct, iteration =>
            {
                var providers = configured?.Receipt() ?? ChessSearchProviderReceipt.Classical;
                emit($"info depth {iteration.Depth} score {ScoreStr(iteration.Score)} " +
                     $"nodes {iteration.Nodes} time {iteration.ElapsedMilliseconds} " +
                     $"nps {(long)(iteration.Nodes * 1000.0 / Math.Max(1, iteration.ElapsedMilliseconds))} " +
                     $"pv {iteration.BestMove.ToUci()}");
                emit($"info string providers depth {iteration.Depth} {providers.Summary}");
            });
            sw.Stop();
            string best = result.BestMove?.ToUci() ?? "0000";
            var receipt = configured?.Receipt() ?? ChessSearchProviderReceipt.Classical;
            return new ChessBackendResult(best,
            [
                $"info depth {result.Depth} score {ScoreStr(result.Score)} nodes {result.Nodes} time {sw.ElapsedMilliseconds} pv {best}",
                $"info string providers {receipt.Summary}",
            ]);
        };
    }

    /// <summary>The UCI limits as Search's limits. Independent UCI limits combine; depth must not discard a declared
    /// clock or node budget. Missing time/nodes retain Search's existing unbounded representation.</summary>
    internal Search.Limits ToSearchLimits(UciLimits l)
    {
        int depth = l.Depth is > 0 and var requestedDepth ? Math.Min(64, requestedDepth) : 64;
        long nodes = l.Nodes is > 0 and var requestedNodes ? requestedNodes : long.MaxValue;
        int milliseconds = l.MoveTimeMs is { } movetime ? (int)Math.Clamp(movetime, 1, int.MaxValue) : int.MaxValue;
        bool white = _state.Board.WhiteToMove;
        if ((white ? l.WTimeMs : l.BTimeMs) is { } remaining)
        {
            long myTime = Math.Min(remaining, int.MaxValue);
            long myInc = Math.Min((white ? l.WIncMs : l.BIncMs) ?? 0, int.MaxValue);
            long moves = l.MovesToGo is > 0 and var movesToGo ? movesToGo : 30;
            // Preserve the clock allocation policy, now using the caller's moves-to-go and
            // wide arithmetic. A zero/exhausted clock cannot become an absent time limit.
            int budget = (int)Math.Clamp(Math.Min(myTime - 30, myTime / moves + myInc * 4 / 5), 1, int.MaxValue);
            milliseconds = Math.Min(milliseconds, budget);
        }
        return new Search.Limits(MaxDepth: depth, MaxNodes: nodes, MaxTimeMs: milliseconds);
    }

    private static string? NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "substrate" or "fold" or "edge" => "substrate",
        "off" or "false" or "none" => "off",
        "true" or "on" => "substrate",
        _ => null,
    };

    // (Re)build the search to match the requested mode. allowInit gates slow provider setup.
    // False means the requested engine is not ready; it never authorizes a different player.
    private bool EnsureSearch(Action<string> info, bool allowInit)
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
                info("substrate mode requires a successful isready initialization");
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
                info($"substrate provider initialization failed ({FirstLine(ex.Message)})");
                return false;
            }
        }

        if (_providers is null)
        {
            info("substrate initialization did not produce a provider stack");
            return false;
        }

        var configured = _providers.Configure(substrate: true);
        _search = configured.BuildSearch(ttBits: 20);
        _builtMode = _substrateMode;
        _searchStale = false;
        var prepared = configured.Receipt();
        info($"substrate provider stack prepared (learned-pst " +
             $"{(prepared.LearnedPstContributes ? $"active:{prepared.LearnedPstNonZeroCells}" : "selected:no-delta")}, " +
             $"syzygy {prepared.SyzygyLargestMen}-men); per-search usage is receipted after go");
        return true;
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

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return nl >= 0 ? s[..nl] : s;
    }

    public void Dispose() => _ds?.Dispose();
}
