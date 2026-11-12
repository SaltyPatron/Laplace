using System.Collections.Concurrent;
using System.Globalization;
using global::Npgsql;
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

/// <summary>
/// The games Laplace plays while they are played: each open game's moves, clocks and tags are held in
/// memory, and a game is recorded once it ends, as the PGN its moves make, through the Engine
/// (<see cref="ChessGameRecorder"/>, source laplace-chess). Searches read the Native standings of the
/// moves recorded games played (<see cref="SubstrateRootBias"/>).
/// </summary>
public sealed partial class ChessLiveGameHost : IAsyncDisposable, ITurnLearner
{
    private readonly NpgsqlDataSource _ds;
    private readonly ChessGameRecorder _recorder;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Hash128, LiveGameSession> _games = new();
    private readonly ConcurrentDictionary<Guid, PlaySession> _playSessions = new();
    private readonly object _playRecoveryGate = new();

    public long GamesCompleted { get; private set; }

    internal ChessLiveGameHost(NpgsqlDataSource ds, ChessGameRecorder recorder)
    {
        _ds = ds;
        _recorder = recorder;
    }

    public static Task<ChessLiveGameHost> CreateAsync(
        CancellationToken ct = default, string? connString = null)
    {
        ct.ThrowIfCancellationRequested();
        var conn = connString ?? ChessEngineService.ResolveConnString();
        var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, conn);
        return Task.FromResult(new ChessLiveGameHost(ds, new ChessGameRecorder(conn)));
    }

    /// <summary>The in-memory handle of a Lichess game while it is played; nothing is recorded under it.</summary>
    public static Hash128 LichessGameId(string lichessGameId)
        => Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes(lichessGameId));

    /// <summary>The in-memory handle of a play session's or a self-play game; nothing is recorded under it.</summary>
    public static Hash128 SessionHandle(Guid session) => Hash128.FromBytes(session.ToByteArray());

    public Task OpenGameAsync(
        Hash128 eventId, string? whitePlayerName = null, string? blackPlayerName = null,
        ChessLiveGameMetadata? metadata = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        metadata ??= new ChessLiveGameMetadata(
            Date: DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        _games[eventId] = new LiveGameSession(whitePlayerName, blackPlayerName, metadata);
        return Task.CompletedTask;
    }

    // The scope owns this exact ephemeral session, not every future session
    // with the same external game id. Disposing it never records a game.
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

    public void SetGamePlayers(Hash128 eventId, string? whitePlayerName, string? blackPlayerName)
    {
        if (_games.TryGetValue(eventId, out var session))
            session.SetPlayers(whitePlayerName, blackPlayerName);
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

    /// <summary>
    /// Laplace's own analysis of a ply. It is kept with the session for the play receipt; a game's record
    /// is what was played, and a calculated judgment never masquerades as it (Sequence 27.3).
    /// </summary>
    public async Task RecordPlyAnalysisAsync(
        Hash128 eventId, int ply, ChessLivePlyAnalysis analysis, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session)) return;
        await _writeGate.WaitAsync(ct);
        try { session.Analysis[ply] = analysis; }
        finally { _writeGate.Release(); }
    }

    public async Task RecordPlyAsync(
        Hash128 eventId, int ply, string fromKey, string toKey, string moveToken, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            throw new InvalidOperationException($"game {eventId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            var recorded = ResolveRecordedPly(fromKey, toKey, moveToken);
            if (session.Plies.Count == 0 && session.Metadata.StartFen is null)
                session.SetMetadata(new ChessLiveGameMetadata(StartFen: recorded.FromFen));
            session.Plies.Add(recorded);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Ends a game and records it: the PGN of its moves, players, result and tags, read by the Engine.
    /// Returns the id of the game's line [P0, M0, P1, ...] (the Engine's, <see cref="ChessEngineIdentity"/>),
    /// or <see cref="Hash128.Zero"/> for a game of no moves, which records nothing.
    /// </summary>
    public async Task<Hash128> CompleteGameAsync(
        Hash128 eventId, GameOutcome result, bool adjudicated, CancellationToken ct = default)
    {
        if (!_games.TryGetValue(eventId, out var session))
            throw new InvalidOperationException($"game {eventId} is not open");

        await _writeGate.WaitAsync(ct);
        try
        {
            Hash128 lineId = Hash128.Zero;
            if (session.Plies.Count > 0)
            {
                await _recorder.RecordAsync(Pgn(session, result, adjudicated), ct).ConfigureAwait(false);
                lineId = LineId(session);
            }
            GamesCompleted++;
            _games.TryRemove(eventId, out _);
            return lineId;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    Task ITurnLearner.LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct)
        => LearnGameAsync(edges, "Laplace self-play", adjudicated: false, ct);

    Task ITurnLearner.RecordPlyAsync(
        Hash128 gameId, int ply, string fromKey, string toKey, string moveToken,
        Hash128? moverPlayerId, CancellationToken ct)
        => RecordPlyAsync(gameId, ply, fromKey, toKey, moveToken, ct);

    Task ITurnLearner.CompleteGameAsync(
        Hash128 gameId, GameOutcome result, bool adjudicated, CancellationToken ct)
        => CompleteGameAsync(gameId, result, adjudicated, ct);

    /// <summary>A game Laplace played against itself, recorded as any other game it plays.</summary>
    public async Task LearnGameAsync(
        IReadOnlyList<RecordedEdge> edges, string eventName, bool adjudicated,
        CancellationToken ct = default)
    {
        if (edges.Count == 0) return;
        var eventId = SessionHandle(Guid.NewGuid());
        await OpenGameAsync(eventId, "Laplace", "Laplace", new ChessLiveGameMetadata(
            Event: eventName, Site: "Laplace",
            Date: DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)), ct);
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            await RecordPlyAsync(eventId, i + 1, e.SubjectKey, e.ObjectKey, "?", ct);
        }

        var outcome = InferOutcome(edges, adjudicated);
        await CompleteGameAsync(eventId, outcome, adjudicated, ct);
    }

    private SubstrateRootBias? _rootBias;

    public Search BuildSearch(bool substrate, int ttBits = 20, int maxDepth = 8,
        SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        return new Search(
            EvalTerm.All, bias, ttBits,
            tablebase: ChessTablebaseRuntime.ProbeSearch,
            continuations: continuations ?? new SearchContinuationTable(OpeningContinuationBook.Shared));
    }

    public void RefreshSearch(Search search, bool substrate, SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds)) : null;
        search.Reconfigure(
            bias, null, null, null, ChessTablebaseRuntime.ProbeSearch,
            continuations ?? new SearchContinuationTable(OpeningContinuationBook.Shared));
    }

    public NpgsqlDataSource DataSource => _ds;

    /// <summary>Has the Engine read a PGN file under a source of the estate, or as a file of no corpus.</summary>
    public Task IngestFileAsync(string? source, string path, CancellationToken ct = default)
        => _recorder.IngestFileAsync(source, path, ct);
}
