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
    private const long CheckmateGames = 3;

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

    // connString overrides the installed default — REQUIRED for tests: the default resolves to
    // the production substrate, and a per-ply recorder pointed there writes real consensus rows.
    public static async Task<ChessLiveGameHost> CreateAsync(
        double witnessWeight = 0.5d, string defaultLearnContext = "chess/live/game",
        CancellationToken ct = default, string? connString = null)
    {
        CodepointPerfcache.LoadDefault();
        var conn = connString ?? ChessEngineService.ResolveConnString();
        var ds = new NpgsqlDataSourceBuilder(conn).Build();
        var inner = new NpgsqlSubstrateWriter(ds);
        var writer = new ConsensusAccumulatingWriter(
            inner, ds, foldWorkers: 1, freshSource: false, persistEvidence: true, stageAsWalks: false);
        var reader = new NpgsqlSubstrateReader(ds);
        var host = new SubstrateTurnHost(ds, writer, reader, witnessWeight, defaultLearnContext);
        var canonicalNames = await ChessVocabulary.BootstrapAsync(writer, ct);
        await RegisterCanonicalsAsync(ds, canonicalNames, ct);
        return new ChessLiveGameHost(ds, writer, host);
    }

    public static Hash128 LichessGameId(string lichessGameId)
        => Hash128.OfCanonical($"chess/lichess/{lichessGameId}");

    public Task OpenGameAsync(
        Hash128 gameId, string learnContext, Hash128? whitePlayerId = null, Hash128? blackPlayerId = null,
        CancellationToken ct = default)
    {
        _games[gameId] = new LiveGameSession(learnContext, whitePlayerId, blackPlayerId);
        return Task.CompletedTask;
    }

    public async Task RecordPlyAsync(
        Hash128 gameId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(gameId, out var session))
            throw new InvalidOperationException($"game {gameId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            session.Moves.Add(moveToken);
            session.Plies.Add(new RecordedPly(fromKey, toKey, session.MoverSide(ply), moverPlayerId));

            var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, session.LearnContext);
            EnsureGameEntity(b, gameId, session);
            WitnessMovetext(b, gameId, session.Moves);
            WitnessPlyToken(b, gameId, ply, moveToken);

            ChessGraph.AppendMoveEdge(
                b, fromKey, toKey, PlyOutcome.Draw, games: 1, WitnessWeight,
                sourceId: ChessVocabulary.SourceId,
                moverPlayerId: moverPlayerId ?? ChessVocabulary.LaplacePlayerId,
                contextId: gameId,
                ply: ply);

            var change = await b.BuildAsync(ct);
            await _writer.ApplyAsync(change, ct);
            await _writer.FoldIncrementalAsync(ct);
            InvalidateLearnedPst();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CompleteGameAsync(
        Hash128 gameId, GameOutcome result, bool adjudicated, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(gameId, out var session))
            return;

        await _writeGate.WaitAsync(ct);
        try
        {
            var b = new SubstrateChangeBuilder(ChessVocabulary.SourceId, session.LearnContext);
            WitnessResult(b, gameId, result);

            bool hasWin = session.Plies.Any(p => result.ForMover(p.MoverSide) == PlyOutcome.Win);
            bool checkmate = !adjudicated && hasWin;
            long games = checkmate ? CheckmateGames : 1;

            foreach (var (ply, rp) in session.Plies.Index())
            {
                var moverOutcome = adjudicated ? PlyOutcome.Draw : result.ForMover(rp.MoverSide);
                ChessGraph.AppendMoveEdge(
                    b, rp.FromKey, rp.ToKey, moverOutcome, games, WitnessWeight,
                    sourceId: ChessVocabulary.SourceId,
                    moverPlayerId: rp.MoverPlayerId ?? ChessVocabulary.LaplacePlayerId,
                    contextId: gameId,
                    ply: ply + 1);
            }

            var change = await b.BuildAsync(ct);
            await _writer.ApplyAsync(change, ct);
            await _writer.FoldIncrementalAsync(ct);
            InvalidateLearnedPst();
            GamesCompleted++;
        }
        finally
        {
            _writeGate.Release();
            _games.TryRemove(gameId, out _);
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
        var gameId = Hash128.OfCanonical($"{learnContext}/{GamesCompleted}/{edges.Count}");
        await OpenGameAsync(gameId, learnContext, ct: ct);
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            await RecordPlyAsync(gameId, i + 1, e.SubjectKey, e.ObjectKey, "?", null, ct);
        }

        var outcome = InferOutcome(edges, adjudicated);
        await CompleteGameAsync(gameId, outcome, adjudicated, ct);
    }

    public Search BuildSearch(bool substrate, int ttBits = 20, int maxDepth = 8)
    {
        IRootBias? bias = substrate ? new SubstructureFoldBias(_ds) : null;
        var (mg, eg) = LearnedPstBlend();
        if (!substrate) { mg = null; eg = null; }
        return new Search(EvalTerm.All, bias, ttBits, mg, eg);
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

    public Guid StartPlaySession(bool recordToSubstrate = true, string learnContext = "chess/play/session")
    {
        var id = Guid.NewGuid();
        var gameId = Hash128.OfCanonical($"{learnContext}/{id:N}");
        _playSessions[id] = new PlaySession(gameId, learnContext, recordToSubstrate);
        if (recordToSubstrate)
            _ = OpenGameAsync(gameId, learnContext);
        return id;
    }

    public PlaySession? GetPlaySession(Guid sessionId)
        => _playSessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task FinishPlaySessionAsync(Guid sessionId, GameOutcome outcome, bool adjudicated, CancellationToken ct)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session)) return;
        if (session.RecordToSubstrate)
            await CompleteGameAsync(session.GameId, outcome, adjudicated, ct);
        _playSessions.TryRemove(sessionId, out _);
    }

    public async Task RecordPlayPlyAsync(
        Guid sessionId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAsync(session.GameId, ply, fromKey, toKey, moveToken, moverPlayerId, ct);
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

    private static void EnsureGameEntity(SubstrateChangeBuilder b, Hash128 gameId, LiveGameSession session)
    {
        if (session.EntityEmitted) return;
        b.AddEntity(gameId, EntityTier.Document, ChessVocabulary.GameType, ChessVocabulary.SourceId);
        if (session.WhitePlayerId is { } wp)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_WHITE", wp, ChessVocabulary.SourceId, null, WitnessWeight));
        if (session.BlackPlayerId is { } bp)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_BLACK", bp, ChessVocabulary.SourceId, null, WitnessWeight));
        session.EntityEmitted = true;
    }

    private static void WitnessMovetext(SubstrateChangeBuilder b, Hash128 gameId, IReadOnlyList<string> moves)
    {
        if (moves.Count == 0) return;
        if (ContentEmitter.Emit(b, string.Join(' ', moves), ChessVocabulary.SourceId) is { } mtId)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_MOVETEXT", mtId, ChessVocabulary.SourceId, null, WitnessWeight));
    }

    private static void WitnessPlyToken(SubstrateChangeBuilder b, Hash128 gameId, int ply, string moveToken)
    {
        var plyId = ChessVocabulary.PlyId(gameId, ply - 1);
        b.AddEntity(plyId, EntityTier.Word, ChessVocabulary.PlyType, ChessVocabulary.SourceId);
        b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_PLY", plyId, ChessVocabulary.SourceId, gameId, WitnessWeight));
        if (ContentEmitter.Emit(b, moveToken, ChessVocabulary.SourceId) is { } tokId)
            b.AddAttestation(NativeAttestation.Categorical(plyId, "HAS_SAN", tokId, ChessVocabulary.SourceId, gameId, WitnessWeight));
    }

    private static void WitnessResult(SubstrateChangeBuilder b, Hash128 gameId, GameOutcome result)
    {
        string token = result.IsDraw ? "1/2-1/2" : result.Winner == 0 ? "1-0" : "0-1";
        if (ContentEmitter.Emit(b, token, ChessVocabulary.SourceId) is { } rid)
            b.AddAttestation(NativeAttestation.Categorical(gameId, "HAS_RESULT", rid, ChessVocabulary.SourceId, null, WitnessWeight));
    }

    private static async Task RegisterCanonicalsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "names",
            Value = names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class LiveGameSession(string learnContext, Hash128? whitePlayerId, Hash128? blackPlayerId)
    {
        public string LearnContext { get; } = learnContext;
        public Hash128? WhitePlayerId { get; } = whitePlayerId;
        public Hash128? BlackPlayerId { get; } = blackPlayerId;
        public List<string> Moves { get; } = new();
        public List<RecordedPly> Plies { get; } = new();
        public bool EntityEmitted { get; set; }

        public int MoverSide(int ply) => (ply - 1) % 2;
    }

    private readonly record struct RecordedPly(string FromKey, string ToKey, int MoverSide, Hash128? MoverPlayerId);
}

public sealed class PlaySession(Hash128 gameId, string learnContext, bool recordToSubstrate)
{
    public Hash128 GameId { get; } = gameId;
    public string LearnContext { get; } = learnContext;
    public bool RecordToSubstrate { get; } = recordToSubstrate;
    public int PlyCount { get; set; }

    /// <summary>
    /// Live modality state including repetition history. FEN alone cannot detect threefold.
    /// </summary>
    public ChessState? State { get; set; }

    public List<string> Moves { get; } = new();
}
