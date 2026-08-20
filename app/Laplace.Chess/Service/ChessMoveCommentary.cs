using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// Substrate-backed move commentary: eval line + motif + book EXPLAINS + recall/salient_facts
/// templates (no LLM).
/// </summary>
public static class ChessMoveCommentary
{
    public const int LichessMaxChars = 140;

    public sealed record Inputs(
        int ScoreCp,
        int Depth,
        IReadOnlyList<string> Pv,
        IReadOnlyList<string> Motifs,
        string? PositionSurface = null);

    public static async Task<string> BuildAsync(
        NpgsqlDataSource ds, Inputs input, CancellationToken ct = default, int maxChars = LichessMaxChars)
    {
        var parts = new List<string>(4);
        string evalLine = FormatEval(input.ScoreCp, input.Depth);
        if (evalLine.Length > 0) parts.Add(evalLine);

        string? motifLine = null;
        foreach (var motif in input.Motifs)
        {
            motifLine = await MotifLineAsync(ds, motif, ct);
            if (motifLine is not null) break;
        }
        if (motifLine is not null) parts.Add(motifLine);

        // The chess literature's judgment of this exact position, if a book attested one —
        // (text, EXPLAINS, position) edges deposited by ChessBookDecomposer.
        if (input.PositionSurface is { } surface
            && await BookLineAsync(ds, surface, ct) is { } bookLine)
            parts.Add(bookLine);

        if (input.Pv.Count > 0)
            parts.Add($"PV {string.Join(' ', input.Pv.Take(3))}");

        return Truncate(string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), maxChars);
    }

    private static readonly Hash128 ExplainsRelation = RelationTypeRegistry.RelationTypeId("EXPLAINS");

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

    private static async Task<string?> MotifLineAsync(NpgsqlDataSource ds, string motif, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var recall = await NpgsqlSubstrateReads.RecallAsync(conn, $"define {motif}", ct);
        if (recall.Count > 0 && !string.IsNullOrWhiteSpace(recall[0].Reply))
            return $"{Label(motif)}: {Truncate(recall[0].Reply, 60)}";

        var traj = await NpgsqlSubstrateReads.RecallTrajectoryAnswerAsync(ds, motif, 2, ct);
        if (!string.IsNullOrWhiteSpace(traj))
            return $"{Label(motif)} — {Truncate(traj, 60)}";

        var fact = await NpgsqlSubstrateReads.SalientFactForWordAsync(ds, motif, 3, ct);
        if (!string.IsNullOrWhiteSpace(fact))
            return $"{Label(motif)} — {Truncate(fact, 60)}";

        return inputMotifOnly(motif);
    }

    private static string? inputMotifOnly(string motif) => motif switch
    {
        "fork" => "Fork — two targets at once",
        "discovered_check" => "Discovered check",
        "hanging_piece_won" => "Won material",
        _ => Label(motif),
    };

    private static string Label(string motif) => motif switch
    {
        "fork" => "Fork",
        "discovered_check" => "Discovered check",
        "hanging_piece_won" => "Material win",
        _ => motif.Replace('_', ' '),
    };
}
