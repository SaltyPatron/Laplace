using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record ChessPlayerModelExportRequest(
    IReadOnlyList<string> Players,
    string EvidenceBoundary);

internal sealed record ChessPlayerModelMatchRequest(
    IReadOnlyList<string> White,
    IReadOnlyList<string> Black,
    string EvidenceBoundary,
    int? Depth = null,
    int? MaxPlies = null,
    string? StartFen = null);

internal static class ChessPlayerModelEndpoints
{
    public static void MapChessPlayerModelEndpoints(this WebApplication app)
    {
        app.MapPost("/chess/player-model/export", (ChessPlayerModelExportRequest req) =>
        {
            if (!ValidPlayers(req.Players, out string? error))
                return Results.BadRequest(new { error });
            if (string.IsNullOrWhiteSpace(req.EvidenceBoundary))
                return Results.BadRequest(new
                {
                    error = "evidence_boundary_required",
                    message = "A player-model export must name the substrate evidence boundary it freezes."
                });

            var export = ChessPlayerModelExport.ForNames(req.Players, req.EvidenceBoundary);
            return Results.Json(new
            {
                id = Hex(export.Id),
                memberSetId = Hex(export.MemberSetId),
                evidenceBoundary = export.EvidenceBoundary,
                members = export.Members.Select(Hex).ToArray(),
                manifest = export.ToJson(),
            });
        }).WithTags("chess");

        app.MapPost("/chess/player-model/match", async (
            ChessPlayerModelMatchRequest req,
            SubstrateClient substrate,
            CancellationToken ct) =>
        {
            if (!ValidPlayers(req.White, out string? whiteError))
                return Results.BadRequest(new { error = $"white: {whiteError}" });
            if (!ValidPlayers(req.Black, out string? blackError))
                return Results.BadRequest(new { error = $"black: {blackError}" });
            if (string.IsNullOrWhiteSpace(req.EvidenceBoundary))
                return Results.BadRequest(new
                {
                    error = "evidence_boundary_required",
                    message = "Both sides must be realized against the same declared evidence boundary."
                });

            var whiteExport = ChessPlayerModelExport.ForNames(req.White, req.EvidenceBoundary);
            var blackExport = ChessPlayerModelExport.ForNames(req.Black, req.EvidenceBoundary);
            var white = new ChessPlayerModelRuntime(substrate.DataSource, whiteExport);
            var black = new ChessPlayerModelRuntime(substrate.DataSource, blackExport);

            // Search is CPU-bound and synchronous by design. Keep it off the ASP.NET request
            // execution thread while still flowing cancellation into every move search.
            var result = await Task.Run(
                () => ChessPlayerModelMatch.Play(
                    white,
                    black,
                    depth: Math.Clamp(req.Depth ?? 4, 1, 12),
                    maxPlies: Math.Clamp(req.MaxPlies ?? 400, 1, 2_000),
                    startFen: req.StartFen,
                    ct),
                ct).ConfigureAwait(false);

            return Results.Json(result);
        }).WithTags("chess");
    }

    private static string Hex(Laplace.Engine.Core.Hash128 id)
        => Convert.ToHexStringLower(id.ToBytes());

    private static bool ValidPlayers(IReadOnlyList<string>? players, out string? error)
    {
        if (players is null || players.Count == 0)
        {
            error = "at least one player is required";
            return false;
        }
        if (players.Count > 32)
        {
            error = "a player-model export is limited to 32 selected members";
            return false;
        }
        if (players.Any(static p => string.IsNullOrWhiteSpace(p)))
        {
            error = "player names must be non-empty";
            return false;
        }
        error = null;
        return true;
    }
}
