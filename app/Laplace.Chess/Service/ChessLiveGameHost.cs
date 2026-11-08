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

public sealed partial class ChessLiveGameHost : IAsyncDisposable, ITurnLearner
{
    private const double WitnessWeight = 0.7;
    private readonly NpgsqlDataSource _ds;
    private readonly ConsensusAccumulatingWriter _writer;
    private readonly SubstrateTurnHost _turnHost;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Hash128, LiveGameSession> _games = new();
    private readonly ConcurrentDictionary<Guid, PlaySession> _playSessions = new();
    private readonly object _playRecoveryGate = new();

    public long GamesCompleted { get; private set; }

    public void InvalidateLearnedPst() => _boardEvaluator = null;

    internal ChessLiveGameHost(
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
        => Hash128.Text($"chess/lichess/{lichessGameId}");

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

    // The scope owns this exact ephemeral session, not every future session
    // with the same external game id. Disposing it never writes a game result.
    internal IDisposable? CaptureGameScope(Hash128 eventId)
        => _games.TryGetValue(eventId, out var session)
            ? new LiveGameScope(this, eventId, session) : null;

    private void AbandonGame(Hash128 eventId, LiveGameSession session)
        => ((ICollection<KeyValuePair<Hash128, LiveGameSession>>)_games)
            .Remove(new KeyValuePair<Hash128, LiveGameSession>(eventId, session));

    private sealed class LiveGameScope(
        ChessLiveGameHost owner, Hash128 eventId, LiveGameSession session) : IDisposable
    {
        public void Dispose() => owner.AbandonGame(eventId, session);
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
            var recorded = ResolveRecordedPly(fromKey, toKey, moveToken, moverPlayerId);
            session.Moves.Add(moveToken == "?" ? recorded.MoveToken : moveToken);
            session.Plies.Add(recorded);
            session.MoveIds.Add(ChessCompose.MoveId(recorded.MovingPiece, recorded.Move));
            if (session.PositionIds.Count == 0)
                session.PositionIds.Add(ChessCompose.PositionId(fromKey));
            session.PositionIds.Add(ChessCompose.PositionId(toKey));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<Hash128> CompleteGameAsync(
        Hash128 eventId, GameOutcome result, bool adjudicated, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            throw new InvalidOperationException($"game {eventId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            using var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, session.LearnContext)
                .DeclareSourcePrior(SourceTrust.Response)
                .DeclareSourcePrior(ChessTransitions.SourceId, SourceTrust.StructuredCorpus)
                .DeclareSourcePrior(ChessPositionOutcomes.SourceId, SourceTrust.StructuredCorpus);

            Hash128 playingId = default;
            if (session.PositionIds.Count > 0)
            {
                var lineId = ChessCompose.LineId(
                    session.PositionIds[0],
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(session.MoveIds));
                b.AddEntity(lineId, ChessCompose.LineFloor(session.MoveIds.Count), ChessVocabulary.GameType);

                playingId = ChessVocabulary.LivePlayingId(
                    session.WhitePlayerId, session.BlackPlayerId, session.LearnContext,
                    lineId, result.ResultToken, session.Metadata.ExternalGameId);
                b.AddEntity(playingId, EntityTier.Document, ChessVocabulary.PlayingType);

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
                    result, session.Plies[0].MoverSide == 0, ChessVocabulary.SourceId, WitnessWeight);
                WitnessResult(b, lineId, playingId, result);
                if (session.WhitePlayerId is { } emitWhite && session.WhitePlayerName is { Length: > 0 } whiteName)
                    ChessVocabulary.EmitPlayer(
                        b, emitWhite, whiteName, ChessVocabulary.SourceId, SourceTrust.Response);
                if (session.BlackPlayerId is { } emitBlack && session.BlackPlayerName is { Length: > 0 } blackName)
                    ChessVocabulary.EmitPlayer(
                        b, emitBlack, blackName, ChessVocabulary.SourceId, SourceTrust.Response);
                if (session.WhitePlayerId is { } wp)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lineId, "White", wp, ChessVocabulary.SourceId, playingId, WitnessWeight));
                if (session.BlackPlayerId is { } bp)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lineId, "Black", bp, ChessVocabulary.SourceId, playingId, WitnessWeight));

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
                    b, session.PositionIds, result, session.Plies[0].MoverSide == 0,
                    SourceTrust.StructuredCorpus,
                    ChessTransitions.SourceId, playingId);
                b.AddEntity(
                    ChessTransitions.MarkerId(playingId), EntityTier.Document,
                    ChessVocabulary.AnalysisMarkerType);
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
            // Overlapping appliers may land the same calculated testimony; re-probe (see
            // ChessPgnIngestor.ApplyAdmittedAsync) instead of failing the live game.
            try
            {
                await Laplace.Ingestion.TransientErrorRetryPolicy.ConcurrencyRetry.ExecuteAsync(
                    token => _writer.ApplyAsync(change, token), ct);
            }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
            if (session.PositionIds.Count > 1)
                ChessTransitionObservations.MarkObserved(
                    session.PositionIds.Take(session.PositionIds.Count - 1), session.MoveIds);
            InvalidateLearnedPst();
            GamesCompleted++;
            _games.TryRemove(eventId, out _);
            return playingId;
        }
        finally
        {
            _writeGate.Release();
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

    public Search BuildSearch(bool substrate, int ttBits = 20, int maxDepth = 8,
        SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        ISearchPositionEvaluator? evaluator = substrate
            ? (_boardEvaluator ??= new SubstrateBoardEvaluator(_ds)) : null;
        return new Search(
            EvalTerm.All, bias, ttBits,
            positionEvaluator: evaluator,
            tablebase: ChessTablebaseRuntime.ProbeSearch,
            continuations: continuations ?? new SearchContinuationTable(OpeningContinuationBook.Shared));
    }

    public void RefreshSearch(Search search, bool substrate, SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        ISearchPositionEvaluator? evaluator = substrate
            ? (_boardEvaluator ??= new SubstrateBoardEvaluator(_ds)) : null;
        search.Reconfigure(
            bias, null, null, evaluator, ChessTablebaseRuntime.ProbeSearch,
            continuations ?? new SearchContinuationTable(OpeningContinuationBook.Shared));
    }

    public NpgsqlDataSource DataSource => _ds;

}
