using System.Text.Json;
using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

internal enum LichessSubmissionDisposition
{
    Accepted,
    Rejected,
    Unknown,
}

internal sealed record LichessPendingSubmission(int Ply, string Uci, ChessLivePlyAnalysis Analysis);

internal sealed record LichessStreamedPly(
    int Ply, string Uci, ChessState Before, ChessMove Move, ChessState After,
    ChessLivePlyAnalysis? SubmittedAnalysis);

internal readonly record struct LichessGameDisposition(string Status, bool Stopped, GameOutcome? Outcome)
{
    public bool CanPlay => !Stopped && Status == "started";
}

/// <summary>
/// Ephemeral transport replay over the existing chess modality and live-game writer.
/// Only a game-stream observation advances the accepted prefix. This is not a game
/// store, identity provider, or move generator.
/// </summary>
internal sealed class LichessGameReplay
{
    private readonly ChessModality _modality = new();
    private readonly List<string> _acceptedMoves = new();
    private LichessPendingSubmission? _pending;

    public LichessGameReplay(string initialFen)
    {
        InitialFen = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
        State = _modality.FromFen(InitialFen);
    }

    public string InitialFen { get; }
    public ChessState State { get; private set; }
    public int AcceptedPlies => _acceptedMoves.Count;
    public bool Faulted { get; private set; }
    public bool HasPendingSubmission => _pending is not null;
    public LichessGameDisposition Disposition { get; private set; } = new("created", false, null);

    public LichessPendingSubmission BeginSubmission(ChessMove move, ChessLivePlyAnalysis analysis)
    {
        RequireUsable();
        if (!Disposition.CanPlay || _pending is not null)
            throw new InvalidOperationException("A move may be submitted only from a started, resolved prefix.");
        var pending = new LichessPendingSubmission(AcceptedPlies + 1, move.ToUci(), analysis);
        _pending = pending;
        return pending;
    }

    public void ResolveSubmission(LichessPendingSubmission pending, LichessSubmissionDisposition disposition)
    {
        RequireUsable();
        // An old HTTP response must never clear a newer attempt.
        if (ReferenceEquals(_pending, pending) && disposition == LichessSubmissionDisposition.Rejected)
            _pending = null;
        // HTTP success and transport uncertainty both await the authoritative stream.
    }

    public async Task<IReadOnlyList<LichessStreamedPly>> ObserveAsync(
        JsonElement state,
        Func<LichessStreamedPly, CancellationToken, Task> append,
        CancellationToken ct = default)
    {
        RequireUsable();
        try
        {
            if (!state.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Lichess game state has no authoritative move list.");
            var tokens = (moves.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < AcceptedPlies)
                throw new InvalidDataException("Lichess move history shrank; takeback replay is unsupported.");
            for (int i = 0; i < AcceptedPlies; i++)
                if (!string.Equals(tokens[i], _acceptedMoves[i], StringComparison.Ordinal))
                    throw new InvalidDataException($"Lichess accepted move changed at ply {i + 1}.");

            var disposition = Classify(state);
            var observed = new List<LichessStreamedPly>();
            for (int i = AcceptedPlies; i < tokens.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ChessMove? move = null;
                foreach (var legal in MoveGen.Legal(State.Board))
                    if (legal.ToUci() == tokens[i]) { move = legal; break; }
                if (move is null)
                    throw new InvalidDataException($"Illegal Lichess streamed move at ply {i + 1}: {tokens[i]}.");
                var before = State;
                var after = _modality.Apply(before, move.Value);
                var analysis = _pending is { } pending && pending.Ply == i + 1 && pending.Uci == tokens[i]
                    ? pending.Analysis : null;
                var ply = new LichessStreamedPly(i + 1, tokens[i], before, move.Value, after, analysis);

                // RecordPlyAsync can append part of its live session before throwing.
                // Never retry a failed append or complete that possibly partial session.
                // The caller must abandon it and open a new session for full replay.
                await append(ply, ct).ConfigureAwait(false);
                State = after;
                _acceptedMoves.Add(tokens[i]);
                if (_pending is { } resolved && resolved.Ply <= AcceptedPlies)
                    _pending = null;
                observed.Add(ply);
            }

            Disposition = disposition;
            if (disposition.Stopped) _pending = null;
            return observed;
        }
        catch
        {
            Faulted = true;
            _pending = null;
            Disposition = new("replay-failed", true, null);
            throw;
        }
    }

    internal static LichessGameDisposition Classify(JsonElement state)
    {
        if (!state.TryGetProperty("status", out var statusValue) ||
            statusValue.ValueKind != JsonValueKind.String)
            return new("missing-status", true, null);
        var status = statusValue.GetString() ?? "";
        if (status is "created" or "started") return new(status, false, null);
        if (status is "aborted" or "unknownFinish") return new(status, true, null);

        bool hasWinner = state.TryGetProperty("winner", out var winnerValue);
        string? winner = hasWinner && winnerValue.ValueKind == JsonValueKind.String
            ? winnerValue.GetString() : null;
        bool noWinner = !hasWinner || winnerValue.ValueKind == JsonValueKind.Null;
        GameOutcome? outcome = null;
        if (status is "draw" or "stalemate" or "insufficientMaterialClaim")
        {
            if (noWinner) outcome = GameOutcome.Draw;
        }
        else if (status is "mate" or "resign" or "timeout" or "outoftime" or "cheat" or "noStart" or "variantEnd")
        {
            outcome = winner switch
            {
                "white" => GameOutcome.WonBy(0),
                "black" => GameOutcome.WonBy(1),
                _ => null,
            };
            // Lichess Finisher.outOfTime/rageQuit explicitly support no winner.
            // A flag with insufficient opposing material is a completed draw.
            if (noWinner && status is ("timeout" or "outoftime"))
                outcome = GameOutcome.Draw;
        }
        return new(status, true, outcome);
    }

    private void RequireUsable()
    {
        if (Faulted)
            throw new InvalidOperationException("A failed replay session must be reopened before recording.");
        if (Disposition.Stopped)
            throw new InvalidOperationException("A terminal replay session cannot accept more actions.");
    }
}
