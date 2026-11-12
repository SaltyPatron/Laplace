using System.Collections.Concurrent;
using System.Globalization;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public sealed partial class ChessLiveGameHost
{
    public Guid StartPlaySession(bool recordToSubstrate = true, string learnContext = "chess/play/session",
        string tenantId = "public", string? userId = null,
        string? whitePlayerName = null, string? blackPlayerName = null, OperationPrincipal? requester = null)
    {
        if (!Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(tenantId))
            throw new ArgumentException($"tenant '{tenantId}' is not a valid identifier", nameof(tenantId));
        if (userId is not null && !Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(userId))
            throw new ArgumentException($"user '{userId}' is not a valid identifier", nameof(userId));

        var id = Guid.NewGuid();
        if (requester is not null && (requester.TenantId != tenantId || requester.Subject != userId))
            throw new ArgumentException("Play ownership must match the authenticated requester.");
        var eventId = SessionHandle(id);
        var metadata = new ChessLiveGameMetadata(
            Event: "Laplace Play",
            Site: "Laplace",
            Date: DateTimeOffset.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            StartFen: ChessModality.StartFen);
        var session = new PlaySession(
            eventId, learnContext, recordToSubstrate, tenantId, userId,
            whitePlayerName, blackPlayerName, requester)
        {
            Metadata = metadata,
            Operation = requester is null ? null : ChessPlayOperation.Start(id, requester, recordToSubstrate) with
            {
                Context = new(learnContext, null, whitePlayerName, null, blackPlayerName, metadata),
            },
        };
        session.SaveOperation();
        _playSessions[id] = session;
        if (recordToSubstrate)
            _ = OpenGameAsync(eventId, whitePlayerName, blackPlayerName, metadata);
        return id;
    }

    public PlaySession? GetPlaySession(Guid sessionId, bool resumeRecording = false)
    {
        if (_playSessions.TryGetValue(sessionId, out var session)) return session;
        lock (_playRecoveryGate)
        {
            if (_playSessions.TryGetValue(sessionId, out session)) return session;
            var operation = ChessPlayOperation.Read(sessionId);
            if (operation is null || (operation.State != "ongoing" && !(resumeRecording && operation.CanRetryRecording)))
                return null;
            session = operation.Restore(resumeRecording);
            if (session.RecordToSubstrate) RestorePlayRecording(session);
            _playSessions[sessionId] = session;
            return session;
        }
    }

    private void RestorePlayRecording(PlaySession session)
    {
        var game = new LiveGameSession(session.WhitePlayerName, session.BlackPlayerName, session.Metadata!);
        var modality = new ChessModality();
        var state = modality.FromFen(session.Metadata!.StartFen ?? ChessModality.StartFen);
        foreach (var uci in session.Moves)
        {
            var move = MoveGen.Legal(state.Board).First(m => m.ToUci() == uci);
            var next = modality.Apply(state, move);
            game.Plies.Add(ResolveRecordedPly(modality.StateKey(state), modality.StateKey(next), uci));
            state = next;
        }
        if (session.Operation is { } operation)
            foreach (var (ply, analysis) in operation.Analysis) game.Analysis.Add(ply, analysis);
        _games[session.EventId] = game;
    }

    // One recording per game: a completion still running (started after a move's response)
    // is awaited by finish and retry rather than recorded a second time beside it.
    private readonly PlayRecordingCompletions _completions = new();

    public async Task FinishPlaySessionAsync(Guid sessionId, GameOutcome outcome, bool adjudicated, CancellationToken ct)
    {
        if (_completions.Running(sessionId) is { } running) { await running.WaitAsync(ct); return; }
        var session = BeginFinish(sessionId, outcome, adjudicated);
        await CompleteFinishAsync(sessionId, session, outcome, adjudicated, ct);
    }

    /// <summary>
    /// Ends a game whose last move is about to be returned. The durable receipt (state
    /// "recording") is written before this returns, so the reply never outruns it; the game
    /// is then recorded by the same completion as <see cref="FinishPlaySessionAsync"/>, after
    /// the response. A failure leaves the receipt "failed" and a lost process leaves it
    /// "recording": both are retried by /chess/play/{id}/recording/retry, never dropped.
    /// </summary>
    public void FinishPlaySessionAfterReply(Guid sessionId, GameOutcome outcome, bool adjudicated)
        => _completions.Start(sessionId,
            () => BeginFinish(sessionId, outcome, adjudicated),
            session => CompleteFinishAsync(sessionId, session, outcome, adjudicated, CancellationToken.None),
            (failure, ms) =>
            {
                // A failure is already the "failed" receipt that retry resumes; it is reported here too.
                if (failure is null)
                    System.Diagnostics.Trace.TraceInformation("CHESS_PLAY_RECORDED session={0} ms={1}", sessionId, ms);
                else
                    System.Diagnostics.Trace.TraceError(
                        "CHESS_PLAY_RECORDING_FAILED session={0} ms={1}; retained for retry: {2}", sessionId, ms, failure);
            });

    /// <summary>Validates the ending and writes the "recording" receipt; no substrate write.</summary>
    private PlaySession BeginFinish(Guid sessionId, GameOutcome outcome, bool adjudicated)
    {
        var session = GetPlaySession(sessionId, resumeRecording: true)
            ?? throw new InvalidOperationException("The play operation is not available for recording.");
        if (session.Operation is { State: not "ongoing" } retained)
        {
            if (!retained.CanRetryRecording || retained.Result != outcome.ResultToken || retained.Adjudicated != adjudicated)
                throw new InvalidOperationException("A recording retry must retain the original result and adjudication.");
        }
        if (session.RecordToSubstrate)
        {
            if (session.Operation is { } pending)
                session.Operation = pending with { Adjudicated = adjudicated };
            session.SaveOperation("recording", "in_progress", outcome.ResultToken);
        }
        return session;
    }

    /// <summary>Records the game into the substrate and writes the final receipt.</summary>
    private async Task CompleteFinishAsync(Guid sessionId, PlaySession session, GameOutcome outcome, bool adjudicated, CancellationToken ct)
    {
        try
        {
            if (session.RecordToSubstrate)
            {
                if (session.Operation is not null) RestorePlayRecording(session);
                SetGameMetadata(session.EventId, new ChessLiveGameMetadata(
                    Termination: adjudicated ? "adjudicated" : "normal"));
                var lineId = await CompleteGameAsync(session.EventId, outcome, adjudicated, ct);
                if (session.Operation is { } operation && lineId != Hash128.Zero)
                    session.Operation = operation with { RecordedPlayingId = Convert.ToHexString(lineId.ToBytes()).ToLowerInvariant() };
            }
            session.SaveOperation("completed", !session.RecordToSubstrate ? "disabled"
                : session.Moves.Count == 0 ? "empty" : "recorded", outcome.ResultToken);
        }
        catch (Exception error)
        {
            session.SaveOperation("failed", session.RecordToSubstrate ? "failed" : "disabled", outcome.ResultToken, error.GetType().Name);
            throw;
        }
        _playSessions.TryRemove(sessionId, out _);
    }

    public Task RetryPlayRecordingAsync(Guid sessionId, CancellationToken ct)
    {
        if (_completions.Running(sessionId) is { } running) return running.WaitAsync(ct);
        var operation = GetPlaySession(sessionId, resumeRecording: true)?.Operation;
        if (operation?.CanRetryRecording != true)
            throw new InvalidOperationException("There is no retained recording to retry.");
        var outcome = operation.Result switch
        {
            "1-0" => GameOutcome.WonBy(0),
            "0-1" => GameOutcome.WonBy(1),
            _ => GameOutcome.Draw,
        };
        return FinishPlaySessionAsync(sessionId, outcome, operation.Adjudicated!.Value, ct);
    }

    public async Task RecordPlayPlyAsync(
        Guid sessionId, int ply, string fromKey, string toKey, string moveToken, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAsync(session.EventId, ply, fromKey, toKey, moveToken, ct);
    }

    public async Task RecordPlayPlyAnalysisAsync(
        Guid sessionId, int ply, ChessLivePlyAnalysis analysis, CancellationToken ct = default)
    {
        if (!_playSessions.TryGetValue(sessionId, out var session) || !session.RecordToSubstrate) return;
        await RecordPlyAnalysisAsync(session.EventId, ply, analysis, ct);
    }

    public async ValueTask DisposeAsync()
    {
        // A recording started after a reply finishes before its writer and pool close.
        try { await _completions.All(); } catch { /* each failure is already a retryable receipt */ }
        _writeGate.Dispose();
        await _ds.DisposeAsync();
    }

    internal static GameOutcome InferOutcome(IReadOnlyList<RecordedEdge> edges, bool adjudicated)
    {
        if (adjudicated) return GameOutcome.Draw;
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].MoverOutcome != PlyOutcome.Win) continue;
            if (!PositionContent.TryFenFromSurface(edges[i].SubjectKey, out var fen))
                throw new InvalidOperationException("live outcome has no typed pre-move board");
            return GameOutcome.WonBy(Board.FromFen(fen).WhiteToMove ? 0 : 1);
        }
        return GameOutcome.Draw;
    }

    internal static RecordedPly ResolveRecordedPly(string fromKey, string toKey, string moveToken)
    {
        var (board, _, move) = ResolveMove(fromKey, toKey, moveToken);
        return new RecordedPly(board.ToFen(), move.ToUci(), San.ToSan(board, move), board.WhiteToMove ? 0 : 1,
            board, move);
    }

    /// <summary>The game's PGN: the Seven Tag Roster, the tags the game was played under, its moves and clocks.</summary>
    internal static string Pgn(LiveGameSession session, GameOutcome result, bool adjudicated)
    {
        var meta = session.Metadata;
        string termination = Clean(meta.Termination);
        if (termination.Length == 0) termination = adjudicated ? "adjudicated" : "normal";
        var tags = new List<KeyValuePair<string, string>>
        {
            new("Event", Tag(meta.Event)),
            new("Site", Tag(meta.Site)),
            new("Date", Tag(meta.Date)),
            new("Round", "-"),
            new("White", Tag(session.WhitePlayerName)),
            new("Black", Tag(session.BlackPlayerName)),
            new("Result", result.ResultToken),
        };
        if (meta.WhiteRating is > 0) tags.Add(new("WhiteElo", meta.WhiteRating.Value.ToString(CultureInfo.InvariantCulture)));
        if (meta.BlackRating is > 0) tags.Add(new("BlackElo", meta.BlackRating.Value.ToString(CultureInfo.InvariantCulture)));
        if (Clean(meta.TimeControl) is { Length: > 0 } timeControl) tags.Add(new("TimeControl", timeControl));
        tags.Add(new("Termination", termination));
        if (Clean(meta.ExternalGameId) is { Length: > 0 } external) tags.Add(new("GameId", external));
        string startFen = session.Plies[0].FromFen;
        if (NonStandardStartFen(startFen) is { } setup)
        {
            tags.Add(new("SetUp", "1"));
            tags.Add(new("FEN", setup));
        }
        return ChessPgnWriter.Game(tags, session.Plies.Select(static p => p.San).ToArray(), result.ResultToken,
            startFen, CompleteClockTokens(session));
    }

    /// <summary>The Engine's id of the game's line [P0, M0, P1, ...], or zero where its rules have none.</summary>
    private static Hash128 LineId(LiveGameSession session)
    {
        var start = session.Plies[0].Before;
        if (!ChessEngineIdentity.Covers(start)) return Hash128.Zero;
        return ChessEngineIdentity.LineId(ChessEngineIdentity.Trajectory(start, session.Plies.Select(static p => p.Move)));
    }

    private static string Tag(string? value) => Clean(value) is { Length: > 0 } clean ? clean : "?";

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
        return (board, board.MovingPiece(move.Value), move.Value);
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

    internal sealed class LiveGameSession(
        string? whitePlayerName,
        string? blackPlayerName,
        ChessLiveGameMetadata metadata)
    {
        public string? WhitePlayerName { get; private set; } = whitePlayerName;
        public string? BlackPlayerName { get; private set; } = blackPlayerName;
        public ChessLiveGameMetadata Metadata { get; private set; } = metadata;
        public List<RecordedPly> Plies { get; } = new();
        public Dictionary<int, int> ClockRemainingMs { get; } = new();
        public Dictionary<int, ChessLivePlyAnalysis> Analysis { get; } = new();

        public void SetPlayers(string? nextWhiteName, string? nextBlackName)
        {
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

    }

    internal readonly record struct RecordedPly(
        string FromFen, string Uci, string San, int MoverSide, Board Before, ChessMove Move);
}

