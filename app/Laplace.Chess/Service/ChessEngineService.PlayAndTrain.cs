using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed partial class ChessEngineService
{
    public async Task RunStrongSelfPlayAsync(
        int games, int depth, int maxPlies, int openingPlies, int reportEvery,
        Action<ChessTrainStatus>? onReport, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var writeHost = await WriteHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {
            if ((g - 1) % Math.Max(1, reportEvery) == 0)
                _boardEvaluator = new SubstrateBoardEvaluator(_ds!);
            var search = new Search(
                EvalTerm.All, _rootBias ??= new SubstrateRootBias(_ds!), ttBits: 18,
                positionEvaluator: _boardEvaluator,
                tablebase: ChessTablebaseRuntime.ProbeSearch);

            var state = _modality!.Initial();
            var subjectKeys = new List<string>(); var objectKeys = new List<string>(); var movers = new List<int>();
            int plies = 0; bool adjudicated = false;
            var terminal = _modality.Terminal(state);
            while (terminal is null)
            {
                if (plies >= maxPlies) { adjudicated = true; break; }
                var legal = _modality.LegalActions(state);
                ChessMove mv = plies < openingPlies
                    ? legal[rng.Next(legal.Count)]
                    : search.Think(state, new Search.Limits(MaxDepth: depth)).BestMove!.Value;
                int mover = _modality.SideToMove(state);
                var next = _modality.Apply(state, mv);
                subjectKeys.Add(_modality.StateKey(state));
                objectKeys.Add(_modality.StateKey(next));
                movers.Add(mover);
                state = next; plies++;
                terminal = _modality.Terminal(state);
            }

            var outcome = terminal ?? GameOutcome.Draw;
            var edges = new RecordedEdge[subjectKeys.Count];
            for (int i = 0; i < edges.Length; i++)
                edges[i] = new RecordedEdge(subjectKeys[i], objectKeys[i], null, outcome.ForMover(movers[i]));
            await writeHost.LearnGameAsync(edges, adjudicated, ct);

            RecordGame(new PlayedGame<ChessMove>(edges, outcome, plies, adjudicated));
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games)) onReport(Status());
        }
    }

    public async Task<ChessPlayStart> StartPlaySessionAsync(
        bool recordToSubstrate = true, IReadOnlyList<string>? moves = null,
        string tenantId = "public", string? userId = null, CancellationToken ct = default,
        OperationPrincipal? requester = null)
    {
        await EngineAsync(ct);
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        // An authenticated account does not establish a canonical chess player.
        // A participant relation requires a recorded player record, not a name hash.
        const string humanName = "Browser player";
        var id = liveHost.StartPlaySession(
            recordToSubstrate,
            tenantId: tenantId,
            userId: userId,
            whitePlayerId: null,
            whitePlayerName: humanName,
            blackPlayerId: ChessVocabulary.LaplacePlayerId,
            blackPlayerName: "Laplace", requester: requester);
        var session = liveHost.GetPlaySession(id)
            ?? throw new InvalidOperationException("play session missing after start");

        var state = _modality!.Initial();
        if (moves is { Count: > 0 })
        {
            foreach (var uci in moves)
            {
                if (FindLegalUci(state, uci) is not { } mv) break;
                string fromKey = _modality.StateKey(state);
                Hash128? moverId = state.Board.WhiteToMove ? session.WhitePlayerId : session.BlackPlayerId;
                var next = _modality.Apply(state, mv);
                string toKey = _modality.StateKey(next);
                session.Moves.Add(uci);
                session.PlyCount = session.Moves.Count;
                if (session.RecordToSubstrate)
                    await liveHost.RecordPlayPlyAsync(
                        id, session.PlyCount, fromKey, toKey, uci, moverId, ct);
                state = next;
            }
        }

        session.State = state;
        session.PlyCount = session.Moves.Count;
        var status = _modality.Terminal(state) is { } t ? Describe(t) : "ongoing";
        session.SaveOperation();
        return new ChessPlayStart(id, state.Board.ToFen(), status, session.PlyCount,
            session.TenantId, session.UserId,
            Convert.ToHexString(session.EventId.ToBytes()).ToLowerInvariant(), session.Requester);
    }

    private static ChessMove? FindLegalUci(ChessState state, string uci)
    {
        foreach (var m in MoveGen.Legal(state.Board))
            if (m.ToUci() == uci) return m;
        return null;
    }

    public async Task<ChessPlayMoveResult> PlayMoveAsync(
        Guid sessionId, string fen, string uci, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        if (liveHost.GetPlaySession(sessionId) is not { } session)
            return new ChessPlayMoveResult(fen, false, "session expired", Legal: false, Ply: 0);

        var state = session.State ?? _modality!.FromFen(fen);
        if (FindLegalUci(state, uci) is not { } mv)
            return new ChessPlayMoveResult(state.Board.ToFen(), false, "illegal move", Legal: false,
                Ply: session.PlyCount);

        string fromKey = _modality!.StateKey(state);
        Hash128? moverId = state.Board.WhiteToMove ? session.WhitePlayerId : session.BlackPlayerId;
        var next = _modality.Apply(state, mv);
        string toKey = _modality.StateKey(next);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();

        var analysis = new ChessLivePlyAnalysis(Motifs: motifs);
        session.CommitMove(next, uci, analysis);

        if (session.RecordToSubstrate)
        {
            await liveHost.RecordPlayPlyAsync(
                sessionId, session.PlyCount, fromKey, toKey, uci, moverId, ct);
            await liveHost.RecordPlayPlyAnalysisAsync(
                sessionId, session.PlyCount,
                analysis, ct);
        }

        if (status != "ongoing")
            await liveHost.FinishPlaySessionAsync(sessionId, ParseTerminalStatus(status), adjudicated: false, ct);

        return new ChessPlayMoveResult(next.Board.ToFen(), status != "ongoing", status, Legal: true,
            Ply: session.PlyCount, Motifs: motifs);
    }

    public async Task<ChessBestMove> PlayBestMoveAsync(
        Guid sessionId, string fen, int depth = 4, bool substrate = true, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        if (liveHost.GetPlaySession(sessionId) is not { } session)
            return new ChessBestMove(null, fen, 0, false, false, "session expired");

        var state = session.State ?? _modality!.FromFen(fen);
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        string startFen = session.Metadata?.StartFen ?? ChessModality.StartFen;
        bool reads = substrate && !HasImmediateMate(_modality, state);
        var search = BuildEngine(
            reads, continuations: ContinuationsFor(startFen, session.Moves, reads, movesIgnoreFen: false));
        var result = search.Think(state, new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 12)));
        ChessMove mv = result.BestMove!.Value;

        string fromKey = _modality.StateKey(state);
        var next = _modality.Apply(state, mv);
        string toKey = _modality.StateKey(next);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var pv = search.ExtractPv(state);
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        int whiteCp = state.Board.WhiteToMove ? result.Score : -result.Score;

        var analysis = new ChessLivePlyAnalysis(result.Score, result.Depth, result.Nodes, pv, motifs);
        session.CommitMove(next, mv.ToUci(), analysis);

        if (session.RecordToSubstrate)
        {
            await liveHost.RecordPlayPlyAsync(
                sessionId, session.PlyCount, fromKey, toKey, mv.ToUci(), ChessVocabulary.LaplacePlayerId, ct);
            await liveHost.RecordPlayPlyAnalysisAsync(
                sessionId, session.PlyCount,
                analysis, ct);
        }
        ReturnEngine(search);

        if (status != "ongoing")
            await liveHost.FinishPlaySessionAsync(sessionId, ParseTerminalStatus(status), adjudicated: false, ct);

        return new ChessBestMove(mv.ToUci(), next.Board.ToFen(), result.Score, reads,
            status != "ongoing", status, ScoreCp: whiteCp, Depth: result.Depth, Nodes: result.Nodes,
            Pv: pv, Motifs: motifs);
    }

    public async Task FinishPlaySessionAsync(
        Guid sessionId, string status, bool adjudicated = false, CancellationToken ct = default)
    {
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        await liveHost.FinishPlaySessionAsync(sessionId, ParseTerminalStatus(status), adjudicated, ct);
    }

    private static GameOutcome ParseTerminalStatus(string status) => status switch
    {
        "draw" => GameOutcome.Draw,
        "white wins" => GameOutcome.WonBy(0),
        "black wins" => GameOutcome.WonBy(1),
        _ => GameOutcome.Draw,
    };

    public async Task<ChessApplyResult> ApplyMoveAsync(
        string fen, string uci, IReadOnlyList<string>? moves = null, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = StateFromHistory(fen, moves);
        if (FindLegalUci(state, uci) is not { } mv)
            return new ChessApplyResult(fen, false, "illegal move", Legal: false);
        var next = _modality!.Apply(state, mv);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        return new ChessApplyResult(next.Board.ToFen(), status != "ongoing", status, Legal: true, Motifs: motifs);
    }

    private IReadOnlyList<LearnedSquare>? _learnedCells;

    public async Task<IReadOnlyList<LearnedSquare>> LearnedPstAsync(CancellationToken ct = default)
    {
        await EngineAsync(ct);
        if (_learnedCells is { } cells) return cells;
        ScheduleLearnedRefresh();
        if (_learnedRefresh is { } t) await t.WaitAsync(ct);
        return _learnedCells ?? [];
    }

    public bool StartTraining(double temperature, double weight, int maxPlies, int maxGames = 0)
    {
        lock (_statLock)
        {
            if (_trainTask is { IsCompleted: false }) return false;
            _trainTemp = temperature; _trainWeight = weight; _trainMaxPlies = maxPlies;
            _trainCts = new CancellationTokenSource();
            _trainTask = Task.Run(() => TrainLoopAsync(maxGames, _trainCts.Token));
            return true;
        }
    }

    public bool StopTraining()
    {
        CancellationTokenSource? cts;
        lock (_statLock) { cts = _trainCts; }
        if (cts is null) return false;
        cts.Cancel();
        return true;
    }

    public ChessTrainStatus Status()
    {
        lock (_statLock)
            return new ChessTrainStatus(
                _trainTask is { IsCompleted: false }, _games, _white, _black, _draws, _adjudicated,
                _lastOutcome, _trainTemp, _trainWeight, _trainMaxPlies);
    }

    public async Task RunSelfPlayAsync(
        int games, double temperature, int maxPlies, int reportEvery,
        Action<ChessTrainStatus>? onReport, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var writeHost = await WriteHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        lock (_statLock) { _trainTemp = temperature; _trainMaxPlies = maxPlies; }
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {
            var played = await engine.PlayGameAsync(_modality!.Initial(), temperature, rng, maxPlies, ct);
            await writeHost.LearnGameAsync(played.Edges, played.Adjudicated, ct);
            RecordGame(played);
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games))
                onReport(Status());
        }
    }

    private async Task TrainLoopAsync(int maxGames, CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        var writeHost = await WriteHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        int played = 0;
        while (!ct.IsCancellationRequested && (maxGames <= 0 || played < maxGames))
        {
            try
            {
                var g = await engine.PlayGameAsync(_modality!.Initial(), _trainTemp, rng, _trainMaxPlies, ct);
                await writeHost.LearnGameAsync(g.Edges, g.Adjudicated, ct);
                RecordGame(g);
                played++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "chess train game failed"); }
        }
    }

    private void RecordGame(PlayedGame<ChessMove> played)
    {
        lock (_statLock)
        {
            _games++;
            if (played.Adjudicated) _adjudicated++;
            if (played.Outcome.IsDraw) { _draws++; _lastOutcome = "draw"; }
            else if (played.Outcome.Winner == 0) { _white++; _lastOutcome = "white"; }
            else { _black++; _lastOutcome = "black"; }
        }
    }

    private Random Rng() => new();

    private static string Describe(GameOutcome o) =>
        o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    public async ValueTask DisposeAsync()
    {
        StopTraining();
        if (_trainTask is not null)
        {
            try { await _trainTask; }
            catch (OperationCanceledException) when (_trainCts?.IsCancellationRequested == true) { }
            catch (Exception error) { _log.LogError(error, "Chess training worker failed during shutdown."); }
        }
        _writeInitGate.Dispose();
        _initGate.Dispose();
    }
}
