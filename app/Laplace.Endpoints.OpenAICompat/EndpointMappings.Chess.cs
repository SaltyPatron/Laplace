using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// `/chess/*` — the turn-based chess modality over the live substrate. Sits OUTSIDE the `/v1`
/// rate-limit/billing gates (local play + training). Thin over <see cref="ChessEngineService"/>.
/// </summary>
internal static class ChessEndpoints
{
    public static void MapChessEndpoints(this WebApplication app)
    {
        app.MapGet("/chess/new", (ChessEngineService svc) =>
            Results.Json(new { fen = svc.NewGameFen() })).WithTags("chess");

        // Scored legal moves (eff_mu per candidate) — for play hints and analysis.
        app.MapPost("/chess/legal", async (FenRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.ScoreAsync(req.Fen, ct))).WithTags("chess");

        // Apply a (human) move; returns the new FEN + terminal status.
        app.MapPost("/chess/move", async (MoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.ApplyMoveAsync(req.Fen, req.Uci, ct))).WithTags("chess");

        // The bot's move from a FEN (temperature 0 = greedy best by eff_mu).
        app.MapPost("/chess/bestmove", async (BestMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.BestMoveAsync(req.Fen, req.Temperature ?? 0d, ct))).WithTags("chess");

        // Background self-play training controls + live status.
        app.MapPost("/chess/train/start", (double? temperature, double? weight, int? maxPlies, ChessEngineService svc) =>
            Results.Json(new { started = svc.StartTraining(temperature ?? 120d, weight ?? 0.5d, maxPlies ?? 400) }))
            .WithTags("chess");

        app.MapPost("/chess/train/stop", (ChessEngineService svc) =>
            Results.Json(new { stopped = svc.StopTraining() })).WithTags("chess");

        app.MapGet("/chess/train/status", (ChessEngineService svc) =>
            Results.Json(svc.Status())).WithTags("chess");
    }

    private sealed record FenRequest(string Fen);
    private sealed record MoveRequest(string Fen, string Uci);
    private sealed record BestMoveRequest(string Fen, double? Temperature);
}
