using System.Collections.Concurrent;
using System.Globalization;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

public sealed record ChessLiveGameMetadata(
    string? Event = null,
    string? Site = null,
    string? Date = null,
    string? TimeControl = null,
    string? TimeControlClass = null,
    string? Termination = null,
    string? StartFen = null,
    string? ExternalGameId = null,
    int? WhiteRating = null,
    int? BlackRating = null);

public sealed record ChessLivePlyAnalysis(
    int? ScoreCpSideToMove = null,
    int Depth = 0,
    long Nodes = 0,
    IReadOnlyList<string>? Pv = null,
    IReadOnlyList<string>? Motifs = null);

public sealed class ChessLiveGameHost : IAsyncDisposable, ITurnLearner
{
    private const double WitnessWeight = 0.7;
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly SubstrateTurnHost _turnHost;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Hash128, LiveGameSession> _games = new();
    private readonly ConcurrentDictionary<Guid, PlaySession> _playSessions = new();

    public long GamesCompleted { get; private set; }

    public void InvalidateLearnedPst() => _boardEvaluator = null;

    private ChessLiveGameHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, SubstrateTurnHost turnHost)
    {
        _ds = ds;
        _writer = writer;
        _turnHost = turnHost;
    }

    internal ConsensusAccumulatingWriter Writer => _writer;
    internal SubstrateTurnHost TurnHost => _turnHost;

    public static async Task<ChessLiveGameHost> CreateAsync(
        double witnessWeight = 0.5d, string defaultLearnContext = "chess/live/game",
        CancellationToken ct = default, string? connString = null)
    {
        CodepointPerfcache.LoadDefault();
        var conn = connString ?? ChessEngineService.ResolveConnString();
        var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, conn);
        var inner = new NpgsqlSubstrateWriter(ds, durability: PostgresWriteDurability.Synchronous);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, persistEvidence: true);
        var reader = new NpgsqlSubstrateReader(ds);
        var host = new SubstrateTurnHost(ds, writer, reader, witnessWeight, defaultLearnContext);
        var canonicalNames = await ChessVocabulary.BootstrapManyAsync(writer,
        [
            new(ChessVocabulary.SourceId, ChessVocabulary.SourceName,
                ChessVocabulary.SelfPlayTrustClass),
            new(ChessTransitions.SourceId, "ChessTransitions", ChessTransitions.TrustClassId),
            new(ChessPositionOutcomes.SourceId, ChessPositionOutcomes.SourceName,
                ChessPositionOutcomes.TrustClassId),
            new(ChessSyzygy.SourceId, ChessSyzygy.SourceName, ChessSyzygy.TrustClassId),
        ], ct, reader);
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, canonicalNames, ct);
        return new ChessLiveGameHost(ds, writer, host);
    }

    public static Hash128 LichessGameId(string lichessGameId)
        => Hash128.OfCanonical($"chess/lichess/{lichessGameId}");

    public Task OpenGameAsync(
        Hash128 eventId, string learnContext, Hash128? whitePlayerId = null, Hash128? blackPlayerId = null,
        string? whitePlayerName = null, string? blackPlayerName = null,
        ChessLiveGameMetadata? metadata = null,
        CancellationToken ct = default)
    {
        metadata ??= new ChessLiveGameMetadata(
            Date: DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        _games[eventId] = new LiveGameSession(
            learnContext, whitePlayerId, blackPlayerId, whitePlayerName, blackPlayerName, metadata);
        return Task.CompletedTask;
    }

    public void SetGamePlayers(
        Hash128 eventId,
        Hash128? whitePlayerId,
        string? whitePlayerName,
        Hash128? blackPlayerId,
        string? blackPlayerName)
    {
        if (_games.TryGetValue(eventId, out var session))
            session.SetPlayers(whitePlayerId, whitePlayerName, blackPlayerId, blackPlayerName);
    }

    public void SetGameMetadata(Hash128 eventId, ChessLiveGameMetadata metadata)
    {
        if (_games.TryGetValue(eventId, out var session))
            session.SetMetadata(metadata);
    }

    public async Task RecordPlyClockAsync(
        Hash128 eventId, int ply, int remainingMs, CancellationToken ct = default)
    {
        if (remainingMs < 0 || !_games.TryGetValue(eventId, out var session)) return;
        await _writeGate.WaitAsync(ct);
        try { session.ClockRemainingMs[ply] = remainingMs; }
        finally { _writeGate.Release(); }
    }

    public async Task RecordPlyAnalysisAsync(
        Hash128 eventId, int ply, ChessLivePlyAnalysis analysis, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session)) return;
        await _writeGate.WaitAsync(ct);
        try { session.Analysis[ply] = analysis; }
        finally { _writeGate.Release(); }
    }

    public async Task RecordPlyAsync(
        Hash128 eventId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            throw new InvalidOperationException($"game {eventId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            var (board, moving, move) = ResolveMove(fromKey, toKey, moveToken);
            string san = San.ToSan(board, move);
            session.Moves.Add(moveToken == "?" ? move.ToUci() : moveToken);
            session.Plies.Add(new RecordedPly(
                fromKey, toKey, move.ToUci(), san, session.MoverSide(ply), moving, move, moverPlayerId));
            session.MoveIds.Add(ChessCompose.MoveId(moving, move));
            if (session.PositionIds.Count == 0)
                session.PositionIds.Add(ChessCompose.PositionId(fromKey));
            session.PositionIds.Add(ChessCompose.PositionId(toKey));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CompleteGameAsync(
        Hash128 eventId, GameOutcome result, bool adjudicated, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            return;

        await _writeGate.WaitAsync(ct);
        try
        {
            var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, session.LearnContext)
                .DeclareSourcePrior(SourceTrust.Response)
                .DeclareSourcePrior(ChessTransitions.SourceId, SourceTrust.StructuredCorpus)
                .DeclareSourcePrior(ChessPositionOutcomes.SourceId, SourceTrust.StructuredCorpus);

            Hash128 playingId = default;
            if (session.PositionIds.Count > 0)
            {
                var lineId = ChessCompose.LineId(
                    session.PositionIds[0],
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(session.MoveIds));
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, ChessVocabulary.SourceId);

                playingId = ChessVocabulary.LivePlayingId(
                    session.WhitePlayerId, session.BlackPlayerId, session.LearnContext,
                    lineId, result.ResultToken, session.Metadata.ExternalGameId);
                EnsurePlayingEntity(b, playingId, session);

                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    playingId, ChessVocabulary.PlaysLineType, lineId,
                    ChessVocabulary.SourceId, null, WitnessWeight));

                long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
                var movePoints = new ChessNode[session.Plies.Count];
                for (int i = 0; i < movePoints.Length; i++)
                    movePoints[i] = ChessGraph.EmitMove(
                        b, session.Plies[i].MovingPiece, session.Plies[i].Move,
                        ChessVocabulary.SourceId, nowUs);
                var startPoint = ChessGraph.ComposePositionPoint(session.Plies[0].FromKey);
                if (startPoint.Id != session.PositionIds[0])
                    throw new InvalidOperationException("live chess start-position identity diverged from line preimage");
                ChessGraph.AppendLineTrajectory(
                    b, lineId, startPoint, movePoints, ChessVocabulary.SourceId, nowUs);
                ChessMoveOutcomes.AppendGame(
                    b, lineId, Array.ConvertAll(movePoints, static n => n.Id),
                    result, ChessVocabulary.SourceId, WitnessWeight);
                WitnessResult(b, lineId, playingId, result);
                if (session.WhitePlayerId is { } emitWhite && session.WhitePlayerName is { Length: > 0 } whiteName)
                    ChessVocabulary.EmitPlayer(
                        b, emitWhite, whiteName, ChessVocabulary.SourceId, SourceTrust.Response);
                if (session.BlackPlayerId is { } emitBlack && session.BlackPlayerName is { Length: > 0 } blackName)
                    ChessVocabulary.EmitPlayer(
                        b, emitBlack, blackName, ChessVocabulary.SourceId, SourceTrust.Response);
                if (session.WhitePlayerId is { } wp)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lineId, "HAS_WHITE", wp, ChessVocabulary.SourceId, playingId, WitnessWeight));
                if (session.BlackPlayerId is { } bp)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lineId, "HAS_BLACK", bp, ChessVocabulary.SourceId, playingId, WitnessWeight));

                if (session.WhitePlayerId is { } w2)
                    ChessGraph.AppendPlayerResult(
                        b, w2, session.BlackPlayerId, result.ForMover(0), WitnessWeight,
                        ChessVocabulary.SourceId, playingId);
                if (session.BlackPlayerId is { } b2)
                    ChessGraph.AppendPlayerResult(
                        b, b2, session.WhitePlayerId, result.ForMover(1), WitnessWeight,
                        ChessVocabulary.SourceId, playingId);

                RecordGameMetadata(b, lineId, playingId, session, result, adjudicated);
                RecordLivePlyDetails(b, playingId, movePoints, session, nowUs);

                string?[]? clocks = CompleteClockTokens(session);
                var witnessed = new ChessWitnessedGame(
                    lineId, playingId,
                    session.Plies.Select(static p => p.San).ToArray(),
                    result,
                    session.WhitePlayerId, session.BlackPlayerId,
                    NonStandardStartFen(session.Metadata.StartFen),
                    clocks,
                    EvalTokens: null,
                    QualityTokens: null)
                {
                    MoveIds = session.MoveIds.ToArray(),
                };
                ChessAnalyze.DeriveFromWitnessed(b, witnessed);
                ChessGraph.AppendTransitions(
                    b, session.PositionIds, result, SourceTrust.StructuredCorpus,
                    ChessTransitions.SourceId, playingId);
                b.AddEntity(
                    ChessTransitions.MarkerId(playingId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, ChessTransitions.SourceId);
                ChessPositionOutcomes.DepositTrajectory(
                    b, session.Plies.Select(static ply => ply.FromKey)
                        .Append(session.Plies[^1].ToKey).ToArray(),
                    result, playingId);
                if (ChessTablebaseRuntime.Prober is { } prober)
                    ChessSyzygy.DeriveGame(b, witnessed, prober);

                for (int i = 0; i < session.Plies.Count; i++)
                {
                    int ply = i + 1;
                    if (session.Analysis.TryGetValue(ply, out var a)
                        && a.ScoreCpSideToMove is { } cp)
                        ChessGraph.AppendEval(
                            b, session.Plies[i].FromKey, cp, 1, WitnessWeight,
                            ChessVocabulary.AnalysisSourceId, playingId);
                }
            }

            var change = await b.BuildAsync(ct);
            await _writer.ApplyAsync(change, ct);
            if (session.PositionIds.Count > 1)
                ChessTransitionObservations.MarkObserved(
                    session.PositionIds.Take(session.PositionIds.Count - 1), session.MoveIds);
            InvalidateLearnedPst();
            GamesCompleted++;
        }
        finally
        {
            _writeGate.Release();
            _games.TryRemove(eventId, out _);
        }
    }

    Task ITurnLearner.LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct)
        => LearnGameAsync(edges, learnContext: "chess/live/batch", adjudicated: false, ct);

    Task ITurnLearner.RecordPlyAsync(
        Hash128 gameId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct)
        => RecordPlyAsync(gameId, ply, fromKey, toKey, moveToken, moverPlayerId, ct);

    Task ITurnLearner.CompleteGameAsync(
        Hash128 gameId, GameOutcome result, bool adjudicated, CancellationToken ct)
        => CompleteGameAsync(gameId, result, adjudicated, ct);

    public async Task LearnGameAsync(
        IReadOnlyList<RecordedEdge> edges, string learnContext, bool adjudicated,
        CancellationToken ct = default)
    {
        if (edges.Count == 0) return;
        var eventId = ChessVocabulary.PlaySessionHandle(Guid.NewGuid());
        await OpenGameAsync(eventId, learnContext, ct: ct);
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            await RecordPlyAsync(eventId, i + 1, e.SubjectKey, e.ObjectKey, "?", null, ct);
        }

        var outcome = InferOutcome(edges, adjudicated);
        await CompleteGameAsync(eventId, outcome, adjudicated, ct);
    }

    private SubstrateRootBias? _rootBias;
    private SubstrateBoardEvaluator? _boardEvaluator;

    public Search BuildSearch(bool substrate, int ttBits = 20, int maxDepth = 8)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        ISearchPositionEvaluator? evaluator = substrate
            ? (_boardEvaluator ??= new SubstrateBoardEvaluator(_ds)) : null;
        return new Search(
            EvalTerm.All, bias, ttBits,
            positionEvaluator: evaluator,
            tablebase: ChessTablebaseRuntime.ProbeSearch);
    }

    public void RefreshSearch(Search search, bool substrate)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        ISearchPositionEvaluator? evaluator = substrate
            ? (_boardEvaluator ??= new SubstrateBoardEvaluator(_ds)) : null;
        search.Reconfigure(
            bias, null, null, evaluator, ChessTablebaseRuntime.ProbeSearch);
    }

    public NpgsqlDataSource DataSource => _ds;

    public Guid StartPlaySession(bool recordToSubstrate = true, string learnContext = "chess/play/session",
        string tenantId = "public", string? userId = null,
        Hash128? whitePlayerId = null, string? whitePlayerName = null,
        Hash128? blackPlayerId = null, string? blackPlayerName = null)
    {
        if (!Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(tenantId))
            throw new ArgumentException($"tenant '{tenantId}' is not a valid identifier", nameof(tenantId));
        if (userId is not null && !Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(userId))
            throw new ArgumentException($"user '{userId}' is not a valid identifier", nameof(userId));

        var id = Guid.NewGuid();
        var eventId = ChessVocabulary.PlaySessionHandle(id);
        var metadata = new ChessLiveGameMetadata(
            Event: "Laplace Play",
            Site: "Laplace",
            Date: DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            StartFen: ChessModality.StartFen);
        _playSessions[id] = new PlaySession(
            eventId, learnContext, recordToSubstrate, tenantId, userId,
            whitePlayerId, whitePlayerName, blackPlayerId, blackPlayerName);
        if (recordToSubstrate)
            _ = OpenGameAsync(
                eventId, learnContext, whitePlayerId, blackPlayerId,
                whitePlayerName, blackPlayerName, metadata);
        return id;
    }

    public PlaySession? GetPlaySession(Guid sessionId)
        => _playSessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task FinishPlaySessionAsync(Guid sessionId, GameOutcome outcome, bool adjudicated, CancellationToken ct)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session)) return;
        if (session.RecordToSubstrate)
        {
            SetGameMetadata(session.EventId, new ChessLiveGameMetadata(
                Termination: adjudicated ? "adjudicated" : "normal"));
            await CompleteGameAsync(session.EventId, outcome, adjudicated, ct);
        }
        _playSessions.TryRemove(sessionId, out _);
    }

    public async Task RecordPlayPlyAsync(
        Guid sessionId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAsync(session.EventId, ply, fromKey, toKey, moveToken, moverPlayerId, ct);
    }

    public async Task RecordPlayPlyAnalysisAsync(
        Guid sessionId, int ply, ChessLivePlyAnalysis analysis, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAnalysisAsync(session.EventId, ply, analysis, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        await _writer.DisposeAsync();
        await _ds.DisposeAsync();
    }

    private static GameOutcome InferOutcome(IReadOnlyList<RecordedEdge> edges, bool adjudicated)
    {
        if (adjudicated) return GameOutcome.Draw;
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].MoverOutcome == PlyOutcome.Win)
                return GameOutcome.WonBy(i % 2);
        }
        return GameOutcome.Draw;
    }

    private static void EnsurePlayingEntity(SubstrateChangeBuilder b, Hash128 playingId, LiveGameSession session)
    {
        if (session.EntityEmitted) return;
        b.AddEntity(playingId, EntityTier.Document, ChessVocabulary.PlayingType, ChessVocabulary.SourceId);
        session.EntityEmitted = true;
    }

    private static (Board Board, Piece Moving, ChessMove Move) ResolveMove(
        string fromKey, string toKey, string token)
    {
        if (!PositionContent.TryFenFromSurface(fromKey, out var fen))
            throw new InvalidOperationException("live move has no typed pre-move board");
        var board = Board.FromFen(fen);
        var legal = MoveGen.Legal(board);
        ChessMove? move = null;
        if (token != "?")
        {
            foreach (var candidate in legal)
                if (candidate.ToUci() == token) { move = candidate; break; }
            if (move is null)
                move = San.Resolve(board, legal, token);
        }

        if (move is null && PositionContent.TryFenFromSurface(toKey, out var toFen))
        {
            string target = Board.FromFen(toFen).ToFen();
            ChessMove? unique = null;
            foreach (var candidate in legal)
            {
                var probe = board.Clone();
                MoveApply.Make(probe, candidate);
                if (probe.ToFen() != target) continue;
                if (unique is not null)
                {
                    unique = null;
                    break;
                }
                unique = candidate;
            }
            move = unique;
        }

        if (move is null)
            throw new InvalidOperationException($"live move '{token}' does not resolve from its pre-move board");
        return (board, board.Squares[move.Value.From], move.Value);
    }

    private static void WitnessResult(SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, GameOutcome result)
    {
        string token = result.ResultToken;
        if (ContentEmitter.Emit(b, token, ChessVocabulary.SourceId) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(
                lineId, "HAS_RESULT", rid,
                ChessVocabulary.SourceId, eventId, WitnessWeight));
    }

    private static void RecordGameMetadata(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 playingId,
        LiveGameSession session, GameOutcome result, bool adjudicated)
    {
        var meta = session.Metadata;
        string date = Clean(meta.Date);
        string eventName = Clean(meta.Event);
        string site = Clean(meta.Site);
        string termination = Clean(meta.Termination);
        if (termination.Length == 0) termination = adjudicated ? "adjudicated" : "normal";

        if (eventName.Length > 0)
        {
            var eventId = ChessVocabulary.PgnEventId(eventName, site, date);
            b.AddEntity(eventId, EntityTier.Document, ChessVocabulary.EventType, ChessVocabulary.SourceId);
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                playingId, ChessVocabulary.HasEventType, eventId,
                ChessVocabulary.SourceId, null, WitnessWeight));
            AddMeta(b, lineId, ChessVocabulary.HasEventType, eventName, playingId);
        }
        AddMeta(b, lineId, ChessVocabulary.OnDateType, date, playingId);
        AddMeta(b, lineId, ChessVocabulary.HasTerminationType, termination, playingId);

        string tc = Clean(meta.TimeControl);
        AddMeta(b, lineId, ChessVocabulary.HasTimeControlType, tc, playingId);
        string tcClass = Clean(meta.TimeControlClass);
        if (tcClass.Length == 0 && tc.Length > 0) tcClass = ChessPgnDecomposer.TcClass(tc);
        AddMeta(b, lineId, ChessVocabulary.HasTcClassType, tcClass, playingId);

        if (session.WhitePlayerId is { } wp && meta.WhiteRating is > 0)
            AddRating(b, wp, meta.WhiteRating.Value, playingId);
        if (session.BlackPlayerId is { } bp && meta.BlackRating is > 0)
            AddRating(b, bp, meta.BlackRating.Value, playingId);

        string? startFen = NonStandardStartFen(meta.StartFen);
        if (startFen is not null)
        {
            try
            {
                var positionId = ChessGraph.EmitPosition(b, Board.FromFen(startFen), ChessVocabulary.SourceId);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    lineId, ChessVocabulary.HasSetupType, positionId,
                    ChessVocabulary.SourceId, playingId, WitnessWeight));
            }
            catch (FormatException) { }
        }

        if (Clean(meta.ExternalGameId) is { Length: > 0 } external
            && ContentEmitter.Emit(b, external, ChessVocabulary.SourceId) is { } externalId)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                playingId, ChessVocabulary.CorrespondsToType, externalId,
                ChessVocabulary.SourceId, null, WitnessWeight));
    }

    private static void AddMeta(
        SubstrateChangeBuilder b, Hash128 lineId, Hash128 typeId, string value, Hash128 playingId)
    {
        if (value.Length == 0) return;
        if (ContentEmitter.Emit(b, value, ChessVocabulary.SourceId) is { } valueId)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                lineId, typeId, valueId, ChessVocabulary.SourceId, playingId, WitnessWeight));
    }

    private static void AddRating(
        SubstrateChangeBuilder b, Hash128 playerId, int rating, Hash128 playingId)
    {
        if (ContentEmitter.Emit(b, rating.ToString(CultureInfo.InvariantCulture), ChessVocabulary.SourceId) is { } ratingId)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                playerId, ChessVocabulary.HasRatingType, ratingId,
                ChessVocabulary.SourceId, playingId, WitnessWeight));
    }

    private static void RecordLivePlyDetails(
        SubstrateChangeBuilder b, Hash128 playingId, IReadOnlyList<ChessNode> movePoints,
        LiveGameSession session, long nowUs)
    {
        if (movePoints.Count == 0) return;
        var missing = ChessGraph.EmitAnnotationMissing(b, ChessVocabulary.SourceId, nowUs);
        var values = new Hash128[movePoints.Count];
        bool any = false;
        for (int i = 0; i < movePoints.Count; i++)
        {
            int ply = i + 1;
            var parts = new List<string>(6);
            if (session.ClockRemainingMs.TryGetValue(ply, out int clockMs))
                parts.Add($"clock_ms={clockMs}");
            if (session.Analysis.TryGetValue(ply, out var a))
            {
                if (a.ScoreCpSideToMove is { } cp) parts.Add($"eval_cp_stm={cp}");
                if (a.Depth > 0) parts.Add($"depth={a.Depth}");
                if (a.Nodes > 0) parts.Add($"nodes={a.Nodes}");
                if (a.Pv is { Count: > 0 }) parts.Add("pv=" + string.Join(' ', a.Pv));
                if (a.Motifs is { Count: > 0 }) parts.Add("motifs=" + string.Join(',', a.Motifs));
            }

            if (parts.Count == 0)
            {
                values[i] = missing.Id;
                continue;
            }
            string token = string.Join(';', parts);
            values[i] = ContentEmitter.Emit(b, token, ChessVocabulary.SourceId) ?? missing.Id;
            any = true;
        }
        if (any)
            ChessGraph.AppendPlayingAnnotationTrajectory(
                b, playingId, values, movePoints,
                PhysicalityType.ChessAnnotation, ChessVocabulary.SourceId, nowUs);
    }

    private static string?[]? CompleteClockTokens(LiveGameSession session)
    {
        if (session.Plies.Count == 0 || session.ClockRemainingMs.Count < session.Plies.Count)
            return null;
        var tokens = new string?[session.Plies.Count];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!session.ClockRemainingMs.TryGetValue(i + 1, out int ms)) return null;
            tokens[i] = ClockToken(ms);
        }
        return tokens;
    }

    private static string ClockToken(int milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(milliseconds);
        int hours = (int)t.TotalHours;
        if (t.Milliseconds == 0) return $"{hours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{hours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}".TrimEnd('0');
    }

    private static string? NonStandardStartFen(string? fen)
    {
        string value = Clean(fen);
        if (value.Length == 0 || value == "startpos" || value == ChessModality.StartFen) return null;
        return value;
    }

    private static string Clean(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) || value == "?" || value == "-" ? "" : value;
    }

    private sealed class LiveGameSession(
        string learnContext,
        Hash128? whitePlayerId,
        Hash128? blackPlayerId,
        string? whitePlayerName,
        string? blackPlayerName,
        ChessLiveGameMetadata metadata)
    {
        public string LearnContext { get; } = learnContext;
        public Hash128? WhitePlayerId { get; private set; } = whitePlayerId;
        public Hash128? BlackPlayerId { get; private set; } = blackPlayerId;
        public string? WhitePlayerName { get; private set; } = whitePlayerName;
        public string? BlackPlayerName { get; private set; } = blackPlayerName;
        public ChessLiveGameMetadata Metadata { get; private set; } = metadata;
        public List<string> Moves { get; } = new();
        public List<RecordedPly> Plies { get; } = new();
        public Dictionary<int, int> ClockRemainingMs { get; } = new();
        public Dictionary<int, ChessLivePlyAnalysis> Analysis { get; } = new();
        public List<Hash128> PositionIds { get; } = new();
        public List<Hash128> MoveIds { get; } = new();

        public void SetPlayers(
            Hash128? nextWhiteId,
            string? nextWhiteName,
            Hash128? nextBlackId,
            string? nextBlackName)
        {
            WhitePlayerId = nextWhiteId ?? WhitePlayerId;
            BlackPlayerId = nextBlackId ?? BlackPlayerId;
            WhitePlayerName = string.IsNullOrWhiteSpace(nextWhiteName) ? WhitePlayerName : nextWhiteName;
            BlackPlayerName = string.IsNullOrWhiteSpace(nextBlackName) ? BlackPlayerName : nextBlackName;
        }

        public void SetMetadata(ChessLiveGameMetadata next)
        {
            Metadata = Metadata with
            {
                Event = Prefer(next.Event, Metadata.Event),
                Site = Prefer(next.Site, Metadata.Site),
                Date = Prefer(next.Date, Metadata.Date),
                TimeControl = Prefer(next.TimeControl, Metadata.TimeControl),
                TimeControlClass = Prefer(next.TimeControlClass, Metadata.TimeControlClass),
                Termination = Prefer(next.Termination, Metadata.Termination),
                StartFen = Prefer(next.StartFen, Metadata.StartFen),
                ExternalGameId = Prefer(next.ExternalGameId, Metadata.ExternalGameId),
                WhiteRating = next.WhiteRating is > 0 ? next.WhiteRating : Metadata.WhiteRating,
                BlackRating = next.BlackRating is > 0 ? next.BlackRating : Metadata.BlackRating,
            };
        }

        private static string? Prefer(string? next, string? current)
            => string.IsNullOrWhiteSpace(next) ? current : next;

        public bool EntityEmitted { get; set; }

        public int MoverSide(int ply) => (ply - 1) % 2;
    }

    private readonly record struct RecordedPly(
        string FromKey, string ToKey, string MoveToken, string San, int MoverSide,
        Piece MovingPiece, ChessMove Move, Hash128? MoverPlayerId);
}

public sealed class PlaySession(
    Hash128 eventId, string learnContext, bool recordToSubstrate,
    string tenantId = "public", string? userId = null,
    Hash128? whitePlayerId = null, string? whitePlayerName = null,
    Hash128? blackPlayerId = null, string? blackPlayerName = null)
{
    public Hash128 EventId { get; } = eventId;
    public string LearnContext { get; } = learnContext;
    public bool RecordToSubstrate { get; } = recordToSubstrate;
    public string TenantId { get; } = tenantId;
    public string? UserId { get; } = userId;
    public Hash128? WhitePlayerId { get; } = whitePlayerId;
    public string? WhitePlayerName { get; } = whitePlayerName;
    public Hash128? BlackPlayerId { get; } = blackPlayerId;
    public string? BlackPlayerName { get; } = blackPlayerName;
    public int PlyCount { get; set; }
    public ChessState? State { get; set; }
    public List<string> Moves { get; } = new();
}
