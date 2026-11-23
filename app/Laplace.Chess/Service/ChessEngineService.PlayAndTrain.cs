using global::Npgsql;
using Laplace.Data;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
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
        var writeHost = await LiveHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {
            var search = new Search(
                EvalTerm.All, _rootBias ??= new SubstrateRootBias(_ds!), ttBits: 18,
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

            var outcome = terminal ?? GameOutcome.Unknown;
            var edges = new RecordedEdge[subjectKeys.Count];
            for (int i = 0; i < edges.Length; i++)
                edges[i] = new RecordedEdge(subjectKeys[i], objectKeys[i], null, outcome.ForMover(movers[i]));
            await writeHost.LearnGameAsync(edges, "Laplace self-play", adjudicated, ct);

            RecordGame(new PlayedGame<ChessMove>(edges, outcome, plies, adjudicated));
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games)) onReport(Status());
        }
    }

    public Task<ChessPlayStart> StartPlaySessionAsync(
        bool recordToSubstrate = true, IReadOnlyList<string>? moves = null,
        string tenantId = "public", string? userId = null, CancellationToken ct = default,
        OperationPrincipal? requester = null)
        => StartPlaySessionAsync(recordToSubstrate, moves, tenantId, userId, variant: null, startFen: null, requester, ct);

    public async Task<ChessPlayStart> StartPlaySessionAsync(
        bool recordToSubstrate, IReadOnlyList<string>? moves,
        string tenantId, string? userId, string? variant, string? startFen,
        OperationPrincipal? requester = null, CancellationToken ct = default)
    {
        await EngineAsync(ct);

        var rules = ChessVariants.Resolve(variant);
        string resolvedFen = !string.IsNullOrWhiteSpace(startFen)
            ? startFen.Trim()
            : rules.IsStandard
                ? ChessModality.StartFen
                : NativeChessRules.LawStart(rules.Law) ?? ChessModality.StartFen;
        var initialState = ParseValidatedState(resolvedFen, rules, nameof(startFen));
        string canonicalStartFen = string.Equals(resolvedFen, "startpos", StringComparison.OrdinalIgnoreCase)
            ? initialState.Board.ToFen()
            : resolvedFen;

        var state = initialState;
        List<(string FromKey, string ToKey, string Uci)>? validatedMoves = null;
        if (moves is { Count: > 0 })
        {
            validatedMoves = new List<(string, string, string)>(moves.Count);
            for (int i = 0; i < moves.Count; i++)
            {
                string rawUci = moves[i] ?? "";
                if (_modality!.Terminal(state) is not null)
                    throw new ArgumentException($"Illegal UCI move '{rawUci}' at index {i}: position is already terminal.", nameof(moves));
                if (FindLegalUci(state, rawUci) is not { } mv)
                    throw new ArgumentException($"Illegal UCI move '{rawUci}' at index {i}.", nameof(moves));
                string canonicalUci = mv.ToUci();
                string fromKey = _modality.StateKey(state);
                var next = _modality.Apply(state, mv);
                string toKey = _modality.StateKey(next);
                validatedMoves.Add((fromKey, toKey, canonicalUci));
                state = next;
            }
        }

        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        // An authenticated account does not establish a canonical chess player.
        // A participant relation requires a recorded player record, not a name hash.
        const string humanName = "Browser player";
        var id = liveHost.StartPlaySession(
            recordToSubstrate,
            tenantId: tenantId,
            userId: userId,
            whitePlayerName: humanName,
            blackPlayerName: "Laplace", requester: requester,
            variant: variant, startFen: canonicalStartFen);
        var session = liveHost.GetPlaySession(id)
            ?? throw new InvalidOperationException("play session missing after start");

        if (validatedMoves is { Count: > 0 })
        {
            foreach (var (fromKey, toKey, canonicalUci) in validatedMoves)
            {
                session.Moves.Add(canonicalUci);
                session.PlyCount = session.Moves.Count;
                if (session.RecordToSubstrate)
                    await liveHost.RecordPlayPlyAsync(id, session.PlyCount, fromKey, toKey, canonicalUci, ct);
            }
        }

        session.State = state;
        session.PlyCount = session.Moves.Count;
        var status = _modality!.Terminal(state) is { } t ? Describe(t) : "ongoing";
        session.SaveOperation();
        return new ChessPlayStart(id, state.Board.ToFen(), status, session.PlyCount,
            session.TenantId, session.UserId,
            Convert.ToHexString(session.EventId.ToBytes()).ToLowerInvariant(), session.Requester);
    }

    private static ChessMove? FindLegalUci(ChessState state, string uci)
        => ResolveNativeMove(state.Board, uci);

    public async Task<ChessPlayMoveResult> PlayMoveAsync(
        Guid sessionId, string fen, string uci, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        if (liveHost.GetPlaySession(sessionId) is not { } session)
            return new ChessPlayMoveResult(fen, false, "session expired", Legal: false, Ply: 0);

        var state = session.State ?? ParseValidatedState(fen, ChessVariants.Resolve(session.Metadata?.Variant), nameof(fen));
        if (FindLegalUci(state, uci) is not { } mv)
            return new ChessPlayMoveResult(state.Board.ToFen(), false, "illegal move", Legal: false,
                Ply: session.PlyCount);

        string fromKey = _modality!.StateKey(state);
        var next = _modality.Apply(state, mv);
        string toKey = _modality.StateKey(next);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();

        var analysis = new ChessLivePlyAnalysis(Motifs: motifs);
        session.CommitMove(next, uci, analysis);

        if (session.RecordToSubstrate)
        {
            await liveHost.RecordPlayPlyAsync(sessionId, session.PlyCount, fromKey, toKey, uci, ct);
            await liveHost.RecordPlayPlyAnalysisAsync(
                sessionId, session.PlyCount,
                analysis, ct);
        }

        if (status != "ongoing")
            liveHost.FinishPlaySessionAfterReply(sessionId, ParseTerminalStatus(status), adjudicated: false);

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

        var state = session.State ?? ParseValidatedState(fen, ChessVariants.Resolve(session.Metadata?.Variant), nameof(fen));
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        string startFen = session.Metadata?.StartFen ?? ChessModality.StartFen;
        int boundedDepth = Math.Clamp(depth, 1, 12);
        bool reads = substrate && !HasImmediateMate(_modality, state);
        if (reads && TryNativeChessPlay(state.Board, state.Board.Rules, boundedDepth) is { StatusCode: 0 } native
            && ResolveNativeMove(state.Board, native.BestUci) is { } nativeMove)
        {
            string nFrom = _modality.StateKey(state);
            var nNext = _modality.Apply(state, nativeMove);
            string nTo = _modality.StateKey(nNext);
            var nStatus = _modality.Terminal(nNext) is { } tn ? Describe(tn) : "ongoing";
            var nPv = new[] { nativeMove.ToUci() };
            var nMotifs = ChessMotifs.DetectAtPly(state.Board, nativeMove, nNext.Board).ToList();
            int nWhiteCp = state.Board.WhiteToMove ? native.ScoreCp : -native.ScoreCp;

            var nAnalysis = new ChessLivePlyAnalysis(native.ScoreCp, native.DepthReached, (long)native.NodesSearched, nPv, nMotifs);
            session.CommitMove(nNext, nativeMove.ToUci(), nAnalysis);

            if (session.RecordToSubstrate)
            {
                await liveHost.RecordPlayPlyAsync(sessionId, session.PlyCount, nFrom, nTo, nativeMove.ToUci(), ct);
                await liveHost.RecordPlayPlyAnalysisAsync(sessionId, session.PlyCount, nAnalysis, ct);
            }
            if (nStatus != "ongoing")
                liveHost.FinishPlaySessionAfterReply(sessionId, ParseTerminalStatus(nStatus), adjudicated: false);

            return new ChessBestMove(nativeMove.ToUci(), nNext.Board.ToFen(), native.ScoreCp, Rated: true,
                nStatus != "ongoing", nStatus, ScoreCp: nWhiteCp, Depth: native.DepthReached,
                Nodes: (long)native.NodesSearched, Pv: nPv, Motifs: nMotifs,
                SyzygyWdl: native.SyzygyWdl, FromSyzygy: native.FromSyzygy,
                WallUs: (long)native.WallUs, Source: native.Source, BestSan: native.BestSan);
        }

        var search = BuildEngine(
            reads, continuations: ContinuationsFor(startFen, session.Moves, reads, movesIgnoreFen: false));
        var result = search.Think(state, new Search.Limits(MaxDepth: boundedDepth));
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
            await liveHost.RecordPlayPlyAsync(sessionId, session.PlyCount, fromKey, toKey, mv.ToUci(), ct);
            await liveHost.RecordPlayPlyAnalysisAsync(
                sessionId, session.PlyCount,
                analysis, ct);
        }
        ReturnEngine(search);

        if (status != "ongoing")
            liveHost.FinishPlaySessionAfterReply(sessionId, ParseTerminalStatus(status), adjudicated: false);

        return new ChessBestMove(mv.ToUci(), next.Board.ToFen(), result.Score, reads,
            status != "ongoing", status, ScoreCp: whiteCp, Depth: result.Depth, Nodes: result.Nodes,
            Pv: pv, Motifs: motifs);
    }

    public Task FinishPlaySessionAsync(
        Guid sessionId, string status, bool adjudicated = false, CancellationToken ct = default)
        => FinishPlaySessionAsync(sessionId, status, (bool?)adjudicated, ct);

    public async Task FinishPlaySessionAsync(
        Guid sessionId, string? status, bool? adjudicated, CancellationToken ct = default)
    {
        var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
        var modality = _modality ??= new ChessModality();
        var session = liveHost.GetPlaySession(sessionId, resumeRecording: true);
        var (outcome, isAdjudicated) = ResolveFinishOutcome(status, session?.State, modality, adjudicated);
        await liveHost.FinishPlaySessionAsync(sessionId, outcome, isAdjudicated, ct);
    }

    private static GameOutcome ParseTerminalStatus(string status)
        => ResolveFinishOutcome(status, state: null, modality: new ChessModality(), adjudicated: null).Outcome;

    internal static (GameOutcome Outcome, bool Adjudicated) ResolveFinishOutcome(
        string? status, ChessState? state, ChessModality modality, bool? adjudicated)
    {
        var boardTerminal = state is null ? null : modality.Terminal(state);
        if (string.IsNullOrWhiteSpace(status))
        {
            if (boardTerminal is { } t)
                return (t, adjudicated ?? false);
            return (GameOutcome.Unknown, adjudicated ?? true);
        }

        string normalized = status.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "auto" or "rules" or "terminal":
                return boardTerminal is { } bt
                    ? (bt, adjudicated ?? false)
                    : (GameOutcome.Unknown, adjudicated ?? true);
            case "ongoing" or "incomplete" or "*" or "aborted":
                return (GameOutcome.Unknown, adjudicated ?? true);
            case "checkmate":
                if (boardTerminal is { IsDraw: false, IsUnknown: false } mate)
                    return (mate, adjudicated ?? false);
                throw new ArgumentException("Position is not checkmate.", nameof(status));
            case "stalemate":
                if (state is null)
                    return (GameOutcome.Draw, adjudicated ?? false);
                if (boardTerminal is { IsDraw: true } && !MoveGen.InCheck(state.Board, state.Board.WhiteToMove))
                    return (GameOutcome.Draw, adjudicated ?? false);
                throw new ArgumentException("Position is not stalemate.", nameof(status));
            case "draw" or "draw_agreement" or "draw agreement" or "1/2-1/2"
                or "fifty_move" or "fifty-move" or "threefold" or "insufficient_material":
                return (GameOutcome.Draw, adjudicated ?? false);
            case "white wins" or "white_wins" or "1-0":
                return (GameOutcome.WonBy(0), adjudicated ?? false);
            case "black wins" or "black_wins" or "0-1":
                return (GameOutcome.WonBy(1), adjudicated ?? false);
            case "resign_white" or "white_resigns" or "white_resign" or "timeout_white" or "white_timeout":
                return (GameOutcome.WonBy(1), adjudicated ?? false);
            case "resign_black" or "black_resigns" or "black_resign" or "timeout_black" or "black_timeout":
                return (GameOutcome.WonBy(0), adjudicated ?? false);
            default:
                throw new ArgumentException($"Unknown chess terminal status '{status}'.", nameof(status));
        }
    }

    public Task<ChessApplyResult> ApplyMoveAsync(
        string fen, string uci, IReadOnlyList<string>? moves = null, CancellationToken ct = default)
        => ApplyMoveAsync(fen, uci, moves, variant: null, ct);

    public async Task<ChessApplyResult> ApplyMoveAsync(
        string fen, string uci, IReadOnlyList<string>? moves, string? variant, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = StateFromHistory(fen, moves, ChessVariants.Resolve(variant));
        if (FindLegalUci(state, uci) is not { } mv)
            return new ChessApplyResult(fen, false, "illegal move", Legal: false);
        var next = _modality!.Apply(state, mv);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        return new ChessApplyResult(next.Board.ToFen(), status != "ongoing", status, Legal: true, Motifs: motifs);
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
        var writeHost = await LiveHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        lock (_statLock) { _trainTemp = temperature; _trainMaxPlies = maxPlies; }
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {
            var played = await engine.PlayGameAsync(_modality!.Initial(), temperature, rng, maxPlies, ct);
            await writeHost.LearnGameAsync(played.Edges, "Laplace self-play", played.Adjudicated, ct);
            RecordGame(played);
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games))
                onReport(Status());
        }
    }

    private async Task TrainLoopAsync(int maxGames, CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        var writeHost = await LiveHostAsync(ct).ConfigureAwait(false);
        var rng = new Random();
        int played = 0;
        while (!ct.IsCancellationRequested && (maxGames <= 0 || played < maxGames))
        {
            try
            {
                var g = await engine.PlayGameAsync(_modality!.Initial(), _trainTemp, rng, _trainMaxPlies, ct);
                await writeHost.LearnGameAsync(g.Edges, "Laplace self-play", g.Adjudicated, ct);
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
            if (played.Outcome.IsUnknown) { _lastOutcome = "*"; }
            else if (played.Outcome.IsDraw) { _draws++; _lastOutcome = "draw"; }
            else if (played.Outcome.Winner == 0) { _white++; _lastOutcome = "white"; }
            else { _black++; _lastOutcome = "black"; }
        }
    }

    private Random Rng() => new();

    private static string Describe(GameOutcome o) =>
        o.IsUnknown ? "incomplete" : o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    public async ValueTask DisposeAsync()
    {
        StopTraining();
        if (_trainTask is not null)
        {
            try { await _trainTask; }
            catch (OperationCanceledException) when (_trainCts?.IsCancellationRequested == true) { }
            catch (Exception error) { _log.LogError(error, "Chess training worker failed during shutdown."); }
        }
        _ownedRuntime?.Dispose();
        _initGate.Dispose();
    }
}