public sealed class PlaySession(
    Hash128 eventId, string learnContext, bool recordToSubstrate,
    string tenantId = "public", string? userId = null,
    string? whitePlayerName = null, string? blackPlayerName = null, OperationPrincipal? requester = null)
{
    public Hash128 EventId { get; } = eventId;
    public string LearnContext { get; } = learnContext;
    public bool RecordToSubstrate { get; } = recordToSubstrate;
    public string TenantId { get; } = tenantId;
    public string? UserId { get; } = userId;
    public OperationPrincipal? Requester { get; } = requester;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    internal ChessPlayOperation? Operation { get; set; }
    internal ChessLiveGameMetadata? Metadata { get; init; }
    public string? WhitePlayerName { get; } = whitePlayerName;
    public string? BlackPlayerName { get; } = blackPlayerName;
    public int PlyCount { get; set; }
    public ChessState? State { get; set; }
    public List<string> Moves { get; } = new();

    internal void SaveOperation(string state = "ongoing", string? recording = null, string? result = null, string? error = null)
    {
        if (Operation is not { } operation) return;
        var next = operation with { UpdatedUtc = DateTimeOffset.UtcNow, State = state, Recording = recording ?? operation.Recording,
            Result = result, Fen = State?.Board.ToFen(), Moves = Moves.ToArray(), Error = error };
        next.Save();
        Operation = next;
    }

    internal void CommitMove(ChessState nextState, string uci, ChessLivePlyAnalysis analysis)
    {
        if (Operation is { } operation)
        {
            var next = operation with { UpdatedUtc = DateTimeOffset.UtcNow, Fen = nextState.Board.ToFen(),
                Moves = [.. Moves, uci], Analysis = new(operation.Analysis) { [Moves.Count + 1] = analysis } };
            next.Save();
            Operation = next;
        }
        State = nextState;
        Moves.Add(uci);
        PlyCount = Moves.Count;
    }
}
