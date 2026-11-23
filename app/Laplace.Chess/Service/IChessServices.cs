using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Npgsql;

namespace Laplace.Chess.Service;

/// <summary>Public service contract for board rules, move search, exploration, and interactive play sessions.</summary>
public interface IChessEngineService : IAsyncDisposable
{
    string NewGameFen();
    Task<ChessLegalResponse> LegalAsync(string fen, CancellationToken ct = default);
    Task<ChessLegalResponse> LegalAsync(string fen, string? variant, CancellationToken ct = default);
    Task<IReadOnlyList<ChessMoveScore>> ScoreAsync(string fen, CancellationToken ct = default);
    Task<ChessExploreResponse> ExploreAsync(string fen, int limit = 12, CancellationToken ct = default);
    Task<ChessExploreResponse> ExploreAsync(string fen, int limit, string? variant, CancellationToken ct = default);
    Task<ChessPositionEval> EvalPositionAsync(string fen, int depth = 4, bool substrate = true, CancellationToken ct = default);
    Task<ChessPositionEval> EvalPositionAsync(string fen, int depth, bool substrate, string? variant, CancellationToken ct = default);
    Task<ChessBestMove> BestMoveAsync(string fen, double temperature = 0d, CancellationToken ct = default);
    Task<ChessBestMove> BestMoveSearchAsync(string fen, int depth = 4, bool substrate = true, IReadOnlyList<string>? moves = null, CancellationToken ct = default);
    Task<ChessBestMove> BestMoveSearchAsync(string fen, int depth, bool substrate, IReadOnlyList<string>? moves, string? variant, CancellationToken ct = default);
    Task<ChessApplyResult> ApplyMoveAsync(string fen, string uci, IReadOnlyList<string>? moves = null, CancellationToken ct = default);
    Task<ChessApplyResult> ApplyMoveAsync(string fen, string uci, IReadOnlyList<string>? moves, string? variant, CancellationToken ct = default);

    Task<ChessPlayStart> StartPlaySessionAsync(
        bool recordToSubstrate = true, IReadOnlyList<string>? moves = null,
        string tenantId = "public", string? userId = null, CancellationToken ct = default,
        OperationPrincipal? requester = null);
    Task<ChessPlayStart> StartPlaySessionAsync(
        bool recordToSubstrate, IReadOnlyList<string>? moves,
        string tenantId, string? userId, string? variant, string? startFen,
        OperationPrincipal? requester = null, CancellationToken ct = default);
    Task<ChessPlayMoveResult> PlayMoveAsync(Guid sessionId, string fen, string uci, CancellationToken ct = default);
    Task<ChessBestMove> PlayBestMoveAsync(Guid sessionId, string fen, int depth = 4, bool substrate = true, CancellationToken ct = default);
    Task FinishPlaySessionAsync(Guid sessionId, string status, bool adjudicated = false, CancellationToken ct = default);
    Task FinishPlaySessionAsync(Guid sessionId, string? status, bool? adjudicated, CancellationToken ct = default);

    bool StartTraining(double temperature, double weight, int maxPlies, int maxGames = 0);
    bool StopTraining();
    ChessTrainStatus Status();
}

/// <summary>Public contract for managing live recorded chess games and browser play sessions.</summary>
public interface IChessLiveGameHost : IAsyncDisposable, ITurnLearner
{
    long GamesCompleted { get; }
    NpgsqlDataSource DataSource { get; }
    Task OpenGameAsync(Hash128 eventId, string? whitePlayerName = null, string? blackPlayerName = null,
        ChessLiveGameMetadata? metadata = null, CancellationToken ct = default);
    void SetGamePlayers(Hash128 eventId, string? whitePlayerName, string? blackPlayerName);
    void SetGameMetadata(Hash128 eventId, ChessLiveGameMetadata metadata);
    Task RecordPlyClockAsync(Hash128 eventId, int ply, int remainingMs, CancellationToken ct = default);
    Task RecordPlyAnalysisAsync(Hash128 eventId, int ply, ChessLivePlyAnalysis analysis, CancellationToken ct = default);
    Task RecordPlyAsync(Hash128 eventId, int ply, string fromKey, string toKey, string moveToken, CancellationToken ct = default);
    new Task<Hash128> CompleteGameAsync(Hash128 eventId, GameOutcome result, bool adjudicated, CancellationToken ct = default);
    Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, string eventName, bool adjudicated, CancellationToken ct = default);
    PlaySession? GetPlaySession(Guid sessionId, bool resumeRecording = false);
    Task FinishPlaySessionAsync(Guid sessionId, GameOutcome outcome, bool adjudicated, CancellationToken ct);
    Task RetryPlayRecordingAsync(Guid sessionId, CancellationToken ct);
    Task IngestFileAsync(string? source, string path, CancellationToken ct = default);
}

/// <summary>Public contract for Chess Lab match/evaluation jobs and live SSE streams.</summary>
public interface IChessLabService
{
    string? StartJob(ChessLabJobKind kind, IReadOnlyDictionary<string, string>? config = null, OperationPrincipal? requester = null);
    bool StopJob(string jobId);
    ChessLabJob? GetJob(string jobId);
    IReadOnlyList<ChessLabJob> ListJobs();
    ChannelReader<ChessLabEvent>? EventReader(string jobId);
    ChessLabEventStream? Events(string jobId);
    ChessLabTerminal? Terminal(string jobId);
    Task<ChessLabArtifactIngestResult?> IngestArtifactAsync(string jobId, CancellationToken ct = default);
}
