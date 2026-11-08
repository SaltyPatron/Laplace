using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed partial class LichessBot
{
    private async Task PlayGameAsync(string lichessGameId, bool weAreWhite, CancellationToken ct)
    {
        var modality = new ChessModality();
        var substrateGameId = ChessLiveGameHost.LichessGameId(lichessGameId);
        LichessGameReplay? replay = null;
        GameOutcome? outcome = null;
        ILichessMoveProvider? provider = null;
        var receipts = new List<EngineReceipt>();

        if (_record)
            await _host.OpenGameAsync(
                substrateGameId, "chess/lichess/game",
                metadata: new ChessLiveGameMetadata(
                    Event: "Lichess",
                    Site: "lichess.org",
                    ExternalGameId: $"lichess:{lichessGameId}"),
                ct: ct);

        // The compiler emits a finally for this scope: every unscored, canceled
        // or failed stream releases only its own live session, without completion.
        using var gameScope = _record ? _host.CaptureGameScope(substrateGameId) : null;

        try
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                _http, $"/api/bot/game/stream/{lichessGameId}", _log, ct))
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "chatLine")
                {
                    string room = ev.TryGetProperty("room", out var r) ? r.GetString() ?? "player" : "player";
                    string user = ev.TryGetProperty("username", out var u) ? u.GetString() ?? "?" : "?";
                    string text = ev.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    _onChatLine?.Invoke(new LichessChatLine(lichessGameId, room, user, text));
                    continue;
                }

                if (type is not ("gameFull" or "gameState")) continue;

                JsonElement stateEl;
                int wtime, btime, winc, binc;
                string initialFen;

                if (type == "gameFull")
                {
                    var sourceWhite = ReadPlayerName(ev, "white") ?? "Lichess player";
                    var sourceBlack = ReadPlayerName(ev, "black") ?? "Lichess player";
                    var whiteName = weAreWhite ? (_botUsername ?? sourceWhite) : sourceWhite;
                    var blackName = weAreWhite ? sourceBlack : (_botUsername ?? sourceBlack);
                    _host.SetGamePlayers(
                        substrateGameId,
                        weAreWhite ? ChessVocabulary.LaplacePlayerId : ChessVocabulary.PlayerId(whiteName), whiteName,
                        weAreWhite ? ChessVocabulary.PlayerId(blackName) : ChessVocabulary.LaplacePlayerId, blackName);
                    stateEl = ev.GetProperty("state");
                    wtime = stateEl.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = stateEl.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = stateEl.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = stateEl.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = ev.TryGetProperty("initialFen", out var fen) ? fen.GetString() ?? "startpos" : "startpos";
                    var startFenMeta = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                    _host.SetGameMetadata(substrateGameId, new ChessLiveGameMetadata(
                        Event: "Lichess",
                        Site: "lichess.org",
                        Date: ReadCreatedDate(ev),
                        TimeControl: ReadTimeControl(ev),
                        TimeControlClass: ReadSpeedClass(ev),
                        StartFen: startFenMeta,
                        ExternalGameId: $"lichess:{lichessGameId}",
                        WhiteRating: ReadPlayerRating(ev, "white"),
                        BlackRating: ReadPlayerRating(ev, "black")));
                }
                else
                {
                    stateEl = ev;
                    wtime = ev.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = ev.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = ev.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = ev.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = "startpos";
                }

                var startFen = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                if (replay is null)
                {
                    if (type != "gameFull")
                        throw new InvalidDataException("Lichess game stream did not start with gameFull.");
                    replay = new LichessGameReplay(startFen);
                }
                else if (type == "gameFull" && startFen != replay.InitialFen)
                    throw new InvalidDataException("Lichess initial position changed during replay.");

                var observed = await replay.ObserveAsync(stateEl,
                    (ply, token) => _record
                        ? _host.RecordPlyAsync(substrateGameId, ply.Ply,
                            modality.StateKey(ply.Before), modality.StateKey(ply.After), ply.Uci,
                            PlayerIdForSide(modality.SideToMove(ply.Before), weAreWhite), token)
                        : Task.CompletedTask, ct);

                // These side observations follow a successfully accepted ply. Keep them
                // outside the append callback so replay cannot retry a recorded move if
                // analysis or commentary fails.
                if (_record)
                {
                    foreach (var ply in observed)
                    {
                        var motifs = ChessMotifs.DetectAtPly(ply.Before.Board, ply.Move, ply.After.Board).ToList();
                        var analysis = ply.SubmittedAnalysis is { } submitted
                            ? submitted with { Motifs = motifs }
                            : new ChessLivePlyAnalysis(Motifs: motifs);
                        if (ply.SubmittedAnalysis is not null || motifs.Count > 0)
                            await _host.RecordPlyAnalysisAsync(substrateGameId, ply.Ply, analysis, ct);

                        if (ply.SubmittedAnalysis?.ScoreCpSideToMove is int score)
                        {
                            try
                            {
                                int whiteCp = ply.Before.Board.WhiteToMove ? score : -score;
                                string comment = await ChessMoveCommentary.BuildAsync(
                                    _host.DataSource,
                                    new ChessMoveCommentary.Inputs(whiteCp, analysis.Depth,
                                        analysis.Pv ?? Array.Empty<string>(), motifs,
                                        PositionSurface: modality.StateKey(ply.After),
                                        UciMoves: replay.AcceptedMoves,
                                        StartFen: replay.InitialFen),
                                    ct);
                                if (!string.IsNullOrWhiteSpace(comment))
                                    await PostChatAsync(lichessGameId, "player", comment, ct);
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                _log.LogDebug(ex, "game {Id}: commentary/chat skipped", lichessGameId);
                            }
                        }
                    }

                    if (replay.AcceptedPlies > 0)
                    {
                        // Initial FEN can start with Black; ply parity is not color.
                        int remaining = replay.State.Board.WhiteToMove ? btime : wtime;
                        if (remaining >= 0)
                            await _host.RecordPlyClockAsync(substrateGameId, replay.AcceptedPlies, remaining, ct);
                    }
                }

                if (replay.Disposition.Stopped)
                {
                    if (_record)
                        _host.SetGameMetadata(substrateGameId,
                            new ChessLiveGameMetadata(Termination: replay.Disposition.Status));
                    outcome = replay.Disposition.Outcome;
                    if (outcome is null)
                        _log.LogInformation("game {Id}: stopped with {Status}; no completed result recorded",
                            lichessGameId, replay.Disposition.Status);
                    break;
                }

                var trackState = replay.State;
                var boardNow = trackState.Board;
                if (!replay.Disposition.CanPlay || replay.HasPendingSubmission ||
                    boardNow.WhiteToMove != weAreWhite) continue;

                if (provider is null)
                {
                    _engine ??= UciLichessEngine.FromEnvironment();
                    provider = await Task.Run(_engine.Open, ct).ConfigureAwait(false);
                    _onEngineIdentity?.Invoke(provider.Identity);
                    _log.LogInformation("game {Id}: engine {Engine} ({Name}, sha256 {Sha})", lichessGameId, _engine.Name,
                        provider.Identity.Via?.Name ?? provider.Identity.Name,
                        (provider.Identity.Via ?? provider.Identity).ExeSha256[..12]);
                }
                var position = ChessPosition.From(replay.InitialFen, replay.AcceptedMoves);
                // The Lichess clock in milliseconds; the provider takes the network overhead off before the engine sees it.
                var clock = new UciClock(wtime, btime, winc, binc);
                var decision = await Task.Run(() => provider.Choose(position, clock, ct), ct).ConfigureAwait(false);
                ChessMove mv = MoveGen.Legal(boardNow).FirstOrDefault(m => m.ToUci() == decision.Move.Text) is var legal
                    && legal.ToUci() == decision.Move.Text
                    ? legal
                    : throw new InvalidDataException($"engine move {decision.Move.Text} is not legal here");
                receipts.Add(decision.Analysis.Receipt);
                var final = decision.Analysis.Final;
                var plyAnalysis = new ChessLivePlyAnalysis(final?.Score?.SideToMoveCentipawns, final?.Depth ?? 0,
                    decision.Analysis.Receipt.Nodes ?? 0,
                    final?.Pv is { Count: > 0 } line ? line.Select(static m => m.Text).ToList() : [decision.Move.Text]);

                _log.LogDebug("game {Id}: play {Move} ({Engine}, depth {D}, score {S}cp, engine {Ms}ms)",
                    lichessGameId, mv.ToUci(), provider.Identity.Via?.Name ?? provider.Identity.Name, plyAnalysis.Depth, plyAnalysis.ScoreCpSideToMove,
                    decision.Analysis.Receipt.EngineMs);

                var pending = replay.BeginSubmission(mv, plyAnalysis);
                var submission = await PostAsync($"/api/bot/game/{lichessGameId}/move/{mv.ToUci()}", ct);
                replay.ResolveSubmission(pending, submission);
                // Even HTTP success is only submission evidence. A later gameState
                // must confirm the exact ply before it reaches RecordPlyAsync.

            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "game {Id} stream ended early", lichessGameId); }
        finally
        {
            provider?.Dispose();
            if (provider is not null) WriteEngineReceipt(lichessGameId, provider.Identity, receipts);
        }

        if (_record && outcome is { } gameOutcome)
        {
            try
            {
                await _host.CompleteGameAsync(substrateGameId, gameOutcome, adjudicated: false, ct);
                _log.LogInformation("game {Id} recorded ({Plies} plies, {Result})",
                    lichessGameId, replay!.AcceptedPlies, Describe(gameOutcome));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "game {Id} substrate complete failed", lichessGameId);
            }
        }

        _log.LogInformation("game {Id} finished", lichessGameId);
    }

    private Hash128? PlayerIdForSide(int moverSide, bool weAreWhite)
    {
        bool botMove = (moverSide == 0) == weAreWhite;
        if (botMove) return ChessVocabulary.LaplacePlayerId;
        return null;
    }

    private static string? ReadPlayerName(JsonElement game, string side)
    {
        if (!game.TryGetProperty(side, out var player)) return null;
        foreach (var key in new[] { "name", "username", "id" })
        {
            if (player.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    internal static int? ReadPlayerRating(JsonElement game, string side)
    {
        if (!game.TryGetProperty(side, out var player)
            || !player.TryGetProperty("rating", out var rating)
            || rating.ValueKind != JsonValueKind.Number
            || !rating.TryGetInt32(out int value)
            || value <= 0)
            return null;
        return value;
    }

    internal static string? ReadCreatedDate(JsonElement game)
    {
        if (!game.TryGetProperty("createdAt", out var created)
            || created.ValueKind != JsonValueKind.Number
            || !created.TryGetInt64(out long millis))
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis)
                .ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string? ReadTimeControl(JsonElement game)
    {
        if (!game.TryGetProperty("clock", out var clock) || clock.ValueKind != JsonValueKind.Object)
            return null;
        if (!clock.TryGetProperty("initial", out var initialEl)
            || !initialEl.TryGetInt32(out int initialMs)
            || initialMs < 0)
            return null;
        int incrementMs = clock.TryGetProperty("increment", out var incEl)
            && incEl.TryGetInt32(out int inc) ? Math.Max(0, inc) : 0;
        return $"{initialMs / 1000}+{incrementMs / 1000}";
    }

    internal static string? ReadSpeedClass(JsonElement game)
    {
        if (!game.TryGetProperty("speed", out var speed) || speed.ValueKind != JsonValueKind.String)
            return null;
        return speed.GetString() switch
        {
            "ultraBullet" or "bullet" => "bullet",
            "blitz" => "blitz",
            "rapid" => "rapid",
            "classical" or "correspondence" => "classical",
            _ => null,
        };
    }

    public async Task PostChatAsync(string lichessGameId, string room, string text, CancellationToken ct = default)
    {
        text = ChessMoveCommentary.Truncate(text, ChessMoveCommentary.LichessMaxChars);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["room"] = room,
                ["text"] = text,
            });
            using var resp = await _http.PostAsync($"/api/bot/game/{lichessGameId}/chat", form, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("POST chat {Id} → {Status}", lichessGameId, (int)resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "POST chat {Id} failed", lichessGameId);
        }
    }

    private bool ShouldAccept(JsonElement challenge)
    {
        if (!challenge.TryGetProperty("variant", out var v)
            || !v.TryGetProperty("key", out var vk) || vk.GetString() != "standard")
            return false;
        bool rated = challenge.TryGetProperty("rated", out var r) && r.ValueKind == JsonValueKind.True;
        return _policy.Accepts(SpeedOf(challenge), rated);
    }

    private static string SpeedOf(JsonElement challenge)
        => challenge.TryGetProperty("speed", out var s) ? s.GetString() ?? "" : "";

    private static string Describe(GameOutcome o)
        => o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    /// <summary>The game's engine receipt: which engine played (path, SHA-256, UCI id, and the engine behind
    /// laplace-uci) and each move's receipt, beside the recorded game (LAPLACE_LICHESS_RECEIPTS, default under the temp
    /// directory). Recording into the substrate stays engine-agnostic.</summary>
    private void WriteEngineReceipt(string lichessGameId, EngineIdentity engine, IReadOnlyList<EngineReceipt> moves)
    {
        try
        {
            string dir = LaplaceInstall.TryReadConfig("LAPLACE_LICHESS_RECEIPTS")
                ?? Path.Combine(Path.GetTempPath(), "laplace-lichess", "receipts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, lichessGameId + ".json"), JsonSerializer.Serialize(new
            {
                game = $"lichess:{lichessGameId}",
                engineName = _engine?.Name,
                engine,
                moves,
            }, new JsonSerializerOptions(UciJson.Options) { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "game {Id}: engine receipt not written", lichessGameId);
        }
    }
}
