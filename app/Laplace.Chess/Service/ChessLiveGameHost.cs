using System.Collections.Concurrent;
using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Single live-game writer: per-ply witness → calculate → fold, terminal outcome pass,
/// and post-fold search factory for Lichess / Play / lab paths.
/// </summary>
public sealed class ChessLiveGameHost : IAsyncDisposable, ITurnLearner
{
    private const double WitnessWeight = 0.7;
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly SubstrateTurnHost _turnHost;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Hash128, LiveGameSession> _games = new();
    private readonly ConcurrentDictionary<Guid, PlaySession> _playSessions = new();

    private bool _learnedTried;
    private int[][]? _lpMg, _lpEg;

    public long GamesCompleted { get; private set; }

    public void InvalidateLearnedPst() => _learnedTried = false;

    private ChessLiveGameHost(
        NpgsqlDataSource ds, ConsensusAccumulatingWriter writer, SubstrateTurnHost turnHost)
    {
        _ds = ds;
        _writer = writer;
        _turnHost = turnHost;
    }

    // The live runtime owns the one chess datasource/write spine. ChessEngineService
    // borrows these components instead of creating and bootstrapping a second writer.
    internal ConsensusAccumulatingWriter Writer => _writer;
    internal SubstrateTurnHost TurnHost => _turnHost;

