using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Substrate-backed move commentary. The commentary is grounded in the exact chess position:
/// historical playings that contained it, chess-book testimony about it, the detected chess
/// motif, and the search that actually chose the move. No generic lexical recall is used for
/// motif names, so a chess fork can never drift into the culinary sense of "fork".
/// </summary>
public static class ChessMoveCommentary
{
    public const int LichessMaxChars = 140;

    public sealed record Inputs(
        int ScoreCp,
        int Depth,
        IReadOnlyList<string> Pv,
        IReadOnlyList<string> Motifs,
        string? PositionSurface = null,
        string? PlayedSan = null);

    public static async Task<string> BuildAsync(
        NpgsqlDataSource ds, Inputs input, CancellationToken ct = default, int maxChars = LichessMaxChars)
    {
        var parts = new List<string>(5);

        // Position history is the most distinctive substrate observation, so give it the scarce
        // Lichess chat budget before generic engine telemetry.
        if (input.PositionSurface is { } surface
            && await HistoricalPositionLineAsync(ds, surface, input.PlayedSan, ct) is { } history)
            parts.Add(history);

        if (input.Motifs.FirstOrDefault() is { } motif)
            parts.Add($"Chess motif: {MotifLabel(motif)}");

        // The chess literature's judgment of this exact position, if a book attested one —
        // (text, EXPLAINS, position) edges deposited by ChessBookDecomposer.
        if (input.PositionSurface is { } bookSurface
            && await BookLineAsync(ds, bookSurface, ct) is { } bookLine)
            parts.Add(bookLine);

        string evalLine = FormatEval(input.ScoreCp, input.Depth);
        if (evalLine.Length > 0) parts.Add(evalLine);

        if (input.Pv.Count > 0)
            parts.Add($"PV {string.Join(' ', input.Pv.Take(3))}");

        return Truncate(string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), maxChars);
    }

    private static readonly Hash128 ExplainsRelation = RelationTypeRegistry.RelationTypeId("EXPLAINS");

    private static async Task<string?> HistoricalPositionLineAsync(
        NpgsqlDataSource ds, string positionSurface, string? playedSan, CancellationToken ct)
    {
        try
        {
            if (!PositionContent.TryFenFromSurface(positionSurface, out var fen)) return null;
            var board = Board.FromFen(fen);
            Hash128 positionId;
            lock (ChessCompose.Gate) { positionId = ChessCompose.PositionId(board); }

            var history = await NpgsqlChessCommentaryReads.PositionHistoryAsync(
                ds,
                positionId.ToBytes(),
                ChessVocabulary.GameType.ToBytes(),
                containerLimit: 64,
                limit: 12,
                ct).ConfigureAwait(false);
            if (history.Count == 0) return null;

            var distinctLines = history
                .Select(static h => Hash128.FromBytes(h.LineId))
                .Distinct()
                .Select(static id => id.ToBytes())
                .ToArray();
            var projection = await NpgsqlSubstrateReads.TrajectoryConstituentsAsync(
                ds, distinctLines, PhysicalityType.Projection, ct).ConfigureAwait(false);

            var byLine = projection
                .GroupBy(static p => Hash128.FromBytes(p.ParentId))
                .ToDictionary(
                    static g => g.Key,
                    static g => g.OrderBy(static p => p.Ordinal)
                        .Select(static p => Hash128.FromBytes(p.EntityId)).ToArray());

            var nextMoves = LegalSuccessors(board);
            foreach (var row in history)
            {
                string mover = board.WhiteToMove ? row.White : row.Black;
                if (string.IsNullOrWhiteSpace(mover)) continue;

                string? nextSan = null;
                var lineId = Hash128.FromBytes(row.LineId);
                if (byLine.TryGetValue(lineId, out var positions))
                {
                    for (int i = 0; i + 1 < positions.Length; i++)
                    {
                        if (positions[i] != positionId) continue;
                        if (nextMoves.TryGetValue(positions[i + 1], out var san))
                            nextSan = san;
                        break;
                    }
                }

                // Prefer a dated witness for the human-facing historical sentence. If an
                // undated playing is the only witness it remains in the substrate, but it does
                // not pretend to provide a year it never asserted.
                if (Year(row.PlayedOn) is null) continue;
                return FormatHistorical(row.PlayedOn, mover, nextSan, playedSan);
            }
        }
        catch
        {
            // Commentary is decoration; a failed historical lookup must never affect play.
        }
        return null;
    }

    private static Dictionary<Hash128, string> LegalSuccessors(Board board)
    {
        var result = new Dictionary<Hash128, string>();
        foreach (var move in MoveGen.Legal(board))
        {
            string san = San.ToSan(board, move);
            var next = board.Clone();
            MoveApply.Make(next, move);
            result[ChessCompose.PositionId(next)] = san;
        }
        return result;
    }

    private static async Task<string?> BookLineAsync(
        NpgsqlDataSource ds, string positionSurface, CancellationToken ct)
    {
        try
        {
            Hash128 posId;
            lock (ChessCompose.Gate) { posId = ChessCompose.PositionId(positionSurface); }

            var subject = await NpgsqlSubstrateReads.FirstAttestationSubjectAsync(
                ds, posId.ToBytes(), ExplainsRelation.ToBytes(), ct);
            if (subject is null) return null;

            var rendered = await NpgsqlSubstrateReads.RenderTextBatchAsync(
                ds, [subject], ct);
            if (rendered is { Length: > 0 } texts && !string.IsNullOrWhiteSpace(texts[0]))
                return $"Book: {Truncate(texts[0], 70)}";
        }
        catch
        {
            // Commentary is decoration; a failed lookup must never affect play.
        }
        return null;
    }

    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        if (text.Length <= maxChars) return text;
        if (maxChars <= 1) return text[..maxChars];
        return text[..(maxChars - 1)] + "…";
    }

    private static string FormatEval(int scoreCp, int depth)
    {
        if (depth <= 0) return "";
        if (Math.Abs(scoreCp) >= 29_000)
            return scoreCp > 0 ? "Mating" : "Getting mated";
        double pawns = scoreCp / 100.0;
        string sign = pawns >= 0 ? "+" : "";
        return $"Eval {sign}{pawns:0.0} (d{depth})";
    }

    internal static string MotifLabel(string motif) => motif switch
    {
        "fork" => "Fork",
        "discovered_check" => "Discovered check",
        "hanging_piece_won" => "Material win",
        _ => motif.Replace('_', ' '),
    };

    internal static string? FormatHistorical(
        string? playedOn, string mover, string? historicalSan, string? playedSan)
    {
        string? year = Year(playedOn);
        if (year is null || string.IsNullOrWhiteSpace(mover)) return null;

        if (!string.IsNullOrWhiteSpace(historicalSan))
        {
            if (!string.IsNullOrWhiteSpace(playedSan)
                && !string.Equals(historicalSan, playedSan, StringComparison.Ordinal))
                return $"{year}: {mover} had this exact position and played {historicalSan}, not {playedSan}.";
            return $"{year}: {mover} had this exact position and also played {historicalSan}.";
        }

        return $"{year}: {mover} also reached this exact position.";
    }

    private static string? Year(string? playedOn)
    {
        if (string.IsNullOrWhiteSpace(playedOn) || playedOn.Length < 4) return null;
        return playedOn.Take(4).All(char.IsDigit) ? playedOn[..4] : null;
    }
}