    // connString overrides the installed default — REQUIRED for tests: the default resolves to
    // the production substrate, and a per-ply recorder pointed there writes real consensus rows.
    public static async Task<ChessLiveGameHost> CreateAsync(
        double witnessWeight = 0.5d, string defaultLearnContext = "chess/live/game",
        CancellationToken ct = default, string? connString = null)
    {
        CodepointPerfcache.LoadDefault();
        var conn = connString ?? ChessEngineService.ResolveConnString();
        var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, conn);
        var inner = new NpgsqlSubstrateWriter(ds);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, persistEvidence: true);
        var reader = new NpgsqlSubstrateReader(ds);
        var host = new SubstrateTurnHost(ds, writer, reader, witnessWeight, defaultLearnContext);
        var canonicalNames = await ChessVocabulary.BootstrapAsync(writer, ct);
        await NpgsqlCanonicalRegistry.RegisterCanonicalsAsync(ds, canonicalNames, ct);
        return new ChessLiveGameHost(ds, writer, host);
    }

    public static Hash128 LichessGameId(string lichessGameId)
        => Hash128.OfCanonical($"chess/lichess/{lichessGameId}");

    // GH #736: the handle a live game is opened under is a ROUTING KEY — it maps plies to a
    // session and never becomes an entity. Neither the line nor the playing exists yet;
    // CompleteGameAsync mints both from what was actually played.
    //
    // This used to be the playing's identity, drawn from a GUID, which meant the same game
    // replayed minted a different entity every time and re-ingest could never dedupe it —
    // the one id in the chess lane that was not a function of what it identifies. It is now
    // content-derived at completion (ChessVocabulary.LivePlayingId).
    public Task OpenGameAsync(
        Hash128 eventId, string learnContext, Hash128? whitePlayerId = null, Hash128? blackPlayerId = null,
        string? whitePlayerName = null, string? blackPlayerName = null,
        CancellationToken ct = default)
    {
        _games[eventId] = new LiveGameSession(
            learnContext, whitePlayerId, blackPlayerId, whitePlayerName, blackPlayerName);
        return Task.CompletedTask;
    }

    /// <summary>Attach source-asserted players once a live provider reveals its game header.</summary>
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

    public async Task RecordPlyAsync(
        Hash128 eventId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            throw new InvalidOperationException($"game {eventId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            var (moving, move) = ResolveMove(fromKey, moveToken);
            session.Moves.Add(moveToken);
            session.Plies.Add(new RecordedPly(
                fromKey, toKey, moveToken, session.MoverSide(ply), moving, move));
            session.MoveIds.Add(ChessCompose.MoveId(moving, move));
            // The ordered position ids the session passes through — the line composition
            // CompleteGameAsync mints (start position first, then one vertex per ply).
            if (session.PositionIds.Count == 0)
                session.PositionIds.Add(ChessCompose.PositionId(fromKey));
            session.PositionIds.Add(ChessCompose.PositionId(toKey));

            // NO SUBSTRATE WRITE HERE. Two reasons, both load-bearing.
            //
            // Identity: the playing is the attestation context for everything this game
            // deposits, and it is content-derived (ChessVocabulary.LivePlayingId) from the
            // line, which does not exist until the last ply. Writing mid-game forced a
            // random session id into the substrate as if it were an entity.
            //
            // Testimony: CompleteGameAsync already re-emits EVERY ply from session.Plies
            // with the real per-mover outcome and the checkmate games weight. The write
            // that used to stand here emitted the same edges with PlyOutcome.Draw first,
            // and testimony does not retract — so every live game deposited a spurious
            // draw witness per ply underneath its own correct one, biasing the fold toward
            // draws in exactly the lane that learns from live play.
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
            var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, session.LearnContext);

            // GH #736: completion mints the LINE — the content entity of what was played —
            // from the ordered position ids the session accumulated. An abandoned playing
            // asserted no completed line, which is why none of this happens at open.
            Hash128 playingId = default;
            if (session.PositionIds.Count > 0)
            {
                var lineId = ChessCompose.LineId(
                    session.PositionIds[0],
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(session.MoveIds));
                b.AddEntity(lineId, EntityTier.Document, ChessVocabulary.GameType, ChessVocabulary.SourceId);

                // The playing exists now, and only now. LivePlayingId closes over the line
                // — itself the Merkle of every position passed through, so the whole move
                // sequence — plus the players, the learn context and the result. Same game
                // played twice mints ONE playing and folds a second witness onto it, which
                // is what content addressing is for; the session handle this method was
                // called with is a routing key and never becomes an entity.
                playingId = ChessVocabulary.LivePlayingId(
                    session.WhitePlayerId, session.BlackPlayerId, session.LearnContext,
                    lineId, result.ResultToken);
                EnsurePlayingEntity(b, playingId, session);

                // The structural playing→line join the read side navigates. It carries no
                // score: HAS_RESULT below is the one witnessed game result.
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    playingId, ChessVocabulary.PlaysLineType, lineId,
                    ChessVocabulary.SourceId, null, WitnessWeight));

                long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
                var movePoints = new ChessNode[session.Plies.Count];
                for (int i = 0; i < movePoints.Length; i++)
                    movePoints[i] = ChessGraph.EmitMove(
                        b, session.Plies[i].MovingPiece, session.Plies[i].Move,
                        ChessVocabulary.SourceId, nowUs);
                ChessGraph.AppendLineTrajectory(
                    b, lineId, movePoints, ChessVocabulary.SourceId, nowUs);
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

                // The player/head-to-head cells are bounded, explicitly reusable statistics.
                // Move, position, substructure, and exact-line outcomes are recovered from
                // this playing's result plus its associated line trajectory instead.
                if (session.WhitePlayerId is { } w2)
                    ChessGraph.AppendPlayerResult(
                        b, w2, session.BlackPlayerId, result.ForMover(0), WitnessWeight,
                        ChessVocabulary.SourceId, playingId);
                if (session.BlackPlayerId is { } b2)
                    ChessGraph.AppendPlayerResult(
                        b, b2, session.WhitePlayerId, result.ForMover(1), WitnessWeight,
                        ChessVocabulary.SourceId, playingId);

                var line = new List<ChessNode>(session.Plies.Count + 1);
                foreach (var rp in session.Plies)
                {
                    var from = ChessGraph.ComposePositionPoint(rp.FromKey);
                    var to = ChessGraph.ComposePositionPoint(rp.ToKey);
                    if (line.Count == 0) line.Add(from);
                    line.Add(to);
                }

                ChessGraph.AppendPositionProjection(
                    b, lineId, line, ChessVocabulary.TrajectorySourceId, nowUs);
                b.AddEntity(
                    ChessTrajectoryDecomposer.MarkerId(lineId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType, ChessVocabulary.TrajectorySourceId);
            }

            var change = await b.BuildAsync(ct);
            await _writer.ApplyAsync(change, ct);
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
        // GH #736: a live occurrence is unique by construction, so the event handle is a
        // fresh GUID per call — never a hash of mutable session state (the old
        // {learnContext}/{GamesCompleted}/{edges.Count} shape collided across restarts).
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

    private SubstructureFoldBias? _foldBias;

    public Search BuildSearch(bool substrate, int ttBits = 20, int maxDepth = 8)
    {
        IRootBias? bias = substrate ? (_foldBias ??= new SubstructureFoldBias(_ds)) : null;
        var (mg, eg) = LearnedPstBlend();
        if (!substrate) { mg = null; eg = null; }
        return new Search(EvalTerm.All, bias, ttBits, mg, eg);
    }

    /// Re-applies the current bias + learned-PST blend to an existing Search
    /// so per-ply PST refreshes reuse the instance (and its 32 MB
    /// transposition table) instead of allocating a new one every ply.
    public void RefreshSearch(Search search, bool substrate)
    {
        IRootBias? bias = substrate ? (_foldBias ??= new SubstructureFoldBias(_ds)) : null;
        var (mg, eg) = LearnedPstBlend();
        if (!substrate) { mg = null; eg = null; }
        search.Reconfigure(bias, mg, eg);
    }

    private (int[][]? Mg, int[][]? Eg) LearnedPstBlend()
    {
        if (_learnedTried) return (_lpMg, _lpEg);
        _learnedTried = true;
        try
        {
            var (lm, le) = LearnedPst.BuildTables(_ds);
            (_lpMg, _lpEg) = Evaluation.BlendPeStoWith(lm, le);
        }
        catch { _lpMg = null; _lpEg = null; }
        return (_lpMg, _lpEg);
    }

    public NpgsqlDataSource DataSource => _ds;

    public Guid StartPlaySession(bool recordToSubstrate = true, string learnContext = "chess/play/session",
        string tenantId = "public", string? userId = null,
        Hash128? whitePlayerId = null, string? whitePlayerName = null,
        Hash128? blackPlayerId = null, string? blackPlayerName = null)
    {
        // Same identifier guard the conversational lane uses (spec 34): tenant and user become
        // canonical-key segments, so the charset is load-bearing even while values are stubbed.
        if (!Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(tenantId))
            throw new ArgumentException($"tenant '{tenantId}' is not a valid identifier", nameof(tenantId));
        if (userId is not null && !Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(userId))
            throw new ArgumentException($"user '{userId}' is not a valid identifier", nameof(userId));

        var id = Guid.NewGuid();
        // Routing key only. The playing entity is minted from content at completion
        // (ChessVocabulary.LivePlayingId); this handle never reaches the substrate.
        var eventId = ChessVocabulary.PlaySessionHandle(id);
        _playSessions[id] = new PlaySession(eventId, learnContext, recordToSubstrate, tenantId, userId);
        if (recordToSubstrate)
            _ = OpenGameAsync(
                eventId, learnContext, whitePlayerId, blackPlayerId,
                whitePlayerName, blackPlayerName);
        return id;
    }

    public PlaySession? GetPlaySession(Guid sessionId)
        => _playSessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task FinishPlaySessionAsync(Guid sessionId, GameOutcome outcome, bool adjudicated, CancellationToken ct)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session)) return;
        if (session.RecordToSubstrate)
            await CompleteGameAsync(session.EventId, outcome, adjudicated, ct);
        _playSessions.TryRemove(sessionId, out _);
    }

    public async Task RecordPlayPlyAsync(
        Guid sessionId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAsync(session.EventId, ply, fromKey, toKey, moveToken, moverPlayerId, ct);
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

    // GH #736: the event entity is a slim provenance handle — no content facts hang off it
    // (colour facts moved to CompleteGameAsync, subjected on the line). It exists as a row
    // solely so the novelty gate can bitmap-probe it.
    // Chess_PLAYING, not Chess_Event. The id passed here is the content-derived playing
    // (LivePlayingId), and it is the subject of PLAYS_LINE and the context on every witness
    // — the playing's role, not the tournament's. Typing it Chess_Event made it invisible
    // to ChessWitnessHydrator once that paged playings, so lab games never reached the
    // analyzer.
    private static void EnsurePlayingEntity(SubstrateChangeBuilder b, Hash128 playingId, LiveGameSession session)
    {
        if (session.EntityEmitted) return;
        b.AddEntity(playingId, EntityTier.Document, ChessVocabulary.PlayingType, ChessVocabulary.SourceId);
        session.EntityEmitted = true;
    }

    private static (Piece Moving, ChessMove Move) ResolveMove(string fromKey, string token)
    {
        if (!PositionContent.TryFenFromSurface(fromKey, out var fen))
            throw new InvalidOperationException("live move has no typed pre-move board");
        var board = Board.FromFen(fen);
        var legal = MoveGen.Legal(board);
        ChessMove? move = null;
        foreach (var candidate in legal)
            if (candidate.ToUci() == token) { move = candidate; break; }
        if (move is null)
            move = San.Resolve(board, legal, token);
        if (move is null)
            throw new InvalidOperationException($"live move '{token}' does not resolve from its pre-move board");
        return (board.Squares[move.Value.From], move.Value);
    }

    private static void WitnessResult(SubstrateChangeBuilder b, Hash128 lineId, Hash128 eventId, GameOutcome result)
    {
        string token = result.ResultToken;
        if (ContentEmitter.Emit(b, token, ChessVocabulary.SourceId) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(
                lineId, "HAS_RESULT", rid, ChessVocabulary.SourceId, eventId, WitnessWeight));
    }

    private sealed class LiveGameSession(
        string learnContext,
        Hash128? whitePlayerId,
        Hash128? blackPlayerId,
        string? whitePlayerName,
        string? blackPlayerName)
    {
        public string LearnContext { get; } = learnContext;
        public Hash128? WhitePlayerId { get; private set; } = whitePlayerId;
        public Hash128? BlackPlayerId { get; private set; } = blackPlayerId;
        public string? WhitePlayerName { get; private set; } = whitePlayerName;
        public string? BlackPlayerName { get; private set; } = blackPlayerName;
        public List<string> Moves { get; } = new();
        public List<RecordedPly> Plies { get; } = new();
        // GH #736: the ordered position ids this playing passes through (start position
        // first) — the line composition CompleteGameAsync mints.
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
        public bool EntityEmitted { get; set; }

        public int MoverSide(int ply) => (ply - 1) % 2;
    }

    private readonly record struct RecordedPly(
        string FromKey, string ToKey, string MoveToken, int MoverSide,
        Piece MovingPiece, ChessMove Move);
}

public sealed class PlaySession(Hash128 eventId, string learnContext, bool recordToSubstrate,
    string tenantId = "public", string? userId = null)
{
    /// <summary>
    /// Session ROUTING KEY (PlaySessionHandle of the session GUID) — not an entity id.
    /// The playing is minted from content at completion by LivePlayingId.
    /// </summary>
    public Hash128 EventId { get; } = eventId;
    public string LearnContext { get; } = learnContext;
    public bool RecordToSubstrate { get; } = recordToSubstrate;

    // Spec-34 identity threaded from the play entry point, stubbed until auth: the tenant scopes
    // the witness source, the user is the within-tenant attribution (a tenant owns many users).
    // Held on the session now; emitted as provenance (tenant→source, user→HAS_ATTRIBUTION) once
    // real values arrive and the chess source declares HAS_ATTRIBUTION.
    public string TenantId { get; } = tenantId;
    public string? UserId { get; } = userId;
    public int PlyCount { get; set; }

    /// <summary>
    /// Live modality state including repetition history. FEN alone cannot detect threefold.
    /// </summary>
    public ChessState? State { get; set; }

    public List<string> Moves { get; } = new();
}
